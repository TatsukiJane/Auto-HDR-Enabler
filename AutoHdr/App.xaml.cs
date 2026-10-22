using System.Windows;
using System.Windows.Threading;
using AutoHdr.Core;
using AutoHdr.HdrInfo;
using AutoHdr.Library;
using AutoHdr.UI;
using Microsoft.Win32;

namespace AutoHdr;

/// <summary>Строка состояния фоновых операций для окна.</summary>
public sealed class AppStatus : Observable
{
    public bool IsBusy { get; set => Set(ref field, value); }
    public string BusyText { get; set => Set(ref field, value); } = "";
    public double Progress { get; set => Set(ref field, value); }
    public bool Indeterminate { get; set => Set(ref field, value); }
}

public partial class App : Application
{
    static readonly TimeSpan HdrInfoTtl = TimeSpan.FromDays(30);

    readonly string[] args;
    Mutex? instanceMutex;
    EventWaitHandle? showEvent;
    EventWaitHandle? exitEvent;
    DispatcherTimer? stateTimer;
    TrayIcon? tray;
    MainWindow? window;
    bool scanning, hdrRefreshRunning, exiting;
    Task hdrRefresh = Task.CompletedTask;

    /// <summary>Нужен генератору App.g.cs; настоящая точка входа — Program.Main.</summary>
    public App() : this([])
    {
    }

    public App(string[] args)
    {
        this.args = args;
        ThemeMode = ThemeMode.System;
    }

    public AppSettings Settings { get; private set; } = null!;
    public MonitorRegistry Monitors { get; private set; } = null!;
    public GameStore Games { get; private set; } = null!;
    public AutoHdrEngine? Engine { get; private set; }
    public ProcessWatcher? Watcher { get; private set; }
    public HdrInfoService HdrInfo { get; } = new();
    public AppStatus Status { get; } = new();

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Необработанная ошибка", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Критическая ошибка", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Ошибка фоновой задачи", ex.Exception);
            ex.SetObserved();
        };

        StartupTrace.Mark("startup");
        var snapshotIndex = Array.FindIndex(args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (snapshotIndex < 0 && !AcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        StartupTrace.Mark("single instance ok");
        Settings = AppSettings.Load();
        Monitors = MonitorRegistry.Load();
        Games = GameStore.Load();
        Monitors.Sync(HdrControl.GetDisplays());
        GameRecord.UserChanged += _ => Games.SaveSoon();
        StartupTrace.Mark("data loaded");

        if (snapshotIndex >= 0)
        {
            var dir = snapshotIndex + 1 < args.Length ? args[snapshotIndex + 1] : AppPaths.DataDir;
            Snapshot.Render(this, dir);
            Shutdown(0);
            return;
        }

        Engine = new AutoHdrEngine(Games, Monitors, Settings);
        Engine.Notify += (title, text) => Dispatcher.BeginInvoke(() =>
        {
            if (Settings.Notifications) tray?.ShowBalloon(title, text);
        });
        Engine.StateChanged += () => Dispatcher.BeginInvoke(RefreshHdrState);

        tray = new TrayIcon(this);
        StartupTrace.Mark("tray created");

        Watcher = new ProcessWatcher();
        Watcher.Started += Engine.OnStarted;
        Watcher.Exited += Engine.OnExited;
        Watcher.Start();
        StartupTrace.Mark("watcher started");
        Log.Info($"Auto HDR запущен. Права администратора: {(Elevation.IsElevated ? "есть" : "нет")}");
        Engine.RecoverPending();

        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(RefreshHdrState);
        stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        stateTimer.Tick += (_, _) => RefreshHdrState();
        stateTimer.Start();
        RefreshHdrState();

        if (!args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) ShowMainWindow();
        _ = StartupScanAsync();
        StartupTrace.Mark("startup done");
    }

    bool AcquireSingleInstance()
    {
        try
        {
            instanceMutex = InstanceSignals.CreateMutex(out bool createdNew);
            if (!createdNew)
            {
                // --wait: перезапуск с правами администратора, старая копия ещё закрывается
                var timeout = args.Contains("--wait", StringComparer.OrdinalIgnoreCase) ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
                bool acquired;
                try { acquired = instanceMutex.WaitOne(timeout); }
                catch (AbandonedMutexException) { acquired = true; }

                if (!acquired)
                {
                    InstanceSignals.Signal(InstanceSignals.ShowEventName);
                    instanceMutex.Dispose();
                    instanceMutex = null;
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            // Мьютекс создан копией, к которой нет доступа: значит, она уже работает
            Log.Error("Auto HDR уже запущен", ex);
            InstanceSignals.Signal(InstanceSignals.ShowEventName);
            return false;
        }

        showEvent = InstanceSignals.CreateEvent(InstanceSignals.ShowEventName);
        exitEvent = InstanceSignals.CreateEvent(InstanceSignals.ExitEventName);
        StartSignalThread(showEvent, ShowMainWindow, "ShowSignal");
        StartSignalThread(exitEvent, () => ExitApp(), "ExitSignal");
        return true;
    }

    void StartSignalThread(EventWaitHandle handle, Action action, string name)
    {
        var thread = new Thread(() =>
        {
            while (handle.WaitOne())
            {
                if (exiting) return;
                Dispatcher.BeginInvoke(action);
            }
        }) { IsBackground = true, Name = name };
        thread.Start();
    }

    public void ShowMainWindow()
    {
        if (exiting) return;
        window ??= new MainWindow(this);
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.RefreshMonitors();
    }

    public void OnWindowHidden()
    {
        if (Settings.TrayHintShown) return;
        Settings.TrayHintShown = true;
        Settings.Save();
        tray?.ShowBalloon("Auto HDR работает в фоне", "Значок в трее открывает окно и меню");
    }

    // ---------------- Состояние HDR ----------------

    public void RefreshHdrState()
    {
        if (exiting) return;
        var displays = HdrControl.GetDisplays();
        Monitors.Sync(displays);
        var targets = Monitors.Targets(displays);
        if (targets.Count == 0) targets = displays.Where(d => d.HdrSupported).ToList();
        bool? hdrOn = targets.Count == 0 ? null : targets.Any(d => d.HdrOn);
        tray?.Update(hdrOn, Settings.Paused, Engine?.RunningGames ?? []);
        window?.UpdateDisplays(displays);
    }

    /// <summary>Переключить HDR вручную на мониторах с авто-HDR (или на всех, если таких нет).</summary>
    public async Task ToggleHdrAsync(string? onlyKey = null)
    {
        try
        {
            var displays = HdrControl.GetDisplays();
            var targets = onlyKey != null
                ? displays.Where(d => d.Key == onlyKey && d.HdrSupported).ToList()
                : Monitors.Targets(displays);
            if (targets.Count == 0) targets = displays.Where(d => d.HdrSupported).ToList();
            if (targets.Count == 0) return;

            bool on = !targets.Any(d => d.HdrOn);
            var changed = targets.Where(d => d.HdrOn != on && HdrControl.SetHdr(d, on)).Select(d => d.Key).ToList();
            if (!on) Engine?.ForgetPending(changed);
            Log.Info($"HDR {(on ? "включён" : "выключен")} вручную: {string.Join(", ", targets.Select(d => d.Name))}");
            if (changed.Count > 0) await HdrControl.WaitForStateAsync(changed, on, TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось переключить HDR", ex);
        }
        RefreshHdrState();
    }

    public void SetPaused(bool paused)
    {
        Settings.Paused = paused;
        Settings.Save();
        Log.Info(paused ? "Автопереключение HDR приостановлено" : "Автопереключение HDR снова работает");
        RefreshHdrState();
        window?.LoadSettings();
    }

    // ---------------- Игры ----------------

    async Task StartupScanAsync()
    {
        if (Games.Games.Count == 0) Log.Info("Первый запуск: поиск игр в библиотеках");
        await ScanLibrariesAsync();
    }

    public async Task ScanLibrariesAsync()
    {
        if (scanning) return;
        scanning = true;
        SetBusy("Поиск игр в библиотеках…", indeterminate: true);
        try
        {
            var folders = Settings.CustomFolders.ToList();
            var result = await Task.Run(() => LibraryScanner.ScanAll(folders));
            var merge = Games.Merge(result.Games, result.Sources, Settings.IgnoredIds);
            Games.Save();
            Log.Info($"Найдено игр: {merge.Found}, новых: {merge.Added}" +
                     (merge.Removed > 0 ? $", больше не установлены: {merge.Removed}" : ""));
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка при поиске игр", ex);
        }
        finally
        {
            scanning = false;
            ClearBusy();
        }
        // Не ждём проверку HDR: она идёт минутами, а кнопка поиска должна освободиться сразу
        _ = RefreshHdrInfoAsync(force: false);
    }

    public async Task AddFolderAsync(string folder)
    {
        var dir = PathUtil.NormalizeDir(folder);
        if (dir == null) return;
        if (!Settings.CustomFolders.Contains(dir, StringComparer.OrdinalIgnoreCase))
        {
            Settings.CustomFolders.Add(dir);
            Settings.Save();
        }
        var found = await Task.Run(() => LibraryScanner.ScanFolder(dir).ToList());
        int added = 0;
        foreach (var game in found)
        {
            Settings.IgnoredIds.RemoveAll(id => string.Equals(id, game.Id, StringComparison.OrdinalIgnoreCase));
            if (ReferenceEquals(Games.AddOrGet(game), game)) added++;
        }
        Settings.Save();
        Games.Save();
        Log.Info(found.Count == 0
            ? $"В папке {dir} игр не найдено"
            : $"Папка {dir}: найдено игр {found.Count}, новых {added}");
        await RefreshHdrInfoAsync(force: false);
    }

    public async Task AddExeAsync(string exePath)
    {
        var game = LibraryScanner.FromExe(exePath);
        var stored = Games.AddOrGet(game);
        Games.Save();
        if (ReferenceEquals(stored, game)) Log.Info($"Добавлена игра «{game.Name}»: {game.ExePath}");
        await RefreshHdrInfoAsync(force: false, only: [stored]);
    }

    public void RemoveGames(IEnumerable<GameRecord> games)
    {
        foreach (var game in games.Where(g => g.IsCustom).ToList())
        {
            if (game.Source == GameSources.Folder && !Settings.IgnoredIds.Contains(game.Id)) Settings.IgnoredIds.Add(game.Id);
            Games.Remove(game);
            Log.Info($"Игра «{game.Name}» убрана из списка");
        }
        Settings.Save();
        Games.Save();
    }

    /// <summary>Проверки идут по очереди: «Проверить HDR заново» во время другой проверки не теряется.</summary>
    public Task RefreshHdrInfoAsync(bool force, IReadOnlyList<GameRecord>? only = null) =>
        hdrRefresh = RefreshHdrInfoAfterAsync(hdrRefresh, force, only);

    async Task RefreshHdrInfoAfterAsync(Task previous, bool force, IReadOnlyList<GameRecord>? only)
    {
        await previous;
        var list = (only ?? Games.Games.ToList())
            .Where(g => !g.Missing && (force || g.HdrCheckedAt == null || DateTime.Now - g.HdrCheckedAt > HdrInfoTtl))
            .ToList();
        if (list.Count == 0) return;

        hdrRefreshRunning = true;
        SetBusy($"Проверка поддержки HDR: 0 из {list.Count}", indeterminate: false);
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Status.BusyText = $"Проверка поддержки HDR: {p.Done} из {p.Total}";
            Status.Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
        });

        try
        {
            await HdrInfo.RefreshAsync(list, Settings.OnlineHdrInfo, a => Dispatcher.Invoke(a), progress);
            Games.Save();
            Log.Info($"Проверка HDR: есть — {list.Count(g => g.Hdr == HdrSupport.Yes)}, " +
                     $"возможно — {list.Count(g => g.Hdr == HdrSupport.Maybe)}, " +
                     $"нет — {list.Count(g => g.Hdr == HdrSupport.No)}, " +
                     $"неизвестно — {list.Count(g => g.Hdr == HdrSupport.Unknown)}");
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка проверки поддержки HDR", ex);
        }
        finally
        {
            hdrRefreshRunning = false;
            ClearBusy();
            window?.RefreshGamesView();
        }
    }

    void SetBusy(string text, bool indeterminate)
    {
        Status.BusyText = text;
        Status.Indeterminate = indeterminate;
        Status.Progress = 0;
        Status.IsBusy = true;
    }

    void ClearBusy()
    {
        if (scanning || hdrRefreshRunning) return;
        Status.IsBusy = false;
        Status.BusyText = "";
    }

    // ---------------- Права и выход ----------------

    /// <summary>Перезапуск с правами администратора: «Ждать HDR» сможет приостановить и игру, запущенную с правами администратора.</summary>
    public void RestartElevated()
    {
        if (TaskInstaller.RunElevated("--wait", wait: false) != 0) return;
        ExitApp(revert: false);
    }

    public async void ExitApp(bool revert = true)
    {
        if (exiting) return;
        exiting = true;
        stateTimer?.Stop();
        Watcher?.Dispose();
        try
        {
            if (revert && Engine is { HasPendingRevert: true })
                await Engine.RevertAsync().WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось вернуть HDR при выходе", ex);
        }

        Games.Save();
        Settings.Save();
        tray?.Dispose();
        window?.CloseForExit();
        Log.Info("Auto HDR закрыт");
        showEvent?.Set();
        exitEvent?.Set();
        try { instanceMutex?.ReleaseMutex(); } catch { }
        Shutdown(0);
    }
}
