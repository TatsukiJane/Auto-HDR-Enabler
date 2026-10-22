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
            Log.Error(Loc.T("Необработанная ошибка", "Unhandled error"), ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error(Loc.T("Критическая ошибка", "Fatal error"), ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error(Loc.T("Ошибка фоновой задачи", "Background task error"), ex.Exception);
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
        Loc.Use(Settings.Language);
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
        Log.Info(Loc.T($"Auto HDR запущен. Права администратора: {(Elevation.IsElevated ? "есть" : "нет")}",
            $"Auto HDR started. Administrator rights: {(Elevation.IsElevated ? "yes" : "no")}"));

        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(RefreshHdrState);
        stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        stateTimer.Tick += (_, _) => RefreshHdrState();
        stateTimer.Start();
        RefreshHdrState(); // заодно вернёт SDR, если прошлый запуск не успел выключить HDR после игры

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
            Log.Error(Loc.T("Auto HDR уже запущен", "Auto HDR is already running"), ex);
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
        tray?.ShowBalloon(Loc.T("Auto HDR работает в фоне", "Auto HDR keeps running in the background"),
            Loc.T("Значок в трее открывает окно и меню", "The tray icon opens the window and the menu"));
    }

    /// <summary>Сменить язык: окно пересоздаётся на том же месте и на той же вкладке, меню трея получает новые подписи.</summary>
    public void SetLanguage(string? language)
    {
        Settings.Language = language;
        Settings.Save();
        Loc.Use(language);
        tray?.ApplyTexts();
        if (window is { } old)
        {
            var bounds = old.WindowState == WindowState.Normal ? new Rect(old.Left, old.Top, old.Width, old.Height) : old.RestoreBounds;
            window = new MainWindow(this)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
            };
            window.Tabs.SelectedIndex = old.Tabs.SelectedIndex;
            bool visible = old.IsVisible, maximized = old.WindowState == WindowState.Maximized;
            old.CloseForExit();
            if (visible) ShowMainWindow();
            if (maximized) window.WindowState = WindowState.Maximized;
        }
        RefreshHdrState();
    }

    // ---------------- Состояние HDR ----------------

    public void RefreshHdrState()
    {
        if (exiting) return;
        var displays = HdrControl.GetDisplays();
        Monitors.Sync(displays);
        Engine?.OnDisplaysChanged(displays);
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
            var names = string.Join(", ", targets.Select(d => d.Name));
            Log.Info(Loc.T($"HDR {(on ? "включён" : "выключен")} вручную: {names}", $"HDR turned {(on ? "on" : "off")} manually: {names}"));
            if (changed.Count > 0) await HdrControl.WaitForStateAsync(changed, on, TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("Не удалось переключить HDR", "Could not switch HDR"), ex);
        }
        RefreshHdrState();
    }

    public void SetPaused(bool paused)
    {
        Settings.Paused = paused;
        Settings.Save();
        Log.Info(paused ? Loc.T("Автопереключение HDR приостановлено", "Automatic HDR switching paused")
            : Loc.T("Автопереключение HDR снова работает", "Automatic HDR switching resumed"));
        RefreshHdrState();
        window?.LoadSettings();
    }

    // ---------------- Игры ----------------

    async Task StartupScanAsync()
    {
        if (Games.Games.Count == 0) Log.Info(Loc.T("Первый запуск: поиск игр в библиотеках", "First run: searching libraries for games"));
        await ScanLibrariesAsync();
    }

    public async Task ScanLibrariesAsync()
    {
        if (scanning) return;
        scanning = true;
        SetBusy(Loc.T("Поиск игр в библиотеках…", "Searching libraries for games…"), indeterminate: true);
        try
        {
            var folders = Settings.CustomFolders.ToList();
            var result = await Task.Run(() => LibraryScanner.ScanAll(folders));
            var merge = Games.Merge(result.Games, result.Sources, Settings.IgnoredIds);
            Games.Save();
            Log.Info(Loc.T($"Найдено игр: {merge.Found}, новых: {merge.Added}", $"Games found: {merge.Found}, new: {merge.Added}") +
                     (merge.Removed > 0 ? Loc.T($", больше не установлены: {merge.Removed}", $", no longer installed: {merge.Removed}") : ""));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("Ошибка при поиске игр", "Game search failed"), ex);
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
            ? Loc.T($"В папке {dir} игр не найдено", $"No games found in {dir}")
            : Loc.T($"Папка {dir}: найдено игр {found.Count}, новых {added}", $"Folder {dir}: games found {found.Count}, new {added}"));
        await RefreshHdrInfoAsync(force: false);
    }

    public async Task AddExeAsync(string exePath)
    {
        var game = LibraryScanner.FromExe(exePath);
        var stored = Games.AddOrGet(game);
        Games.Save();
        if (ReferenceEquals(stored, game)) Log.Info(Loc.T($"Добавлена игра «{game.Name}»: {game.ExePath}", $"Added game “{game.Name}”: {game.ExePath}"));
        await RefreshHdrInfoAsync(force: false, only: [stored]);
    }

    public void RemoveGames(IEnumerable<GameRecord> games)
    {
        foreach (var game in games.Where(g => g.IsCustom).ToList())
        {
            if (game.Source == GameSources.Folder && !Settings.IgnoredIds.Contains(game.Id)) Settings.IgnoredIds.Add(game.Id);
            Games.Remove(game);
            Log.Info(Loc.T($"Игра «{game.Name}» убрана из списка", $"Game “{game.Name}” removed from the list"));
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
        SetBusy(Loc.T($"Проверка поддержки HDR: 0 из {list.Count}", $"Checking HDR support: 0 of {list.Count}"), indeterminate: false);
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Status.BusyText = Loc.T($"Проверка поддержки HDR: {p.Done} из {p.Total}", $"Checking HDR support: {p.Done} of {p.Total}");
            Status.Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
        });

        try
        {
            await HdrInfo.RefreshAsync(list, Settings.OnlineHdrInfo, a => Dispatcher.Invoke(a), progress);
            Games.Save();
            int yes = list.Count(g => g.Hdr == HdrSupport.Yes), maybe = list.Count(g => g.Hdr == HdrSupport.Maybe);
            int no = list.Count(g => g.Hdr == HdrSupport.No), unknown = list.Count(g => g.Hdr == HdrSupport.Unknown);
            Log.Info(Loc.T($"Проверка HDR: есть — {yes}, возможно — {maybe}, нет — {no}, неизвестно — {unknown}",
                $"HDR check: yes {yes}, maybe {maybe}, no {no}, unknown {unknown}"));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("Ошибка проверки поддержки HDR", "HDR support check failed"), ex);
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
            Log.Error(Loc.T("Не удалось вернуть HDR при выходе", "Could not restore HDR on exit"), ex);
        }

        Games.Save();
        Settings.Save();
        tray?.Dispose();
        window?.CloseForExit();
        Log.Info(Loc.T("Auto HDR закрыт", "Auto HDR closed"));
        showEvent?.Set();
        exitEvent?.Set();
        try { instanceMutex?.ReleaseMutex(); } catch { }
        Shutdown(0);
    }
}
