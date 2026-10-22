using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AutoHdr.Core;
using AutoHdr.Library;

namespace AutoHdr.UI;

/// <summary>Строка вкладки «Мониторы»: запись из реестра мониторов плюс текущее состояние.</summary>
public sealed class MonitorRow : Observable
{
    readonly Action<string, bool> setAuto;

    public MonitorRow(string key, Action<string, bool> setAuto)
    {
        Key = key;
        this.setAuto = setAuto;
    }

    public string Key { get; }
    public string Name { get; set => Set(ref field, value); } = "";
    public string Details { get; set => Set(ref field, value); } = "";
    public bool Connected { get; set { if (Set(ref field, value)) Raise(nameof(StateText)); } }
    public bool HdrSupported { get; set { if (Set(ref field, value)) Raise(nameof(StateText)); } }
    public bool HdrOn { get; set { if (Set(ref field, value)) Raise(nameof(StateText)); } }
    public bool CanToggle => Connected && HdrSupported;
    public bool CanForget => !Connected;

    bool silent;

    public bool AutoHdr
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            if (!silent) setAuto(Key, value);
        }
    }

    public string StateText => !Connected ? "Не подключён"
        : !HdrSupported ? "HDR не поддерживается"
        : HdrOn ? "HDR включён" : "HDR выключен";

    /// <summary>Обновить флажок значением из реестра мониторов, не записывая его обратно.</summary>
    public void SetAutoSilently(bool value)
    {
        silent = true;
        try { AutoHdr = value; }
        finally { silent = false; }
    }

    public void RaiseAll()
    {
        Raise(nameof(CanToggle));
        Raise(nameof(CanForget));
        Raise(nameof(StateText));
    }
}

public sealed class HdrBrushConverter : IValueConverter
{
    static readonly Brush Yes = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)));
    static readonly Brush Maybe = Freeze(new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22)));
    static readonly Brush No = Freeze(new SolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E)));
    static readonly Brush Unknown = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        HdrSupport.Yes => Yes,
        HdrSupport.Maybe => Maybe,
        HdrSupport.No => No,
        _ => Unknown,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}

public sealed class BoolToVisibleConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
