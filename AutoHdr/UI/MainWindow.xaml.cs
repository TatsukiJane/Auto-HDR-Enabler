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

        // Подсказки заголовков заданы в разметке через ToolTipService.ToolTip столбца.
        // Привязка из стиля не работает: заголовок получает Column позже и без уведомления
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

        // Копия из Program Files не меняет путь при пересборке и есть, пока включён или был включён автозапуск
        var launcher = File.Exists(AppPaths.InstalledExe) ? AppPaths.InstalledExe : AppPaths.ExePath;
        LaunchCommandBox.Text = $"\"{launcher}\" --launch %command%";
        VersionText.Text = $"Auto HDR {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · {Loc.T("данные", "data")}: {AppPaths.DataDir}";
        LoadSettings();
    }

    // ---------------- Игры ----------------

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

    // ---------------- Мониторы ----------------

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
            // У виртуального дисплея тип подключения и так «Виртуальный»: показываем один раз
            var connection = record.IsVirtual ? Loc.T("Виртуальный дисплей", "Virtual display") : record.Connection;
            var kind = !record.IsVirtual && MonitorRegistry.LooksLikeTv(record.Name) ? Loc.T("телевизор", "TV") : null;
            row.Details = string.Join(" · ", new[] { connection, record.EdidId, kind }.Where(s => !string.IsNullOrEmpty(s)));
            row.Connected = display != null;
            row.HdrSupported = display?.HdrSupported ?? record.HdrSupported;
            row.HdrOn = display?.HdrOn ?? false;
            row.SetAutoSilently(record.AutoHdr);
            row.RaiseAll();
        }
    }

    async void ToggleMonitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MonitorRow row) return;
        ((Button)sender).IsEnabled = false;
        try { await app.ToggleHdrAsync(row.Key); }
        finally { ((Button)sender).IsEnabled = true; }
    }

    void ForgetMonitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MonitorRow row) app.Monitors.Forget(row.Key);
    }

    // ---------------- Настройки ----------------

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

    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSettings) return;
        string? language = LanguageBox.SelectedIndex switch { 1 => "ru", 2 => "en", _ => null };
        // Окно пересоздаётся на новом языке: не закрываем его посреди его же обработчика
        Dispatcher.BeginInvoke(() => app.SetLanguage(language));
    }

    void ExitDelay_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        app.Settings.ExitDelay = ExitDelayCheck.IsChecked == true;
        app.Settings.Save();
    }

    // Сохраняем на каждый ввод: LostFocus не срабатывает, если окно закрыли прямо из поля
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

    // ---------------- Журнал ----------------

    void AppendJournal(string line)
    {
        bool atEnd = JournalBox.VerticalOffset + JournalBox.ViewportHeight >= JournalBox.ExtentHeight - 4;
        if (JournalBox.Text.Length > MaxJournalChars)
            JournalBox.Text = JournalBox.Text[^(MaxJournalChars / 2)..];
        JournalBox.AppendText(line + Environment.NewLine);
        if (atEnd) JournalBox.ScrollToEnd();
    }

    void ClearJournal_Click(object sender, RoutedEventArgs e) => JournalBox.Clear();

    // ---------------- Окно ----------------

    /// <summary>
    /// Открыть папку, файл или ссылку от имени обычного пользователя:
    /// через explorer.exe, чтобы браузер не унаследовал права администратора.
    /// Полный путь: по короткому имени Windows сначала ищет программу в папке приложения.
    /// </summary>
    static void OpenUnelevated(string target)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try { Process.Start(new ProcessStartInfo(explorer, $"\"{target}\"") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Error(Loc.T($"Не удалось открыть {target}", $"Could not open {target}"), ex); }
    }

    void OnGamesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(UpdateCounts);
    void OnMonitorsChanged() => Dispatcher.BeginInvoke(RefreshMonitors);
    void OnLogLine(string line) => Dispatcher.BeginInvoke(() => AppendJournal(line));

    // При смене языка окно пересоздаётся: закрытое не должно держаться за события приложения
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
