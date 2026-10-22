using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AutoHdr.Core;
using Microsoft.Win32;

namespace AutoHdr.Library;

public sealed record ScanResult(List<GameRecord> Games, HashSet<string> Sources);

/// <summary>Поиск установленных игр в библиотеках лаунчеров и пользовательских папках.</summary>
public static partial class LibraryScanner
{
    public static ScanResult ScanAll(IEnumerable<string> customFolders)
    {
        var games = new List<GameRecord>();
        var sources = new HashSet<string>();

        void Run(string source, Func<IEnumerable<GameRecord>> scan)
        {
            try
            {
                var found = scan().ToList();
                games.AddRange(found);
                sources.Add(source);
            }
            catch (Exception ex)
            {
                // Источник не отмечаем просканированным: его игры не пометятся как удалённые
                Log.Error($"Не удалось просканировать {source}", ex);
            }
        }

        Run(GameSources.Steam, ScanSteam);
        Run(GameSources.Gog, ScanGog);
        Run(GameSources.Epic, ScanEpic);
        Run(GameSources.Ea, ScanEa);
        Run(GameSources.Ubisoft, ScanUbisoft);
        Run(GameSources.Xbox, ScanXbox);
        Run(GameSources.Rockstar, ScanRockstar);
        Run(GameSources.Folder, () => customFolders.SelectMany(ScanFolder));
        return new ScanResult(games, sources);
    }

    // ---------------- Steam ----------------

    static readonly HashSet<string> SteamToolIds =
    [
        "228980",  // Steamworks Common Redistributables
        "250820",  // SteamVR
        "1070560", "1391110", "1628350", // Steam Linux Runtime
        "1493710", "1826330", "2180100", // Proton
    ];

    static IEnumerable<GameRecord> ScanSteam()
    {
        var steamPath = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        steamPath = PathUtil.NormalizeDir(steamPath) ?? PathUtil.NormalizeDir(@"C:\Program Files (x86)\Steam");
        if (steamPath == null || !Directory.Exists(steamPath)) yield break;

        // Сначала пути из libraryfolders.vdf: в них правильный регистр букв, а SteamPath в реестре записан строчными
        var libraries = new List<string>();
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
            {
                var dir = PathUtil.NormalizeDir(VdfUnescape(m.Groups[1].Value));
                if (dir != null && !libraries.Contains(dir, StringComparer.OrdinalIgnoreCase)) libraries.Add(dir);
            }
        }
        if (!libraries.Contains(steamPath, StringComparer.OrdinalIgnoreCase)) libraries.Insert(0, steamPath);

        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(manifest); }
                catch { continue; }

                var appId = VdfValue(text, "appid");
                var name = VdfValue(text, "name");
                var installDir = VdfValue(text, "installdir");
                if (appId == null || name == null || installDir == null) continue;
                if (SteamToolIds.Contains(appId) || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dir = PathUtil.NormalizeDir(Path.Combine(apps, "common", installDir));
                if (dir == null || !Directory.Exists(dir)) continue;

                yield return new GameRecord
                {
                    Id = "steam:" + appId,
                    Name = name,
                    Source = GameSources.Steam,
                    StoreId = appId,
                    InstallDir = dir,
                };
            }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    static string? VdfValue(string text, string key)
    {
        var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase);
        return m.Success ? VdfUnescape(m.Groups[1].Value) : null;
    }

    static string VdfUnescape(string s) => s.Replace("\\\\", "\\").Replace("\\\"", "\"");

    // ---------------- GOG ----------------

    static IEnumerable<GameRecord> ScanGog()
    {
        using var root = OpenHklm32(@"SOFTWARE\GOG.com\Games");
        if (root == null) yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(id);
            if (key == null) continue;
            if (!string.IsNullOrWhiteSpace(key.GetValue("dependsOn") as string)) continue; // DLC
            if (string.IsNullOrWhiteSpace(key.GetValue("exe") as string)) continue;
            var dir = PathUtil.NormalizeDir(key.GetValue("path") as string);
            if (dir == null || !Directory.Exists(dir)) continue;
            var gameId = key.GetValue("gameID") as string ?? id;
            yield return new GameRecord
            {
                Id = "gog:" + gameId,
                Name = key.GetValue("gameName") as string ?? Path.GetFileName(dir),
                Source = GameSources.Gog,
                StoreId = gameId,
                InstallDir = dir,
            };
        }
    }

    // ---------------- Epic ----------------

    static IEnumerable<GameRecord> ScanEpic()
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) yield break;

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            GameRecord? game = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                var appName = Str("AppName");
                var mainApp = Str("MainGameAppName");
                bool incomplete = r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True;
                bool isGame = !r.TryGetProperty("AppCategories", out var cats) || cats.ValueKind != JsonValueKind.Array
                    || cats.EnumerateArray().Any(c => c.GetString() == "games");
                var dir = PathUtil.NormalizeDir(Str("InstallLocation"));
                if (appName != null && (mainApp == null || mainApp == appName) && !incomplete && isGame
                    && dir != null && Directory.Exists(dir))
                {
                    game = new GameRecord
                    {
                        Id = "epic:" + appName,
                        Name = Str("DisplayName") ?? Path.GetFileName(dir),
                        Source = GameSources.Epic,
                        StoreId = appName,
                        InstallDir = dir,
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Epic: не удалось прочитать {Path.GetFileName(file)}: {ex.Message}");
            }
            if (game != null) yield return game;
        }
    }

    // ---------------- EA app ----------------

    static readonly HashSet<string> EaServiceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "EA Core", "EA Desktop", "EADM", "EA app", "Origin", "EA Installer", "EAAntiCheat",
    };

    static IEnumerable<GameRecord> ScanEa()
    {
        foreach (var rootPath in new[] { @"SOFTWARE\EA Games", @"SOFTWARE\Electronic Arts" })
        {
            using var root = OpenHklm32(rootPath);
            if (root == null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                if (EaServiceKeys.Contains(name)) continue;
                using var key = root.OpenSubKey(name);
                var dir = PathUtil.NormalizeDir((key?.GetValue("Install Dir") ?? key?.GetValue("InstallDir")) as string);
                if (dir == null || !Directory.Exists(dir)) continue;
                yield return new GameRecord
                {
                    Id = "ea:" + name.ToLowerInvariant(),
                    Name = key?.GetValue("DisplayName") as string ?? name,
                    Source = GameSources.Ea,
                    InstallDir = dir,
                };
            }
        }
    }

    // ---------------- Ubisoft Connect ----------------

    static IEnumerable<GameRecord> ScanUbisoft()
    {
        using var root = OpenHklm32(@"SOFTWARE\Ubisoft\Launcher\Installs");
        if (root == null) yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(id);
            var dir = PathUtil.NormalizeDir(key?.GetValue("InstallDir") as string);
            if (dir == null || !Directory.Exists(dir)) continue;
            yield return new GameRecord
            {
                Id = "ubi:" + id,
                Name = Path.GetFileName(dir),
                Source = GameSources.Ubisoft,
                StoreId = id,
                InstallDir = dir,
            };
        }
    }

    // ---------------- Xbox / Microsoft Store ----------------

    static IEnumerable<GameRecord> ScanXbox()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;

            foreach (var gameDir in Directory.EnumerateDirectories(root))
            {
                var config = new[] { Path.Combine(gameDir, "Content", "MicrosoftGame.config"), Path.Combine(gameDir, "MicrosoftGame.config") }
                    .FirstOrDefault(File.Exists);
                if (config == null) continue;

                var folderName = Path.GetFileName(gameDir);
                var name = folderName;
                try
                {
                    var shell = XDocument.Load(config).Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
                    var display = shell?.Attribute("DefaultDisplayName")?.Value;
                    if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                        name = display;
                }
                catch
                {
                    // имя папки подойдёт
                }

                yield return new GameRecord
                {
                    Id = "xbox:" + folderName.ToLowerInvariant(),
                    Name = name,
                    Source = GameSources.Xbox,
                    InstallDir = PathUtil.NormalizeDir(gameDir),
                };
            }
        }
    }

    // ---------------- Rockstar Games Launcher ----------------

    static readonly HashSet<string> RockstarServiceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Launcher", "Rockstar Games Social Club", "Social Club", "Steam",
    };

    static IEnumerable<GameRecord> ScanRockstar()
    {
        using var root = OpenHklm32(@"SOFTWARE\Rockstar Games");
        if (root == null) yield break;
        foreach (var name in root.GetSubKeyNames())
        {
            if (RockstarServiceKeys.Contains(name)) continue;
            using var key = root.OpenSubKey(name);
            var dir = PathUtil.NormalizeDir(key?.GetValue("InstallFolder") as string);
            if (dir == null || !Directory.Exists(dir)) continue;
            yield return new GameRecord
            {
                Id = "rockstar:" + name.ToLowerInvariant(),
                Name = name,
                Source = GameSources.Rockstar,
                InstallDir = dir,
            };
        }
    }

    // ---------------- Пользовательские папки и exe ----------------

    [GeneratedRegex(@"crash|report|unins|setup|redist|dxsetup|directx|dotnet|prereq|easyanticheat|eac_|battleye|be_service|cefprocess|cefsharp|webhelper|helper|updater|update|patcher|install|bugsplat|crs-|activation|cleanup|touchup|dxwebsetup|oalinst|physx|vulkan|7z|notification",
        RegexOptions.IgnoreCase)]
    private static partial Regex HelperExe();

    public static bool IsHelperExe(string fileName) => HelperExe().IsMatch(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>
    /// Папка с одной игрой (exe лежит прямо в ней) или папка с играми (каждая подпапка с exe — отдельная игра).
    /// </summary>
    public static IEnumerable<GameRecord> ScanFolder(string folder)
    {
        var root = PathUtil.NormalizeDir(folder);
        if (root == null || !Directory.Exists(root)) yield break;

        if (SafeFiles(root, "*.exe", 0).Any(f => !IsHelperExe(f)))
        {
            yield return FolderGame(root);
            yield break;
        }

        foreach (var sub in SafeDirs(root))
            if (SafeFiles(sub, "*.exe", 3).Any(f => !IsHelperExe(f)))
                yield return FolderGame(sub);
    }

    static GameRecord FolderGame(string dir) => new()
    {
        Id = "folder:" + dir.ToLowerInvariant(),
        Name = Path.GetFileName(dir),
        Source = GameSources.Folder,
        InstallDir = dir,
    };

    public static GameRecord FromExe(string exePath)
    {
        var full = Path.GetFullPath(exePath);
        string? name = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(full);
            name = info.ProductName?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 60) name = info.FileDescription?.Trim();
        }
        catch
        {
            // имя файла подойдёт
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60) name = Path.GetFileNameWithoutExtension(full);

        return new GameRecord
        {
            Id = "exe:" + full.ToLowerInvariant(),
            Name = name,
            Source = GameSources.Manual,
            ExePath = full,
        };
    }

    static IEnumerable<string> SafeFiles(string dir, string pattern, int depth)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = depth > 0,
            MaxRecursionDepth = depth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        try { return Directory.EnumerateFiles(dir, pattern, options); }
        catch { return []; }
    }

    static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { IgnoreInaccessible = true }); }
        catch { return []; }
    }

    static RegistryKey? OpenHklm32(string path)
    {
        try { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(path); }
        catch { return null; }
    }
}
