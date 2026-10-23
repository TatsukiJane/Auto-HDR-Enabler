using AutoHdr.Library;

namespace AutoHdr.Core;

/// <summary>
/// Ties games, displays and processes together: turns HDR on when a game with auto HDR starts
/// and restores the previous mode when all game processes have exited and the delay has passed.
/// HDR that the user turned on manually is never turned off by the app.
/// </summary>
public sealed class AutoHdrEngine
{
    static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(6);

    readonly GameStore games;
    readonly MonitorRegistry monitors;
    readonly AppSettings settings;
    readonly object gate = new();
    readonly Dictionary<uint, GameRecord> tracked = [];
    readonly SemaphoreSlim switchLock = new(1, 1);
    CancellationTokenSource? graceCts;

    public AutoHdrEngine(GameStore games, MonitorRegistry monitors, AppSettings settings)
    {
        this.games = games;
        this.monitors = monitors;
        this.settings = settings;

        // The display came back under another device path (another port, driver, reconnect): PendingRevert follows it
        monitors.KeyChanged += (oldKey, newKey) =>
        {
            lock (gate)
            {
                int i = settings.PendingRevert.IndexOf(oldKey);
                if (i < 0) return;
                settings.PendingRevert[i] = newKey;
            }
            settings.Save();
        };
    }

    /// <summary>Notification title and text.</summary>
    public event Action<string, string>? Notify;
    public event Action? StateChanged;

    public IReadOnlyList<string> RunningGames
    {
        get { lock (gate) return tracked.Values.Select(g => g.Name).Distinct().ToList(); }
    }

    public void OnStarted(uint pid, string path)
    {
        if (settings.Paused) return;
        var game = games.Match(path);
        if (game == null || !game.Enabled) return;

        bool firstProcess;
        lock (gate)
        {
            firstProcess = !tracked.ContainsValue(game);
            tracked[pid] = game;
            graceCts?.Cancel();
            graceCts = null;
        }

        if (firstProcess) Log.Info(Loc.T($"Запущена игра «{game.Name}»: {Path.GetFileName(path)}, PID {pid}", $"Game started “{game.Name}”: {Path.GetFileName(path)}, PID {pid}"));
        _ = Task.Run(() => EnsureHdrAsync(game, pid));
    }

    public void OnExited(uint pid)
    {
        GameRecord? game;
        bool gameClosed;
        lock (gate)
        {
            if (!tracked.Remove(pid, out game)) return;
            gameClosed = !tracked.ContainsValue(game);
            // Under gate: OnDisplaysChanged must not restore SDR bypassing the delay
            if (tracked.Count == 0) ScheduleRevert();
        }

        if (gameClosed) Log.Info(Loc.T($"Игра «{game.Name}» закрыта", $"Game closed “{game.Name}”"));
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Called on every display poll. If no games run, no revert is scheduled and a display from PendingRevert
    /// is connected, restores the previous mode. This turns HDR off after an app crash, after a game that exited
    /// while HDR was still turning on, and on a display that was missing from the Windows list when the game exited.
    /// </summary>
    public void OnDisplaysChanged(IReadOnlyList<DisplayInfo> displays)
    {
        lock (gate)
        {
            if (tracked.Count > 0 || graceCts != null || !displays.Any(d => settings.PendingRevert.Contains(d.Key))) return;
            ScheduleRevert();
        }
        Log.Info(Loc.T("HDR, включённый для игры, ещё не выключен: возвращается прежний режим", "HDR turned on for a game is still on: restoring the previous mode"));
    }

    public bool HasPendingRevert
    {
        get { lock (gate) return settings.PendingRevert.Count > 0 && tracked.Count == 0; }
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

    async Task EnsureHdrAsync(GameRecord game, uint pid)
    {
        bool suspended = false;
        try
        {
            if (game.HoldUntilHdr && NeedsSwitch())
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
                    if (!tracked.ContainsKey(pid)) return; // the process exited while waiting in the queue: it no longer needs HDR
                }

                var displays = HdrControl.GetDisplays();
                monitors.Sync(displays);
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
            finally
            {
                switchLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T($"Не удалось включить HDR для «{game.Name}»", $"Could not turn HDR on for “{game.Name}”"), ex);
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

    bool NeedsSwitch() => monitors.Targets(HdrControl.GetDisplays()).Any(d => !d.HdrOn);

    void ScheduleRevert()
    {
        CancellationTokenSource cts;
        lock (gate)
        {
            if (settings.PendingRevert.Count == 0) return;
            if (!settings.RestoreAfterExit)
            {
                settings.PendingRevert.Clear();
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
            lock (gate)
            {
                if (tracked.Count > 0) return;
                keys = settings.PendingRevert.ToList();
            }
            if (keys.Count == 0) return;

            var displays = HdrControl.GetDisplays();
            var toDisable = displays.Where(d => keys.Contains(d.Key) && d.HdrOn).ToList();
            foreach (var display in toDisable) HdrControl.SetHdr(display, false);
            if (toDisable.Count > 0)
                await HdrControl.WaitForStateAsync(toDisable.Select(d => d.Key).ToList(), false, ConfirmTimeout);

            // A display that is missing now (turned off, or Windows did not answer) stays in the list until OnDisplaysChanged
            lock (gate) settings.PendingRevert.RemoveAll(k => displays.Any(d => d.Key == k));
            settings.Save();

            if (toDisable.Count > 0)
            {
                var names = string.Join(", ", toDisable.Select(d => d.Name));
                Log.Info(Loc.T($"HDR выключен на: {names}", $"HDR turned off on: {names}"));
                Notify?.Invoke(Loc.T("HDR выключен", "HDR off"), Loc.T("Игра закрыта", "Game closed"));
            }
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("Не удалось вернуть прежний режим HDR", "Could not restore the previous HDR mode"), ex);
        }
        finally
        {
            switchLock.Release();
            StateChanged?.Invoke();
        }
    }
}
