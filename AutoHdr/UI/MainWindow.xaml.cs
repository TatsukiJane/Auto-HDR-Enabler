using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using AutoHdr.Core;
using AutoHdr.Library;
using Microsoft.Win32;

namespace AutoHdr.UI;

public partial class MainWindow : Window
{
    const int MaxJournalChars = 300_000;

    readonly App app;
    readonly ListCollectionView gamesView;
    readonly ObservableCollection<MonitorRow> monitorRows = [];
    bool loadingSettings;
    bool closingForExit;

    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        DataContext = app.Status;

        // Header tooltips are set in markup through the column's ToolTipService.ToolTip.
        // A binding from the style does not work: the header gets Column later and without a notification
        var headerStyle = TryFindResource(typeof(DataGridColumnHeader)) as Style;
        foreach (var column in GamesGrid.Columns.Concat(MonitorsGrid.Columns))
            if (ToolTipService.GetToolTip(column) is { } tip)
                column.HeaderStyle = new Style(typeof(DataGridColumnHeader), headerStyle) { Setters = { new Setter(ToolTipProperty, tip) } };

        gamesView = new ListCollectionView(app.Games.Games) { Filter = FilterGame };
        ApplyDefaultSort();
        GamesGrid.ItemsSource = gamesView;
        GamesGrid.Sorting += (_, _) => gamesView.SortDescriptions.Clear();
        app.Games.Games.CollectionChanged += OnGamesChanged;
        UpdateCounts();

        MonitorsGrid.ItemsSource = monitorRows;
        app.Monitors.Changed += OnMonitorsChanged;
        RefreshMonitors();

        JournalBox.Text = string.Join(Environment.NewLine, Log.Snapshot()) + Environment.NewLine;
        Log.LineAdded += OnLogLine;
        Loaded += (_, _) => JournalBox.ScrollToEnd();

        // The Program Files copy keeps its path across rebuilds and exists while autostart is or was on
        var launcher = File.Exists(AppPaths.InstalledExe) ? AppPaths.InstalledExe : AppPaths.ExePath;
        LaunchCommandBox.Text = $"\"{launcher}\" --launch %command%";
        VersionText.Text = $"Auto HDR {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · {Loc.T("данные", "data")}: {AppPaths.DataDir}";
        LoadSettings();
    }

    // ---------------- Games ----------------

    void ApplyDefaultSort()
    {
        using (gamesView.DeferRefresh())
        {
            gamesView.SortDescriptions.Clear();
            gamesView.SortDescriptions.Add(new SortDescription(nameof(GameRecord.Missing), ListSortDirection.Ascending));
            gamesView.SortDescriptions.Add(new SortDescription(nameof(GameRecord.Enabled), ListSortDirection.Descending));
            gamesView.SortDescriptions.Add(new SortDescription(nameof(GameRecord.HdrSortKey), ListSortDirection.Ascending));
            gamesView.SortDescriptions.Add(new SortDescription(nameof(GameRecord.Name), ListSortDirection.Ascending));
        }
    }

    bool FilterGame(object item)
    {
        if (item is not GameRecord game) return false;
        var search = SearchBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(search)
            && !game.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            && !game.Location.Contains(search, StringComparison.OrdinalIgnoreCase))
            return false;

        int filter = FilterBox?.SelectedIndex ?? 0;
        if (filter == 6) return game.Missing;
        if (game.Missing) return false;
        return filter switch
        {
            1 => game.Enabled,
            2 => game.Hdr == HdrSupport.Yes,
            3 => game.Hdr == HdrSupport.Maybe,
            4 => game.Hdr == HdrSupport.No,
            5 => game.HdrCheckedAt == null,
            _ => true,
        };
    }

    public void RefreshGamesView()
    {
        if (gamesView.IsAddingNew || gamesView.IsEditingItem) return;
        gamesView.Refresh();
        UpdateCounts();
    }

    void UpdateCounts()
    {
        var installed = app.Games.Games.Where(g => !g.Missing).ToList();
        int enabled = installed.Count(g => g.Enabled);
        CountText.Text = installed.Count == 0 ? ""
            : Loc.T($"Игр: {installed.Count} · с авто-HDR: {enabled}", $"Games: {installed.Count} · with auto HDR: {enabled}");
        EmptyState.Visibility = app.Games.Games.Count == 0 && !app.Status.IsBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        gamesView?.Refresh();
    }

    void Filter_Changed(object sender, SelectionChangedEventArgs e) => gamesView?.Refresh();

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        try
        {
            await app.ScanLibrariesAsync();
        }
        finally
        {
            ScanButton.IsEnabled = true;
            ApplyDefaultSort();
            RefreshGamesView();
        }
    }

    async void AddExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Выберите exe игры", "Select the game exe"),
            Filter = Loc.T("Программы (*.exe)|*.exe", "Programs (*.exe)|*.exe"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        await app.AddExeAsync(dialog.FileName);
        RefreshGamesView();
    }

    async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Папка с игрой или с несколькими играми", "Folder with a game or several games") };
        if (dialog.ShowDialog(this) != true) return;
        await app.AddFolderAsync(dialog.FolderName);
        RefreshGamesView();
    }

    async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        RecheckButton.IsEnabled = false;
        try { await app.RefreshHdrInfoAsync(force: true); }
        finally { RecheckButton.IsEnabled = true; }
    }

    async void RecheckSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedGames();
        if (selected.Count > 0) await app.RefreshHdrInfoAsync(force: true, only: selected);
    }

    void ResetOverride_Click(object sender, RoutedEventArgs e)
    {
        foreach (var game in SelectedGames())
        {
            game.UserOverride = false;
            HdrInfo.HdrRules.Apply(game);
        }
        app.Games.SaveSoon();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedGames();
        var custom = selected.Where(g => g.IsCustom).ToList();
        if (custom.Count == 0)
        {
            MessageBox.Show(this,
                Loc.T("Игры из библиотек остаются в списке, пока установлены. Чтобы HDR для них не включался, снимите флажок «Авто-HDR»",
                    "Library games stay in the list while they are installed. To keep HDR off for them, clear the “Auto HDR” checkbox"),
                "Auto HDR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        app.RemoveGames(custom);
        UpdateCounts();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var game = SelectedGames().FirstOrDefault();
        var dir = game?.InstallDir ?? (game?.ExePath != null ? Path.GetDirectoryName(game.ExePath) : null);
        if (dir != null && Directory.Exists(dir)) OpenUnelevated(dir);
    }

    void OpenPcgw_Click(object sender, RoutedEventArgs e)
    {
        var game = SelectedGames().FirstOrDefault();
        if (game == null) return;
        var url = game.PcgwPage != null
            ? "https://www.pcgamingwiki.com/wiki/" + Uri.EscapeDataString(game.PcgwPage.Replace(' ', '_'))
            : "https://www.pcgamingwiki.com/w/index.php?search=" + Uri.EscapeDataString(game.Name);
        OpenUnelevated(url);
    }

    void GamesGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(GamesGrid, source) is DataGridRow row
            && row.Item is GameRecord game && !IsInsideCheckBox(source))
            game.EnabledUi = !game.EnabledUi;
    }

    static bool IsInsideCheckBox(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is CheckBox) return true;
            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    List<GameRecord> SelectedGames() => GamesGrid.SelectedItems.OfType<GameRecord>().ToList();

    // ---------------- Displays ----------------

    public void RefreshMonitors() => UpdateDisplays(HdrControl.GetDisplays());

    public void UpdateDisplays(IReadOnlyList<DisplayInfo> displays)
    {
        var records = app.Monitors.All;
        foreach (var stale in monitorRows.Where(r => records.All(m => m.Key != r.Key)).ToList())
            monitorRows.Remove(stale);

        foreach (var record in records)
        {
            var row = monitorRows.FirstOrDefault(r => r.Key == record.Key);
            if (row == null)
            {
                row = new MonitorRow(record.Key, (key, on) => app.Monitors.SetAuto(key, on));
                monitorRows.Add(row);
            }
            var display = displays.FirstOrDefault(d => d.Key == record.Key);
            row.Name = record.Name;
            // For a virtual display the connection type already says “Virtual”: show it once
            var connection = record.IsVirtual ? Loc.T("Виртуальный дисплей", "Virtual display") : record.Connection;
            var kind = !record.IsVirtual && MonitorRegistry.LooksLikeTv(record.Name) ? Loc.T("телевизор", "TV") : null;
            var preset = record.GamePreset is int p ? Loc.T($"режим для игр: {MonitorPreset.Name(record.GamePresetCode ?? 0xE0)} = {p}", $"game mode: {MonitorPreset.Name(record.GamePresetCode ?? 0xE0)} = {p}") : null;
            row.Details = string.Join(" · ", new[] { connection, record.EdidId, kind, preset }.Where(s => !string.IsNullOrEmpty(s)));
            row.Connected = display != null;
            row.HdrSupported = display?.HdrSupported ?? record.HdrSupported;
            row.HdrOn = display?.HdrOn ?? false;
            row.IsVirtual = record.IsVirtual;
            row.SetAutoSilently(record.AutoHdr);
            row.RaiseAll();
        }

        // The checkbox does nothing until a game mode is set up: do not offer it before that
        MonitorModeColumn.Visibility = records.Any(m => m.GamePreset != null) ? Visibility.Visible : Visibility.Collapsed;
    }

    async void ToggleMonitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MonitorRow row) return;
        ((Button)sender).IsEnabled = false;
        try { await app.ToggleHdrAsync(row.Key); }
        finally { ((Button)sender).IsEnabled = true; }
    }

    bool settingUpMode;

    /// <summary>
    /// Game mode setup: the user switches the monitor to the everyday mode, the game mode and the everyday mode again,
    /// the app reads all settings at each step, then finds the code that switches the whole mode (MonitorPreset.FindModeCode).
    /// </summary>
    async void SetupMode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MonitorRow row) return;
        if (settingUpMode)
        {
            Info(Loc.T("Настройка режима для игр уже идёт", "The game mode setup is already running"));
            return;
        }
        if (row.HdrOn)
        {
            Info(Loc.T("Выключите HDR на этом мониторе на время настройки: в HDR монитор подменяет яркость и цвета режима",
                "Turn HDR off on this display for the setup: in HDR the monitor overrides the mode brightness and colors"));
            return;
        }

        settingUpMode = true;
        try
        {
            var everyday = await ReadStepAsync(row, Loc.T("Шаг 1 из 3. Включите кнопками монитора обычный режим, в котором вы работаете, и закройте меню монитора",
                "Step 1 of 3. Use the monitor buttons to select your everyday mode, then close the monitor menu"), null);
            if (everyday == null) return;
            var game = await ReadStepAsync(row, Loc.T("Шаг 2 из 3. Включите режим изображения для игр, например с local dimming, и закройте меню монитора. Если режим меняет разрешение или частоту, дождитесь, пока вернётся картинка",
                "Step 2 of 3. Select the game picture mode, for example with local dimming, then close the monitor menu. If the mode changes the resolution or refresh rate, wait until the picture is back"), everyday.Keys);
            if (game == null) return;
            var again = await ReadStepAsync(row, Loc.T("Шаг 3 из 3. Снова включите обычный режим и закройте меню монитора",
                "Step 3 of 3. Select the everyday mode again, then close the monitor menu"), everyday.Keys);
            if (again == null) return;

            row.Activity = Loc.T("Проверяю команды, картинка может мигнуть или погаснуть на несколько секунд…", "Checking commands, the picture may flicker or go dark for a few seconds…");
            var result = await Task.Run(() => MonitorPreset.FindModeCode(row.Key, everyday, game, again));
            row.Activity = null;

            switch (result.Outcome)
            {
                case LearnOutcome.Found:
                    app.Monitors.SetGamePreset(row.Key, result.Code, result.Value);
                    Info(Loc.T($"Режим для игр сохранён: код {MonitorPreset.Name(result.Code)}, значение {result.Value}. Монитор вернулся в обычный режим. Отметьте «Режим монитора» у нужных игр во вкладке «Игры»",
                        $"Game mode saved: code {MonitorPreset.Name(result.Code)}, value {result.Value}. The monitor is back in the everyday mode. Check “Monitor mode” for the games you want on the Games tab"));
                    break;
                case LearnOutcome.NoChange:
                    Warn(Loc.T("Между шагами настройки монитора не изменились. Возможно, режим не переключили или монитор не сообщает его по DDC/CI. Режим для игр не сохранён",
                        "The monitor settings did not change between the steps. Maybe the mode was not switched, or the monitor does not report it over DDC/CI. No game mode saved"));
                    break;
                case LearnOutcome.NotFound:
                    Warn(Loc.T("Ни одна команда не переключила режим целиком, поэтому режим для игр не сохранён. Похоже, этот режим монитор с компьютера не переключает. Какие настройки изменились, записано во вкладке «Журнал»",
                        "No command switched the whole mode, so no game mode was saved. The monitor does not seem to switch this mode from the computer. The settings that changed are listed on the Log tab"));
                    break;
                case LearnOutcome.NotRestored:
                    Warn(Loc.T("Монитор не вернулся в обычный режим: переключите его кнопками. Режим для игр не сохранён",
                        "The monitor did not return to the everyday mode: switch it with its buttons. No game mode saved"));
                    break;
            }
        }
        finally
        {
            settingUpMode = false;
            row.Activity = null;
        }
    }

    /// <summary>Shows the step, then reads the monitor settings. Null if the user cancelled or the monitor did not answer.</summary>
    async Task<Dictionary<int, int>?> ReadStepAsync(MonitorRow row, string step, IReadOnlyCollection<int>? codes)
    {
        var answer = MessageBox.Show(this, step + Environment.NewLine + Environment.NewLine + Loc.T("Когда будет готово, нажмите «ОК»", "Click OK when ready"),
            "Auto HDR", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return null;

        row.Activity = codes == null ? Loc.T("Читаю настройки монитора, около минуты…", "Reading the monitor settings, about a minute…")
            : Loc.T("Читаю настройки монитора, около 30 с…", "Reading the monitor settings, about 30 s…");
        var codeList = codes?.ToList();
        var snapshot = await Task.Run(() => MonitorPreset.Snapshot(row.Key, codeList));
        row.Activity = null;
        if (snapshot.Count > 0) return snapshot;

        Log.Warn(Loc.T($"«{row.Name}» не ответил по DDC/CI: режим для игр не настроен", $"“{row.Name}” did not answer over DDC/CI: no game mode set up"));
        Warn(Loc.T("Монитор не ответил по DDC/CI. Включите DDC/CI в меню монитора и закройте другие программы управления монитором",
            "The monitor did not answer over DDC/CI. Turn DDC/CI on in the monitor menu and close other monitor control apps"));
        return null;
    }

    void Info(string text) => MessageBox.Show(this, text, "Auto HDR", MessageBoxButton.OK, MessageBoxImage.Information);
    void Warn(string text) => MessageBox.Show(this, text, "Auto HDR", MessageBoxButton.OK, MessageBoxImage.Warning);

    void ForgetMonitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MonitorRow row) app.Monitors.Forget(row.Key);
    }

    // ---------------- Settings ----------------

    public void LoadSettings()
    {
        loadingSettings = true;
        try
        {
            var s = app.Settings;
            AutoSwitchCheck.IsChecked = !s.Paused;
            RestoreCheck.IsChecked = s.RestoreAfterExit;
            ExitDelayCheck.IsChecked = s.ExitDelay;
            ExitDelayBox.Text = s.ExitDelaySec.ToString();
            NotifyCheck.IsChecked = s.Notifications;
            OnlineCheck.IsChecked = s.OnlineHdrInfo;
            UpdatesCheck.IsChecked = s.CheckUpdates;
            LanguageBox.SelectedIndex = s.Language switch { "ru" => 1, "en" => 2, _ => 0 };

            ElevateButton.Visibility = TrackingText.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            loadingSettings = false;
        }
        _ = LoadAutostartAsync();
    }

    async Task LoadAutostartAsync()
    {
        bool installed = await Task.Run(TaskInstaller.IsInstalled);
        loadingSettings = true;
        AutostartCheck.IsChecked = installed;
        loadingSettings = false;
    }

    async void Autostart_Click(object sender, RoutedEventArgs e)
    {
        bool want = AutostartCheck.IsChecked == true;
        AutostartCheck.IsEnabled = false;
        try
        {
            bool ok = await Task.Run(() => TaskInstaller.SetInstalled(want));
            bool installed = await Task.Run(TaskInstaller.IsInstalled);
            AutostartCheck.IsChecked = installed;
            if (!ok && installed != want)
                AutostartHint.Text = Loc.T("Не получилось изменить автозапуск: нужны права администратора", "Could not change autostart: administrator rights are needed");
        }
        finally
        {
            AutostartCheck.IsEnabled = true;
        }
    }

    void AutoSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        app.SetPaused(AutoSwitchCheck.IsChecked != true);
    }

    void Restore_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        app.Settings.RestoreAfterExit = RestoreCheck.IsChecked == true;
        app.Settings.Save();
    }

    void Notify_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        app.Settings.Notifications = NotifyCheck.IsChecked == true;
        app.Settings.Save();
    }

    void Online_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        app.Settings.OnlineHdrInfo = OnlineCheck.IsChecked == true;
        app.Settings.Save();
    }

    void Updates_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        app.Settings.CheckUpdates = UpdatesCheck.IsChecked == true;
        app.Settings.SkippedVersion = null; // turning the check back on reminds about a declined release too
        app.Settings.Save();
    }

    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSettings) return;
        string? language = LanguageBox.SelectedIndex switch { 1 => "ru", 2 => "en", _ => null };
        // The window is recreated in the new language: do not close it in the middle of its own handler
        Dispatcher.BeginInvoke(() => app.SetLanguage(language));
    }

    void ExitDelay_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        app.Settings.ExitDelay = ExitDelayCheck.IsChecked == true;
        app.Settings.Save();
    }

    // Save on every keystroke: LostFocus does not fire if the window is closed right from the field
    void ExitDelaySec_Changed(object sender, TextChangedEventArgs e)
    {
        if (loadingSettings || !int.TryParse(ExitDelayBox.Text.Trim(), out var seconds)) return;
        app.Settings.ExitDelaySec = Math.Clamp(seconds, 0, 600);
        app.Settings.Save();
    }

    void ExitDelaySec_LostFocus(object sender, RoutedEventArgs e) => ExitDelayBox.Text = app.Settings.ExitDelaySec.ToString();

    void Elevate_Click(object sender, RoutedEventArgs e) => app.RestartElevated();

    void CopyLaunch_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LaunchCommandBox.Text); }
        catch (Exception ex) { Log.Error(Loc.T("Не удалось скопировать в буфер обмена", "Could not copy to the clipboard"), ex); }
    }

    void OpenDataDir_Click(object sender, RoutedEventArgs e) => OpenUnelevated(AppPaths.DataDir);

    void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(AppPaths.LogFile)) OpenUnelevated(AppPaths.LogFile);
    }

    // ---------------- Log ----------------

    void AppendJournal(string line)
    {
        bool atEnd = JournalBox.VerticalOffset + JournalBox.ViewportHeight >= JournalBox.ExtentHeight - 4;
        if (JournalBox.Text.Length > MaxJournalChars)
            JournalBox.Text = JournalBox.Text[^(MaxJournalChars / 2)..];
        JournalBox.AppendText(line + Environment.NewLine);
        if (atEnd) JournalBox.ScrollToEnd();
    }

    void ClearJournal_Click(object sender, RoutedEventArgs e) => JournalBox.Clear();

    // ---------------- Window ----------------

    /// <summary>
    /// Open a folder, file or URL as a regular user:
    /// through explorer.exe, so the browser does not inherit administrator rights.
    /// Full path: for a short name Windows first looks for the program in the app folder.
    /// </summary>
    internal static void OpenUnelevated(string target)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try { Process.Start(new ProcessStartInfo(explorer, $"\"{target}\"") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Error(Loc.T($"Не удалось открыть {target}", $"Could not open {target}"), ex); }
    }

    void OnGamesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(UpdateCounts);
    void OnMonitorsChanged() => Dispatcher.BeginInvoke(RefreshMonitors);
    void OnLogLine(string line) => Dispatcher.BeginInvoke(() => AppendJournal(line));

    // The window is recreated when the language changes: a closed one must not hold on to app events
    protected override void OnClosed(EventArgs e)
    {
        app.Games.Games.CollectionChanged -= OnGamesChanged;
        app.Monitors.Changed -= OnMonitorsChanged;
        Log.LineAdded -= OnLogLine;
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!closingForExit)
        {
            e.Cancel = true;
            Hide();
            app.OnWindowHidden();
        }
        base.OnClosing(e);
    }

    public void CloseForExit()
    {
        closingForExit = true;
        Close();
    }
}
