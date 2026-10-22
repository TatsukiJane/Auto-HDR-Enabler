using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoHdr.Core;

public static class AppPaths
{
    static readonly Lazy<string> dataDir = new(CreateDataDir);

    /// <summary>Папка из аргумента --data. Задаётся в Program.Main до первого обращения к <see cref="DataDir"/>.</summary>
    public static string? DataDirOverride { get; set; }

    public static string DataDir => dataDir.Value;
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string MonitorsFile => Path.Combine(DataDir, "monitors.json");
    public static string GamesFile => Path.Combine(DataDir, "games.json");
    public static string LogFile => Path.Combine(DataDir, "log.txt");
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AutoHdr.exe");

    /// <summary>Копия для автозапуска: в Program Files её не может подменить обычный пользователь.</summary>
    public static string InstalledExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Auto HDR", "AutoHdr.exe");

    /// <summary>Дескриптор папки данных у копии с правами администратора: пока он открыт, папку не переименовать.</summary>
    public static Microsoft.Win32.SafeHandles.SafeFileHandle? DataDirLock { get; private set; }

    static string CreateDataDir()
    {
        // AUTOHDR_DATA_DIR — отдельная папка данных для тестов. С правами администратора не читаем: окружение задаёт обычный пользователь
        var custom = DataDirOverride ?? (Elevation.IsElevated ? null : Environment.GetEnvironmentVariable("AUTOHDR_DATA_DIR"));
        var dir = !string.IsNullOrWhiteSpace(custom)
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoHdr");
        Directory.CreateDirectory(dir);
        // Без FILE_SHARE_DELETE папку нельзя переименовать и поставить на её место ссылку
        if (Elevation.IsElevated)
            DataDirLock = Native.CreateFile(dir, Native.FILE_LIST_DIRECTORY, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open,
                Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        return dir;
    }

    /// <summary>
    /// Копия с правами администратора не читает и не пишет через reparse point (соединение, символическую ссылку)
    /// ни в самой папке данных, ни в её файлах: папкой владеет обычный пользователь.
    /// shortcut: между проверкой и записью остаётся гонка; закрыть её можно только папкой, куда обычный пользователь не пишет
    /// </summary>
    public static void EnsureNoReparsePoint(params string[] paths)
    {
        if (!Elevation.IsElevated) return;
        foreach (var path in paths.Prepend(DataDir))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"{path} ведёт в другое место, копия с правами администратора его не трогает");
        }
    }
}

/// <summary>
/// Именованные объекты для связи копий приложения. Права доступа заданы явно:
/// копия с правами администратора создаёт объекты, которые иначе не открыть из обычного процесса.
/// </summary>
public static class InstanceSignals
{
    public const string MutexName = @"Local\AutoHdrEnabler.Instance";
    public const string ShowEventName = @"Local\AutoHdrEnabler.Show";
    public const string ExitEventName = @"Local\AutoHdrEnabler.Exit";

    public static Mutex CreateMutex(out bool createdNew)
    {
        var security = new System.Security.AccessControl.MutexSecurity();
        foreach (var sid in AllowedSids())
            security.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(sid,
                System.Security.AccessControl.MutexRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        return System.Threading.MutexAcl.Create(true, MutexName, out createdNew, security);
    }

    public static EventWaitHandle CreateEvent(string name)
    {
        var security = new System.Security.AccessControl.EventWaitHandleSecurity();
        foreach (var sid in AllowedSids())
            security.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(sid,
                System.Security.AccessControl.EventWaitHandleRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        return System.Threading.EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
    }

    /// <summary>Подать сигнал работающей копии. false, если она не запущена.</summary>
    public static bool Signal(string name)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name);
            return handle.Set();
        }
        catch
        {
            return false;
        }
    }

    static IEnumerable<SecurityIdentifier> AllowedSids()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User != null) yield return identity.User;
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    }
}

/// <summary>
/// Диагностика запуска: если рядом с exe лежит файл trace.on (или AUTOHDR_TRACE=1),
/// этапы старта пишутся в trace-&lt;pid&gt;.txt рядом с exe. По умолчанию выключена.
/// </summary>
public static class StartupTrace
{
    static readonly bool Enabled = Environment.GetEnvironmentVariable("AUTOHDR_TRACE") == "1"
                                   || File.Exists(Path.Combine(AppContext.BaseDirectory, "trace.on"));

    public static void Mark(string stage)
    {
        if (!Enabled) return;
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, $"trace-{Environment.ProcessId}.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {stage}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

public static class Log
{
    const int MaxLines = 1000;
    static readonly object Gate = new();
    static readonly LinkedList<string> Recent = new();
    public static string Prefix { get; set; } = "";

    public static event Action<string>? LineAdded;

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate) return Recent.ToList();
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR ", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {Prefix}{message}";
        StartupTrace.Mark("log: " + message);
        lock (Gate)
        {
            Recent.AddLast(line);
            if (Recent.Count > MaxLines) Recent.RemoveFirst();
            try
            {
                AppPaths.EnsureNoReparsePoint(AppPaths.LogFile, AppPaths.LogFile + ".old");
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 1_000_000)
                    File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", overwrite: true);
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // Журнал не должен ронять приложение: второй процесс (обёртка --launch) может держать файл.
                StartupTrace.Mark("log append failed: " + ex);
            }
        }
        LineAdded?.Invoke(line);
    }
}

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            AppPaths.EnsureNoReparsePoint(path);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось прочитать {Path.GetFileName(path)}, файл сохранён как .bad", ex);
            try
            {
                AppPaths.EnsureNoReparsePoint(path, path + ".bad");
                File.Copy(path, path + ".bad", overwrite: true);
            }
            catch { }
        }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        AppPaths.EnsureNoReparsePoint(tmp, path);
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        Raise(name!);
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Elevation
{
    public static bool IsElevated { get; } = Check();

    static bool Check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public static class PathUtil
{
    /// <summary>Полный путь без завершающего слэша или null, если путь некорректный.</summary>
    public static string? NormalizeDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"').Replace('/', '\\'));
            return full.Length > 3 ? full.TrimEnd('\\') : full;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsUnder(string path, string dir) =>
        path.Length > dir.Length + 1
        && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
        && (path[dir.Length] == '\\' || dir.EndsWith('\\'));
}
