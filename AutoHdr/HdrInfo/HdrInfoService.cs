using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoHdr.Core;
using AutoHdr.Library;

namespace AutoHdr.HdrInfo;

public static class HdrRules
{
    public static HdrSupport? FromPcgw(string? value) => value switch
    {
        "true" or "limited" or "always on" => HdrSupport.Yes,
        "hackable" => HdrSupport.Maybe,
        "false" or "n/a" => HdrSupport.No,
        _ => null,
    };

    /// <summary>
    /// Итоговый статус: Steam или PCGamingWiki подтверждают HDR → «Есть» и авто-HDR включается сам.
    /// PCGamingWiki «hackable» или только упоминания в файлах → «Возможно», решает пользователь.
    /// </summary>
    public static void Apply(GameRecord game)
    {
        var pcgw = FromPcgw(game.PcgwValue);
        var sources = new List<string>();
        if (game.SteamHdrFlag == true) sources.Add("Steam");
        if (pcgw == HdrSupport.Yes) sources.Add("PCGamingWiki");

        HdrSupport status;
        if (sources.Count > 0) status = HdrSupport.Yes;
        else if (pcgw == HdrSupport.No) { status = HdrSupport.No; sources.Add("PCGamingWiki"); }
        else if (pcgw == HdrSupport.Maybe) { status = HdrSupport.Maybe; sources.Add("PCGamingWiki"); }
        else if (!string.IsNullOrEmpty(game.FileMarkers)) { status = HdrSupport.Maybe; sources.Add("файлы игры"); }
        else status = HdrSupport.Unknown;

        game.HdrSources = string.Join(", ", sources);
        game.Hdr = status;
        game.RaiseHdrDetails();
        if (!game.UserOverride) game.Enabled = status == HdrSupport.Yes;
    }
}

/// <summary>Проверяет поддержку HDR по трём источникам параллельно, с ограничением частоты запросов.</summary>
public sealed partial class HdrInfoService
{
    const string PcgwApi = "https://www.pcgamingwiki.com/w/api.php";
    static readonly HttpClient Http = CreateClient();

    readonly Throttle steamThrottle = new(TimeSpan.FromMilliseconds(1500));
    readonly Throttle pcgwThrottle = new(TimeSpan.FromMilliseconds(1100)); // лимит PCGamingWiki: 60 запросов в минуту

    static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 8_000_000, // страница PCGamingWiki — до 2 МБ, больше не ждём
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoHdrEnabler/1.0 (Windows desktop utility; automatic HDR switching)");
        return client;
    }

    /// <param name="ui">Выполняет действие в потоке интерфейса: записи игр меняются только там.</param>
    public async Task RefreshAsync(IReadOnlyList<GameRecord> games, bool online, Action<Action> ui,
        IProgress<(int Done, int Total)>? progress)
    {
        int passes = online ? 3 : 1;
        var remaining = new ConcurrentDictionary<GameRecord, int>(games.Select(g => KeyValuePair.Create(g, passes)));
        var failed = new ConcurrentDictionary<GameRecord, bool>();
        int done = 0;
        progress?.Report((0, games.Count));

        void PassFinished(GameRecord game)
        {
            if (remaining.AddOrUpdate(game, 0, (_, n) => n - 1) == 0)
                progress?.Report((Interlocked.Increment(ref done), games.Count));
        }

        var tasks = new List<Task> { Task.Run(() => LocalPass(games, ui, PassFinished)) };
        if (online)
        {
            tasks.Add(Task.Run(() => SteamPassAsync(games, ui, PassFinished, failed)));
            tasks.Add(Task.Run(() => PcgwPassAsync(games, ui, PassFinished, failed)));
        }
        await Task.WhenAll(tasks);

        ui(() =>
        {
            foreach (var game in games)
            {
                HdrRules.Apply(game);
                if (!failed.ContainsKey(game)) game.HdrCheckedAt = DateTime.Now;
            }
        });
    }

    // ---------------- Steam Store: категория 61 «HDR available» ----------------

    async Task SteamPassAsync(IReadOnlyList<GameRecord> games, Action<Action> ui, Action<GameRecord> finished,
        ConcurrentDictionary<GameRecord, bool> failed)
    {
        int errorsInRow = 0;
        foreach (var game in games)
        {
            try
            {
                if (game.Source != GameSources.Steam || game.StoreId == null) continue;
                // Пропущенную из-за сети игру проверим в следующий раз, а не через 30 дней
                if (errorsInRow >= 3) { failed[game] = true; continue; }
                var flag = await SteamHasHdrAsync(game.StoreId);
                ui(() => { game.SteamHdrFlag = flag; HdrRules.Apply(game); });
                errorsInRow = 0;
            }
            catch (Exception ex)
            {
                failed[game] = true;
                if (++errorsInRow == 3) Log.Warn($"Steam Store недоступен, остальные игры пропущены: {ex.Message}");
            }
            finally
            {
                finished(game);
            }
        }
    }

    async Task<bool?> SteamHasHdrAsync(string appId)
    {
        await steamThrottle.WaitAsync();
        var url = $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appId)}&filters=categories&cc=us";
        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty(appId, out var app)) return null;
        if (!app.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) return null;
        if (!app.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
        if (!data.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array) return false;
        return categories.EnumerateArray().Any(c =>
            c.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 61);
    }

    // ---------------- PCGamingWiki: поле hdr в шаблоне {{Video}} ----------------

    async Task PcgwPassAsync(IReadOnlyList<GameRecord> games, Action<Action> ui, Action<GameRecord> finished,
        ConcurrentDictionary<GameRecord, bool> failed)
    {
        int errorsInRow = 0;
        foreach (var game in games)
        {
            try
            {
                if (errorsInRow >= 3) { failed[game] = true; continue; }
                var page = game.Source switch
                {
                    GameSources.Steam when game.StoreId != null => await PcgwLookupAsync("steamappid", game.StoreId),
                    GameSources.Gog when game.StoreId != null => await PcgwLookupAsync("gogid", game.StoreId),
                    _ => null,
                };
                page ??= await PcgwSearchAsync(game.Name);

                (string? Value, string? Notes) info = (null, null);
                if (page != null) info = await PcgwHdrAsync(page);

                ui(() =>
                {
                    game.PcgwPage = page;
                    game.PcgwValue = info.Value;
                    game.PcgwNotes = info.Notes;
                    HdrRules.Apply(game);
                });
                errorsInRow = 0;
            }
            catch (Exception ex)
            {
                failed[game] = true;
                if (++errorsInRow == 3) Log.Warn($"PCGamingWiki недоступна, остальные игры пропущены: {ex.Message}");
            }
            finally
            {
                finished(game);
            }
        }
    }

    async Task<JsonDocument> PcgwGetAsync(string query)
    {
        for (int attempt = 0; ; attempt++)
        {
            await pcgwThrottle.WaitAsync();
            using var response = await Http.GetAsync($"{PcgwApi}?{query}&format=json");
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(61)); // PCGamingWiki блокирует IP на 60 с
                continue;
            }
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
    }

    async Task<string?> PcgwLookupAsync(string field, string value)
    {
        using var doc = await PcgwGetAsync($"action=idlookup&field={field}&value={Uri.EscapeDataString(value)}");
        if (!doc.RootElement.TryGetProperty("idlookup", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in list.EnumerateArray())
        {
            var page = FindString(item, "Page");
            if (!string.IsNullOrWhiteSpace(page)) return page;
        }
        return null;
    }

    async Task<string?> PcgwSearchAsync(string name)
    {
        var wanted = NormalizeTitle(name);
        if (wanted.Length == 0) return null;
        using var doc = await PcgwGetAsync($"action=opensearch&namespace=0&limit=5&search={Uri.EscapeDataString(CleanName(name))}");
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return null;
        foreach (var title in root[1].EnumerateArray())
        {
            var t = title.GetString();
            if (t != null && NormalizeTitle(t) == wanted) return t;
        }
        return null;
    }

    async Task<(string? Value, string? Notes)> PcgwHdrAsync(string page)
    {
        using var doc = await PcgwGetAsync(
            $"action=parse&prop=wikitext&redirects=1&formatversion=2&page={Uri.EscapeDataString(page)}");
        if (!doc.RootElement.TryGetProperty("parse", out var parse)) return (null, null);
        var text = parse.TryGetProperty("wikitext", out var w) ? w.GetString() : null;
        if (string.IsNullOrEmpty(text)) return (null, null);

        text = HtmlComment().Replace(text, "");
        var value = HdrField().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim().ToLowerInvariant() : null;
        var notes = HdrNotesField().Match(text) is { Success: true } n ? CleanWikiText(n.Groups[1].Value) : null;
        return (string.IsNullOrEmpty(value) ? null : value, string.IsNullOrEmpty(notes) ? null : notes);
    }

    static string? FindString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (p.NameEquals(name) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                var nested = FindString(p.Value, name);
                if (nested != null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, name);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    static string CleanName(string name) => TrademarkChars().Replace(name, "").Trim();

    static string NormalizeTitle(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in CleanName(name).ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    static string CleanWikiText(string s)
    {
        s = RefSelfClosing().Replace(s, "");
        s = RefBlock().Replace(s, "");
        s = NoteTemplate().Replace(s, "$1");
        for (int i = 0; i < 3; i++) s = AnyTemplate().Replace(s, "");
        s = WikiLink().Replace(s, "$1");
        s = ExternalLink().Replace(s, "$1");
        s = Regex.Replace(s, "'{2,}", "");
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd(',', ';').Trim();
        return s.Length > 400 ? s[..397] + "…" : s;
    }

    [GeneratedRegex(@"(?m)^\|\s*hdr\s*=\s*([^\r\n|]*)", RegexOptions.IgnoreCase)]
    private static partial Regex HdrField();

    [GeneratedRegex(@"(?m)^\|\s*hdr notes\s*=\s*([^\r\n]*)", RegexOptions.IgnoreCase)]
    private static partial Regex HdrNotesField();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"<ref[^>]*/>", RegexOptions.IgnoreCase)]
    private static partial Regex RefSelfClosing();

    [GeneratedRegex(@"<ref[^>]*>.*?</ref>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RefBlock();

    [GeneratedRegex(@"\{\{\s*note\s*\|\s*(?:note\s*=\s*)?([^{}]*)\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex NoteTemplate();

    [GeneratedRegex(@"\{\{[^{}]*\}\}")]
    private static partial Regex AnyTemplate();

    [GeneratedRegex(@"\[\[(?:[^|\]]*\|)?([^\]]+)\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"\[https?://\S+\s+([^\]]+)\]")]
    private static partial Regex ExternalLink();

    [GeneratedRegex("[™®©]")]
    private static partial Regex TrademarkChars();

    // ---------------- Файлы игры: упоминания настроек HDR в exe ----------------

    static readonly string[] Markers = ["PaperWhite", "paper_white", "PeakBrightness", "HDR10", "ST2084", "scRGB", "r.HDR.EnableHDROutput"];

    static readonly (string Name, byte[] Ascii, byte[] Utf16)[] MarkerBytes =
        Markers.Select(m => (m, Encoding.ASCII.GetBytes(m), Encoding.Unicode.GetBytes(m))).ToArray();

    static void LocalPass(IReadOnlyList<GameRecord> games, Action<Action> ui, Action<GameRecord> finished)
    {
        foreach (var game in games)
        {
            try
            {
                var markers = FindMarkers(game);
                ui(() => { game.FileMarkers = markers; HdrRules.Apply(game); });
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось проверить файлы «{game.Name}»: {ex.Message}");
            }
            finally
            {
                finished(game);
            }
        }
    }

    static string? FindMarkers(GameRecord game)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var exe in CandidateExes(game))
        {
            try { ScanFile(exe, found); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found.Count == 0 ? null : string.Join(", ", found);
    }

    static List<string> CandidateExes(GameRecord game)
    {
        // Пути \\сервер\… и \\.\устройство не читаем: список игр правит обычный пользователь, а читать может копия с правами администратора
        if (game.ExePath != null) return !IsUncOrDevice(game.ExePath) && File.Exists(game.ExePath) ? [game.ExePath] : [];
        if (game.InstallDir == null || IsUncOrDevice(game.InstallDir) || !Directory.Exists(game.InstallDir)) return [];

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        return new DirectoryInfo(game.InstallDir).EnumerateFiles("*.exe", options)
            .Where(f => f.Length is > 3_000_000 and < 400_000_000 && !LibraryScanner.IsHelperExe(f.Name))
            .OrderByDescending(f => f.Length)
            .Take(2)
            .Select(f => f.FullName)
            .ToList();
    }

    static bool IsUncOrDevice(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    static void ScanFile(string path, ISet<string> found)
    {
        int overlap = MarkerBytes.Max(m => m.Utf16.Length) - 1;
        var buffer = new byte[8 << 20];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.SequentialScan);
        int carry = 0;
        while (true)
        {
            int read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read <= 0) break;
            int length = carry + read;
            var span = buffer.AsSpan(0, length);
            foreach (var (name, ascii, utf16) in MarkerBytes)
            {
                if (found.Contains(name)) continue;
                if (span.IndexOf(ascii) >= 0 || span.IndexOf(utf16) >= 0) found.Add(name);
            }
            if (found.Count == MarkerBytes.Length) break;
            carry = Math.Min(overlap, length);
            Buffer.BlockCopy(buffer, length - carry, buffer, 0, carry);
        }
    }

    sealed class Throttle(TimeSpan interval)
    {
        readonly SemaphoreSlim gate = new(1, 1);
        DateTime last = DateTime.MinValue;

        public async Task WaitAsync()
        {
            await gate.WaitAsync();
            try
            {
                var wait = last + interval - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                last = DateTime.UtcNow;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
