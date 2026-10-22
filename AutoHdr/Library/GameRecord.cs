using System.Text;
using System.Text.Json.Serialization;
using AutoHdr.Core;

namespace AutoHdr.Library;

public enum HdrSupport { Unknown, Yes, Maybe, No }

public static class GameSources
{
    public const string Steam = "Steam";
    public const string Gog = "GOG";
    public const string Epic = "Epic";
    public const string Ea = "EA";
    public const string Ubisoft = "Ubisoft";
    public const string Xbox = "Xbox";
    public const string Rockstar = "Rockstar";
    public const string Folder = "Папка";
    public const string Manual = "Вручную";

    /// <summary>Порядок при дублях: одна и та же папка из двух источников остаётся за первым.</summary>
    public static readonly string[] Priority = [Steam, Gog, Epic, Ea, Ubisoft, Xbox, Rockstar, Folder, Manual];

    /// <summary>Название источника на языке интерфейса: в games.json «Папка» и «Вручную» хранятся по-русски.</summary>
    public static string Display(string source) => source switch
    {
        Folder => Loc.T("Папка", "Folder"),
        Manual => Loc.T("Вручную", "Manual"),
        _ => source,
    };
}

public sealed class GameRecord : Observable
{
    public string Id { get; set; } = "";
    public string Name { get; set => Set(ref field, value); } = "";
    public string Source { get; set; } = "";
    public string? StoreId { get; set; }
    public string? InstallDir { get; set => Set(ref field, value); }
    public string? ExePath { get; set; }

    public bool Enabled
    {
        get;
        set { if (Set(ref field, value)) Raise(nameof(EnabledUi)); }
    }

    /// <summary>Пользователь сам переключал авто-HDR: автоматические правила больше не трогают флаг.</summary>
    public bool UserOverride { get; set; }

    public bool HoldUntilHdr
    {
        get;
        set { if (Set(ref field, value)) Raise(nameof(HoldUi)); }
    }

    public bool Missing
    {
        get;
        set { if (Set(ref field, value)) Raise(nameof(SourceText)); }
    }

    // ---- Данные о поддержке HDR ----

    public HdrSupport Hdr
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(HdrText));
            Raise(nameof(HdrSortKey));
        }
    }

    public string? HdrSources { get; set; }
    public bool? SteamHdrFlag { get; set; }
    public string? PcgwPage { get; set; }
    public string? PcgwValue { get; set; }
    public string? PcgwNotes { get; set; }
    public string? FileMarkers { get; set; }

    public DateTime? HdrCheckedAt
    {
        get;
        set { if (Set(ref field, value)) Raise(nameof(HdrTooltip)); }
    }

    // ---- Для интерфейса ----

    /// <summary>Вызывается, когда пользователь сам меняет флаги в таблице.</summary>
    public static event Action<GameRecord>? UserChanged;

    [JsonIgnore]
    public bool EnabledUi
    {
        get => Enabled;
        set
        {
            if (Enabled == value) return;
            Enabled = value;
            UserOverride = true;
            UserChanged?.Invoke(this);
        }
    }

    [JsonIgnore]
    public bool HoldUi
    {
        get => HoldUntilHdr;
        set
        {
            if (HoldUntilHdr == value) return;
            HoldUntilHdr = value;
            UserChanged?.Invoke(this);
        }
    }

    [JsonIgnore]
    public string HdrText => Hdr switch
    {
        HdrSupport.Yes => Loc.T("Есть", "Yes"),
        HdrSupport.Maybe => Loc.T("Возможно", "Maybe"),
        HdrSupport.No => Loc.T("Нет", "No"),
        _ => Loc.T("Неизвестно", "Unknown"),
    };

    [JsonIgnore]
    public int HdrSortKey => Hdr switch
    {
        HdrSupport.Yes => 0,
        HdrSupport.Maybe => 1,
        HdrSupport.Unknown => 2,
        _ => 3,
    };

    [JsonIgnore]
    public string HdrTooltip
    {
        get
        {
            var sb = new StringBuilder();
            switch (Hdr)
            {
                case HdrSupport.Yes:
                    sb.Append(Loc.T("HDR поддерживается, источник: ", "HDR supported, source: ")).Append(HdrSources);
                    break;
                case HdrSupport.No:
                    sb.Append(Loc.T("HDR не поддерживается, источник: PCGamingWiki", "HDR not supported, source: PCGamingWiki"));
                    break;
                case HdrSupport.Maybe when PcgwValue == "hackable":
                    sb.Append(Loc.T("PCGamingWiki: HDR можно включить сторонним модом", "PCGamingWiki: HDR can be added with a third-party mod"));
                    break;
                case HdrSupport.Maybe:
                    sb.Append(Loc.T("В файлах игры есть упоминания HDR: ", "The game files mention HDR: ")).Append(FileMarkers)
                      .Append(Loc.T(". Проверьте настройки графики в самой игре", ". Check the graphics settings in the game itself"));
                    break;
                default:
                    sb.Append(HdrCheckedAt == null ? Loc.T("Поддержку HDR ещё не проверяли", "HDR support not checked yet") : Loc.T("Нет данных о поддержке HDR", "No data on HDR support"));
                    break;
            }
            if (!string.IsNullOrWhiteSpace(PcgwNotes)) sb.Append("\n\nPCGamingWiki: ").Append(PcgwNotes);
            if (HdrCheckedAt != null) sb.Append(string.Create(Loc.Culture, $"\n\n{Loc.T("Проверено", "Checked")} {HdrCheckedAt:d MMMM yyyy}"));
            return sb.ToString();
        }
    }

    [JsonIgnore] public string Location => ExePath ?? InstallDir ?? "";
    [JsonIgnore] public bool IsCustom => Source is GameSources.Folder or GameSources.Manual;
    [JsonIgnore] public string SourceText => Missing
        ? Loc.T($"{GameSources.Display(Source)}, не установлена", $"{GameSources.Display(Source)}, not installed")
        : GameSources.Display(Source);

    public void RaiseHdrDetails()
    {
        Raise(nameof(HdrTooltip));
        Raise(nameof(HdrText));
    }
}
