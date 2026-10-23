using System.Globalization;

namespace AutoHdr.Core;

/// <summary>
/// UI and log language: Russian or English. Strings live in pairs right in the code: <c>Loc.T("Игры", "Games")</c>,
/// in markup — <c>{ui:Tr 'Игры', 'Games'}</c>.
/// </summary>
public static class Loc
{
    /// <summary>Until the settings are read (--install-task, --exit), the language comes from Windows.</summary>
    public static bool English { get; private set; } = !WindowsPrefersRussian();

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(English ? "en-US" : "ru-RU");

    /// <summary>"ru", "en" or null — same as Windows.</summary>
    public static void Use(string? language) => English = language switch
    {
        "ru" => false,
        "en" => true,
        _ => !WindowsPrefersRussian(),
    };

    public static string T(string ru, string en) => English ? en : ru;

    // Russian also where Windows is in the language of a country where people usually know Russian better than English
    static bool WindowsPrefersRussian() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "be" or "kk" or "ky" or "tg";
}
