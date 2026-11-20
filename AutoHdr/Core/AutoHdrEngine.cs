using AutoHdr.Library;

namespace AutoHdr.Core;

/// <summary>
/// Ties games, displays and processes together: turns HDR on when a game with auto HDR starts,
/// switches the monitor picture mode for a game with “Monitor mode”, and restores the previous state
/// when all such processes have exited and the delay has passed. HDR and the picture mode are restored separately:
/// HDR when no auto HDR game runs, the picture mode when no “Monitor mode” game runs.
/// HDR or a picture mode that the user set manually is never switched back by the app.
/// </summary>
public sealed class AutoHdrEngine
{
    static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(6);

    /// <summary>A tracked process and what its game asked for when it started.</summary>
    sealed record Tracked(GameRecord Game, bool Hdr, bool Preset);

    readonly GameStore games;
    readonly MonitorRegistry monitors;
    readonly AppSettings settings;
    readonly object gate = new();
    readonly Dictionary<uint, Tracked> tracked = [];
    readonly SemaphoreSlim switchLock = new(1, 1);
    CancellationTokenSource? graceCts;

    public AutoHdrEngine(GameStore games, MonitorRegistry monitors, AppSettings settings)
    {
        this.games = games;
        this.monitors = monitors;
        this.settings = settings;

        // The display came back under another device path (another port, driver, reconnect): pending reverts follow it
        monitors.KeyChanged += (oldKey, newKey) =>
        {
            lock (gate)
            {
                int i = settings.PendingRevert.IndexOf(oldKey);
                if (i >= 0) settings.PendingRevert[i] = newKey;
                if (settings.PendingPresets.Remove(oldKey, out int preset)) settings.PendingPresets[newKey] = preset;
                if (i < 0 && !settings.PendingPresets.ContainsKey(newKey)) return;
            }
            settings.Save();
        };
    }

    /// <summary>Notification title and text.</summary>
    public event Action<string, string>? Notify;
    public event Action? StateChanged;

    public IReadOnlyList<string> RunningGames
    {
        get { lock (gate) return tracked.Values.Select(t => t.Game.Name).Distinct().ToList(); }
    }

    public void OnStarted(uint pid, string path)
    {
        if (settings.Paused) return;
        var game = games.Match(path);
        if (game == null || !(game.Enabled || game.MonitorMode)) return;

        var entry = new Tracked(game, game.Enabled, game.MonitorMode);
        bool firstProcess;
        lock (gate)
        {
            firstProcess = !tracked.Values.Any(t => t.Game == game);
            tracked[pid] = entry;
            // A scheduled revert of what this game needs is cancelled; RevertAsync skips it anyway, this only avoids a useless wake-up
            if ((entry.Hdr && settings.PendingRevert.Count > 0) || (entry.Preset && settings.PendingPresets.Count > 0))
            {
                graceCts?.Cancel();
                graceCts = null;
            }
        }

        if (firstProcess) Log.Info(Loc.T($"Запущена игра «{game.Name}»: {Path.GetFileName(path)}, PID {pid}", $"Game started “{game.Name}”: {Path.GetFileName(path)}, PID {pid}"));
        _ = Task.Run(() => EnsureAsync(entry, pid));
    }

    public void OnExited(uint pid)
    {
        Tracked? entry;
        bool gameClosed;
        lock (gate)
        {
            if (!tracked.Remove(pid, out entry)) return;
            gameClosed = !tracked.Values.Any(t => t.Game == entry.Game);
            // Under gate: OnDisplaysChanged must not restore bypassing the delay
            if (RevertibleLocked()) ScheduleRevert();
        }

        if (gameClosed) Log.Info(Loc.T($"Игра «{entry.Game.Name}» закрыта", $"Game closed “{entry.Game.Name}”"));
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Called on every display poll. If nothing that needs it runs, no revert is scheduled and a display from PendingRevert
    /// or PendingPresets is connected, restores the previous state. This turns HDR off and brings the picture mode back after an app crash,
    /// after a game that exited while HDR was still turning on, and on a display that was missing from the Windows list when the game exited.
    /// </summary>
    public void OnDisplaysChanged(IReadOnlyList<DisplayInfo> displays)
    {
        bool hdr, preset;
        lock (gate)
        {
            if (graceCts != null) return;
            hdr = !HdrGamesLocked() && displays.Any(d => settings.PendingRevert.Contains(d.Key));
            preset = !PresetGamesLocked() && displays.Any(d => settings.PendingPresets.ContainsKey(d.Key));
            if (!hdr && !preset) return;
            ScheduleRevert();
        }
        if (hdr) Log.Info(Loc.T("HDR, включённый для игры, ещё не выключен: возвращается прежний режим", "HDR turned on for a game is still on: restoring the previous mode"));
        if (preset) Log.Info(Loc.T("Режим монитора, включённый для игры, ещё не возвращён: возвращается прежний", "The monitor picture mode set for a game is still on: restoring the previous one"));
    }

    public bool HasPendingRevert
    {
        get { lock (gate) return RevertibleLocked(); }
    }

    /// <summary>The user turned HDR off manually: nothing left to restore.</summary>
    public void ForgetPending(IEnumerable<string> keys)
    {
        lock (gate)
        {
            if (settings.PendingRevert.RemoveAll(k => keys.Contains(k)) == 0) return;
        }
        settings.Save();
    }

    bool HdrGamesLocked() => tracked.Values.Any(t => t.Hdr);
    bool PresetGamesLocked() => tracked.Values.Any(t => t.Preset);

    bool RevertibleLocked() =>
        (settings.PendingRevert.Count > 0 && !HdrGamesLocked()) || (settings.PendingPresets.Count > 0 && !PresetGamesLocked());

    async Task EnsureAsync(Tracked entry, uint pid)
    {
        var game = entry.Game;
        bool suspended = false;
        try
        {
            if (entry.Hdr && game.HoldUntilHdr && NeedsSwitch())
            {
                suspended = ProcessUtil.Suspend(pid);
                Log.Info(suspended
                    ? Loc.T($"«{game.Name}» приостановлена до включения HDR", $"“{game.Name}” is on hold until HDR is on")
                    : Loc.T($"«{game.Name}» не приостановлена: нет прав или процесс запущен другим пользователем",
                        $"“{game.Name}” is not on hold: no rights, or the process belongs to another user"));
            }

            await switchLock.WaitAsync();
            try
            {
                lock (gate)
                {
                    if (!tracked.ContainsKey(pid)) return; // the process exited while waiting in the queue: it no longer needs anything
                }

                var displays = HdrControl.GetDisplays();
                monitors.Sync(displays);
                // The picture mode first: the monitor keeps it in HDR too, and HDR blanks the screen for a few seconds
                // A mode that changes the resolution resyncs the signal: HDR goes on the display list Windows has after it
                if (entry.Preset && ApplyPresets(game, displays)) displays = HdrControl.GetDisplays();
                if (entry.Hdr) await EnableHdrAsync(game, displays);
            }
            finally
            {
                switchLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T($"Не удалось включить HDR или режим монитора для «{game.Name}»", $"Could not turn on HDR or the monitor mode for “{game.Name}”"), ex);
        }
        finally
        {
            if (suspended)
            {
                ProcessUtil.Resume(pid);
                Log.Info(Loc.T($"«{game.Name}» продолжает запуск", $"“{game.Name}” continues launching"));
            }
            StateChanged?.Invoke();
        }
    }

    /// <summary>Switches the monitors to their game modes. True if any monitor was switched.</summary>
    bool ApplyPresets(GameRecord game, IReadOnlyList<DisplayInfo> displays)
    {
        bool switched = false;
        foreach (var (display, code, preset) in monitors.GamePresets(displays))
        {
            // A short read: a monitor without DDC/CI must not delay HDR by seconds
            var current = MonitorPreset.Read(display.Key, code);
            if (current == null)
            {
                Log.Warn(Loc.T($"«{display.Name}» не ответил по DDC/CI: режим монитора не переключён", $"“{display.Name}” did not answer over DDC/CI: the monitor mode was not switched"));
                continue;
            }
            if (current == preset) continue; // already in the game mode: set manually or by another game, nothing to restore

            if (!MonitorPreset.Write(display.Key, code, preset, ConfirmTimeout))
            {
                Log.Warn(Loc.T($"Не удалось включить режим монитора {preset} на «{display.Name}»", $"Could not switch “{display.Name}” to monitor mode {preset}"));
                continue;
            }
            lock (gate) settings.PendingPresets.TryAdd(display.Key, current.Value);
            settings.Save();
            switched = true;
            Log.Info(Loc.T($"Режим монитора {preset} включён для «{game.Name}» на «{display.Name}», прежний режим {current}",
                $"Monitor mode {preset} on for “{game.Name}” on “{display.Name}”, previous mode {current}"));
        }
        return switched;
    }

    async Task EnableHdrAsync(GameRecord game, IReadOnlyList<DisplayInfo> displays)
    {
        var toEnable = monitors.Targets(displays).Where(d => !d.HdrOn).ToList();
        if (toEnable.Count == 0) return;

        var switched = new List<string>();
        foreach (var display in toEnable)
        {
            if (!HdrControl.SetHdr(display, true)) continue;
            switched.Add(display.Key);
            lock (gate)
            {
                if (!settings.PendingRevert.Contains(display.Key)) settings.PendingRevert.Add(display.Key);
            }
        }
        settings.Save();
        if (switched.Count == 0) return;

        bool confirmed = await HdrControl.WaitForStateAsync(switched, true, ConfirmTimeout);
        var names = string.Join(", ", toEnable.Where(d => switched.Contains(d.Key)).Select(d => d.Name));
        if (confirmed) Log.Info(Loc.T($"HDR включён для «{game.Name}» на: {names}", $"HDR turned on for “{game.Name}” on: {names}"));
        else Log.Warn(Loc.T($"HDR на {names} не подтвердился за {ConfirmTimeout.TotalSeconds:0} с", $"HDR on {names} not confirmed within {ConfirmTimeout.TotalSeconds:0} s"));
        Notify?.Invoke(Loc.T("HDR включён", "HDR on"), game.Name);
    }

    bool NeedsSwitch() => monitors.Targets(HdrControl.GetDisplays()).Any(d => !d.HdrOn);

    void ScheduleRevert()
    {
        CancellationTokenSource cts;
        lock (gate)
        {
            if (!RevertibleLocked()) return;
            if (!settings.RestoreAfterExit)
            {
                settings.PendingRevert.Clear();
                settings.PendingPresets.Clear();
                settings.Save();
                return;
            }
            graceCts?.Cancel();
            cts = graceCts = new CancellationTokenSource();
        }

        var delay = TimeSpan.FromSeconds(settings.ExitDelay ? Math.Clamp(settings.ExitDelaySec, 0, 600) : 0);
        _ = Task.Delay(delay, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            await RevertAsync();
            lock (gate)
            {
                if (graceCts == cts) graceCts = null;
            }
        }, TaskScheduler.Default);
    }

    public async Task RevertAsync()
    {
        await switchLock.WaitAsync();
        try
        {
            List<string> keys;
            Dictionary<string, int> presets;
            lock (gate)
            {
                keys = HdrGamesLocked() ? [] : settings.PendingRevert.ToList();
                presets = PresetGamesLocked() ? [] : new(settings.PendingPresets);
            }
            if (keys.Count == 0 && presets.Count == 0) return;

            var displays = HdrControl.GetDisplays();
            if (keys.Count > 0) await RevertHdrAsync(keys, displays);
            if (presets.Count > 0) RevertPresets(presets, displays);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("Не удалось вернуть прежний режим", "Could not restore the previous mode"), ex);
        }
        finally
        {
            switchLock.Release();
            StateChanged?.Invoke();
        }
    }

    async Task RevertHdrAsync(List<string> keys, IReadOnlyList<DisplayInfo> displays)
    {
        var toDisable = displays.Where(d => keys.Contains(d.Key) && d.HdrOn).ToList();
        foreach (var display in toDisable) HdrControl.SetHdr(display, false);
        if (toDisable.Count > 0)
            await HdrControl.WaitForStateAsync(toDisable.Select(d => d.Key).ToList(), false, ConfirmTimeout);

        // A display that is missing now (turned off, or Windows did not answer) stays in the list until OnDisplaysChanged
        lock (gate) settings.PendingRevert.RemoveAll(k => keys.Contains(k) && displays.Any(d => d.Key == k));
        settings.Save();

        if (toDisable.Count > 0)
        {
            var names = string.Join(", ", toDisable.Select(d => d.Name));
            Log.Info(Loc.T($"HDR выключен на: {names}", $"HDR turned off on: {names}"));
            Notify?.Invoke(Loc.T("HDR выключен", "HDR off"), Loc.T("Игра закрыта", "Game closed"));
        }
    }

    void RevertPresets(Dictionary<string, int> presets, IReadOnlyList<DisplayInfo> displays)
    {
        var gamePresets = monitors.GamePresets(displays).ToDictionary(p => p.Display.Key);
        foreach (var (key, previous) in presets)
        {
            var display = displays.FirstOrDefault(d => d.Key == key);
            if (display == null) continue; // not connected now: stays in the list until it comes back

            bool hasMode = gamePresets.TryGetValue(key, out var mode);
            var current = hasMode ? MonitorPreset.Read(key, mode.Code, ConfirmTimeout) : null;
            if (!hasMode)
                Log.Info(Loc.T($"На «{display.Name}» режим для игр больше не настроен: режим монитора не возвращается", $"No game mode is set up on “{display.Name}” any more: the monitor mode is not restored"));
            else if (current == null)
                Log.Warn(Loc.T($"«{display.Name}» не ответил по DDC/CI: режим монитора {previous} не возвращён", $"“{display.Name}” did not answer over DDC/CI: monitor mode {previous} was not restored"));
            else if (current != mode.Preset)
                Log.Info(Loc.T($"На «{display.Name}» режим монитора сменили вручную ({current}): оставлен как есть", $"The monitor mode on “{display.Name}” was changed manually ({current}): left as is"));
            else if (MonitorPreset.Write(key, mode.Code, previous, ConfirmTimeout))
                Log.Info(Loc.T($"Режим монитора {previous} возвращён на «{display.Name}»", $"Monitor mode {previous} restored on “{display.Name}”"));
            else
                Log.Warn(Loc.T($"Не удалось вернуть режим монитора {previous} на «{display.Name}»", $"Could not restore monitor mode {previous} on “{display.Name}”"));

            // One attempt only: a monitor that does not answer would otherwise be asked every 3 seconds
            lock (gate) settings.PendingPresets.Remove(key);
        }
        settings.Save();
    }
}
