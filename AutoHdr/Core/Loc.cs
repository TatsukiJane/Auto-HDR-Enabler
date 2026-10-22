using System.Globalization;

namespace AutoHdr.Core;

/// <summary>
/// Язык интерфейса и журнала: русский или английский. Строки лежат парами прямо в коде: <c>Loc.T("Игры", "Games")</c>,
/// в разметке — <c>{ui:Tr 'Игры', 'Games'}</c>.
/// </summary>
public static class Loc
{
    /// <summary>Пока настройки не прочитаны (--install-task, --exit), язык берётся из Windows.</summary>
    public static bool English { get; private set; } = !WindowsPrefersRussian();

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(English ? "en-US" : "ru-RU");

    /// <summary>"ru", "en" или null — как в Windows.</summary>
    public static void Use(string? language) => English = language switch
    {
        "ru" => false,
        "en" => true,
        _ => !WindowsPrefersRussian(),
    };

    public static string T(string ru, string en) => English ? en : ru;

    // Русский и там, где Windows на языке страны, в которой русским обычно владеют лучше, чем английским
    static bool WindowsPrefersRussian() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "be" or "kk" or "ky" or "tg";
}
