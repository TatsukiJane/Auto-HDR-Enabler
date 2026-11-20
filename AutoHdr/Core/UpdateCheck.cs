using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace AutoHdr.Core;

/// <summary>Asks GitHub for the latest release and compares it with this build.</summary>
public static class UpdateCheck
{
    const string Repo = "TatsukiJane/Auto-HDR-Enabler";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1_000_000 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoHdrEnabler/1.0 (Windows desktop utility; update check)"); // GitHub rejects requests without one
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>This build as major.minor.patch.</summary>
    public static Version Current => Trim(Assembly.GetExecutingAssembly().GetName().Version!);

    /// <summary>The latest release if it is newer than this build, otherwise null. Throws on network errors.</summary>
    public static async Task<Version?> NewerReleaseAsync()
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = Trim(latest);
        return latest > Current ? latest : null;
    }

    /// <summary>
    /// The release page is built from the parsed version, not taken from the response:
    /// the tray runs with administrator rights and opens only links to this repository.
    /// </summary>
    public static string ReleasePage(Version version) => $"https://github.com/{Repo}/releases/tag/v{version}";

    // 1.0.10.0 from the assembly and 1.0.10 from the tag must compare as equal
    static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
