using AutoHdr.Library;

namespace AutoHdr.Core;

/// <summary>
/// Связывает игры, мониторы и процессы: включает HDR при запуске игры с авто-HDR
/// и возвращает прежний режим, когда все процессы игр закрыты и прошла задержка.
/// HDR, включённый пользователем вручную, приложение не выключает.
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

        // Монитор вернулся под другим путём устройства (другой разъём, драйвер, переподключение): PendingRevert идёт за ним
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

    /// <summary>Заголовок и текст уведомления.</summary>
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

        if (firstProcess) Log.Info($"Запущена игра «{game.Name}»: {Path.GetFileName(path)}, PID {pid}");
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
            // Под gate: OnDisplaysChanged не должен вернуть SDR в обход задержки
            if (tracked.Count == 0) ScheduleRevert();
        }

        if (gameClosed) Log.Info($"Игра «{game.Name}» закрыта");
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Вызывается при каждом опросе мониторов. Если игр нет, возврат не запланирован, а монитор из PendingRevert
    /// подключён, возвращает прежний режим. Так HDR выключается после сбоя приложения, после игры, которая закрылась,
    /// пока HDR ещё включался, и на мониторе, которого не было в списке Windows в момент выхода из игры.
    /// </summary>
    public void OnDisplaysChanged(IReadOnlyList<DisplayInfo> displays)
    {
        lock (gate)
        {
            if (tracked.Count > 0 || graceCts != null || !displays.Any(d => settings.PendingRevert.Contains(d.Key))) return;
            ScheduleRevert();
        }
        Log.Info("HDR, включённый для игры, ещё не выключен: возвращается прежний режим");
    }

    public bool HasPendingRevert
    {
        get { lock (gate) return settings.PendingRevert.Count > 0 && tracked.Count == 0; }
    }

    /// <summary>Пользователь вручную выключил HDR: возвращать больше нечего.</summary>
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
                if (suspended) Log.Info($"«{game.Name}» приостановлена до включения HDR");
            }

            await switchLock.WaitAsync();
            try
            {
                lock (gate)
                {
                    if (!tracked.ContainsKey(pid)) return; // процесс закрылся, пока ждал очереди: HDR ему уже не нужен
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
                if (confirmed) Log.Info($"HDR включён для «{game.Name}» на: {names}");
                else Log.Warn($"HDR на {names} не подтвердился за {ConfirmTimeout.TotalSeconds:0} с");
                Notify?.Invoke("HDR включён", game.Name);
            }
            finally
            {
                switchLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось включить HDR для «{game.Name}»", ex);
        }
        finally
        {
            if (suspended)
            {
                ProcessUtil.Resume(pid);
                Log.Info($"«{game.Name}» продолжает запуск");
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

            // Монитор, которого сейчас нет (выключен, или Windows не ответила), остаётся в списке до OnDisplaysChanged
            lock (gate) settings.PendingRevert.RemoveAll(k => displays.Any(d => d.Key == k));
            settings.Save();

            if (toDisable.Count > 0)
            {
                Log.Info($"HDR выключен на: {string.Join(", ", toDisable.Select(d => d.Name))}");
                Notify?.Invoke("HDR выключен", "Игра закрыта");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось вернуть прежний режим HDR", ex);
        }
        finally
        {
            switchLock.Release();
            StateChanged?.Invoke();
        }
    }
}
