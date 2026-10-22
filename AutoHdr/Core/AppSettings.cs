namespace AutoHdr.Core;

public sealed class AppSettings
{
    static readonly object Gate = new();

    /// <summary>Ждать ли после закрытия последнего процесса игры, прежде чем вернуть SDR.</summary>
    public bool ExitDelay { get; set; }

    /// <summary>Сколько секунд ждать, если включена <see cref="ExitDelay"/>.</summary>
    public int ExitDelaySec { get; set; } = 5;
    public bool RestoreAfterExit { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool Paused { get; set; }
    public bool OnlineHdrInfo { get; set; } = true;
    public bool TrayHintShown { get; set; }

    /// <summary>"ru", "en" или null — язык Windows.</summary>
    public string? Language { get; set; }

    /// <summary>Папки, добавленные кнопкой «Добавить папку с играми»: пересканируются вместе с библиотеками.</summary>
    public List<string> CustomFolders { get; set; } = [];

    /// <summary>Игры из пользовательских папок, которые убрали из списка.</summary>
    public List<string> IgnoredIds { get; set; } = [];

    /// <summary>Мониторы, на которых HDR включило приложение и ещё не вернуло назад (переживает перезапуск).</summary>
    public List<string> PendingRevert { get; set; } = [];

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
