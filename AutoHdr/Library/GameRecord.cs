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

    static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

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
        HdrSupport.Yes => "Есть",
        HdrSupport.Maybe => "Возможно",
        HdrSupport.No => "Нет",
        _ => "Неизвестно",
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
                    sb.Append("HDR поддерживается, источник: ").Append(HdrSources);
                    break;
                case HdrSupport.No:
                    sb.Append("HDR не поддерживается, источник: PCGamingWiki");
                    break;
                case HdrSupport.Maybe when PcgwValue == "hackable":
                    sb.Append("PCGamingWiki: HDR можно включить сторонним модом");
                    break;
                case HdrSupport.Maybe:
                    sb.Append("В файлах игры есть упоминания HDR: ").Append(FileMarkers)
                      .Append(". Проверьте настройки графики в самой игре");
                    break;
                default:
                    sb.Append(HdrCheckedAt == null ? "Поддержку HDR ещё не проверяли" : "Нет данных о поддержке HDR");
                    break;
            }
            if (!string.IsNullOrWhiteSpace(PcgwNotes)) sb.Append("\n\nPCGamingWiki: ").Append(PcgwNotes);
            if (HdrCheckedAt != null) sb.Append(string.Create(Ru, $"\n\nПроверено {HdrCheckedAt:d MMMM yyyy}"));
            return sb.ToString();
        }
    }

    [JsonIgnore] public string Location => ExePath ?? InstallDir ?? "";
    [JsonIgnore] public bool IsCustom => Source is GameSources.Folder or GameSources.Manual;
    [JsonIgnore] public string SourceText => Missing ? $"{Source}, не установлена" : Source;

    public void RaiseHdrDetails()
    {
        Raise(nameof(HdrTooltip));
        Raise(nameof(HdrText));
    }
}
