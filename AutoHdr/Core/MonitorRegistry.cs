namespace AutoHdr.Core;

public sealed class MonitorRecord
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string EdidId { get; set; } = "";
    public string Connection { get; set; } = "";
    public bool AutoHdr { get; set; }
    public bool HdrSupported { get; set; }
    public bool IsVirtual { get; set; }
}

public sealed class MonitorRegistryData
{
    public bool Initialized { get; set; }
    public List<MonitorRecord> Monitors { get; set; } = [];
}

/// <summary>
/// Every display ever connected and the “Auto HDR” flag for each.
/// The flag turns on by itself only for the first physical HDR display found that does not look like a TV.
/// TVs, virtual displays and all displays connected after it are added with the flag off.
/// </summary>
public sealed partial class MonitorRegistry
{
    readonly object gate = new();
    readonly MonitorRegistryData data;

    MonitorRegistry(MonitorRegistryData data) => this.data = data;

    public event Action? Changed;

    /// <summary>A display recognized by EDID and name under a new device path: old key, new key.</summary>
    public event Action<string, string>? KeyChanged;

    public static MonitorRegistry Load() => new(JsonStore.Load<MonitorRegistryData>(AppPaths.MonitorsFile));

    public IReadOnlyList<MonitorRecord> All
    {
        get { lock (gate) return data.Monitors.ToList(); }
    }

    public bool IsAuto(DisplayInfo display)
    {
        lock (gate) return FindLocked(display)?.AutoHdr == true;
    }

    /// <summary>Displays where the app may switch HDR.</summary>
    public List<DisplayInfo> Targets(IEnumerable<DisplayInfo> displays) =>
        displays.Where(d => d.HdrSupported && IsAuto(d)).ToList();

    public void Sync(IReadOnlyList<DisplayInfo> displays)
    {
        bool changed = false;
        var rekeyed = new List<(string Old, string New)>();
        lock (gate)
        {
            foreach (var d in displays)
            {
                var record = FindLocked(d);
                if (record == null)
                {
                    bool tv = LooksLikeTv(d.Name);
                    record = new MonitorRecord
                    {
                        Key = d.Key,
                        AutoHdr = !data.Initialized && d.HdrSupported && !d.IsVirtual && !tv,
                    };
                    data.Monitors.Add(record);
                    if (record.AutoHdr) data.Initialized = true;
                    changed = true;
                    Log.Info(record.AutoHdr ? Loc.T($"Монитор «{d.Name}» добавлен, авто-HDR включён", $"Display “{d.Name}” added, auto HDR on")
                        : tv ? Loc.T($"«{d.Name}» похож на телевизор: добавлен с выключенным авто-HDR", $"“{d.Name}” looks like a TV: added with auto HDR off")
                        : Loc.T($"Дисплей «{d.Name}» ({d.Connection}) добавлен с выключенным авто-HDR", $"Display “{d.Name}” ({d.Connection}) added with auto HDR off"));
                }

                if (record.Key != d.Key || record.Name != d.Name || record.EdidId != d.EdidId
                    || record.Connection != d.Connection || record.HdrSupported != d.HdrSupported
                    || record.IsVirtual != d.IsVirtual)
                {
                    if (record.Key != d.Key) rekeyed.Add((record.Key, d.Key));
                    record.Key = d.Key;
                    record.Name = d.Name;
                    record.EdidId = d.EdidId;
                    record.Connection = d.Connection;
                    record.HdrSupported = d.HdrSupported;
                    record.IsVirtual = d.IsVirtual;
                    changed = true;
                }
            }

            if (changed) SaveLocked();
        }
        foreach (var (oldKey, newKey) in rekeyed) KeyChanged?.Invoke(oldKey, newKey);
        if (changed) Changed?.Invoke();
    }

    public void SetAuto(string key, bool on)
    {
        lock (gate)
        {
            var record = data.Monitors.FirstOrDefault(m => m.Key == key);
            if (record == null || record.AutoHdr == on) return;
            record.AutoHdr = on;
            data.Initialized = true; // the user picked displays: new ones no longer turn on by themselves
            SaveLocked();
            Log.Info(Loc.T($"Авто-HDR для «{record.Name}» {(on ? "включён" : "выключен")}", $"Auto HDR for “{record.Name}” turned {(on ? "on" : "off")}"));
        }
        Changed?.Invoke();
    }

    public void Forget(string key)
    {
        lock (gate)
        {
            if (data.Monitors.RemoveAll(m => m.Key == key) == 0) return;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>TVs usually call themselves “LG TV”, “SONY TV”, “SAMSUNG”, “BRAVIA”.</summary>
    public static bool LooksLikeTv(string name) =>
        TvName().IsMatch(name)
        || name.Trim().ToUpperInvariant() is "SAMSUNG" or "HISENSE" or "TCL" or "PHILIPS" or "VIZIO" or "PANASONIC" or "TOSHIBA-TV";

    [System.Text.RegularExpressions.GeneratedRegex(@"\bTV\b|\bFTV\b|BRAVIA|Television|\bOLED\d{2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TvName();

    MonitorRecord? FindLocked(DisplayInfo d)
    {
        var byKey = data.Monitors.FirstOrDefault(m => m.Key == d.Key);
        if (byKey != null) return byKey;

        // The same display on another port or after a driver reinstall: EDID and name match
        if (string.IsNullOrEmpty(d.EdidId)) return null;
        var byEdid = data.Monitors.Where(m => m.EdidId == d.EdidId && m.Name == d.Name).ToList();
        return byEdid.Count == 1 ? byEdid[0] : null;
    }

    void SaveLocked()
    {
        try { JsonStore.Save(AppPaths.MonitorsFile, data); }
        catch (Exception ex) { Log.Error(Loc.T("Не удалось сохранить список мониторов", "Could not save the display list"), ex); }
    }
}
