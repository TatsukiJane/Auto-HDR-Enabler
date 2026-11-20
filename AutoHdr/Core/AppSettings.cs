namespace AutoHdr.Core;

public sealed class AppSettings
{
    static readonly object Gate = new();

    /// <summary>Whether to wait after the last game process exits before restoring SDR.</summary>
    public bool ExitDelay { get; set; }

    /// <summary>How many seconds to wait when <see cref="ExitDelay"/> is on.</summary>
    public int ExitDelaySec { get; set; } = 5;
    public bool RestoreAfterExit { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool Paused { get; set; }
    public bool OnlineHdrInfo { get; set; } = true;
    public bool TrayHintShown { get; set; }

    /// <summary>Whether to ask GitHub for a newer release at startup.</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>A release the user declined: its dialog is not shown again.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>"ru", "en" or null — the Windows language.</summary>
    public string? Language { get; set; }

    /// <summary>Folders added with the “Add folder” button: rescanned together with the libraries.</summary>
    public List<string> CustomFolders { get; set; } = [];

    /// <summary>Games from custom folders that were removed from the list.</summary>
    public List<string> IgnoredIds { get; set; } = [];

    /// <summary>Displays where the app turned HDR on and has not turned it back yet (survives a restart).</summary>
    public List<string> PendingRevert { get; set; } = [];

    /// <summary>Displays where the app switched the picture mode for a game: key → the mode to restore (survives a restart).</summary>
    public Dictionary<string, int> PendingPresets { get; set; } = [];

    public static AppSettings Load() => JsonStore.Load<AppSettings>(AppPaths.SettingsFile);

    public void Save()
    {
        lock (Gate)
        {
            try { JsonStore.Save(AppPaths.SettingsFile, this); }
            catch (Exception ex) { Log.Error(Loc.T("Не удалось сохранить настройки", "Could not save settings"), ex); }
        }
    }
}
