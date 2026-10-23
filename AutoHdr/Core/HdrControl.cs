using System.Runtime.InteropServices;
using static AutoHdr.Core.Native;

namespace AutoHdr.Core;

/// <summary>An active display and its HDR state at the time of the poll.</summary>
public sealed record DisplayInfo(
    string Key,
    string Name,
    string EdidId,
    uint OutputTechnology,
    LUID AdapterId,
    uint TargetId,
    bool HdrSupported,
    bool HdrOn,
    bool HdrActive,
    bool IsVirtual)
{
    public string Connection => HdrControl.DescribeConnection(OutputTechnology);
}

/// <summary>Reads and switches HDR through the DisplayConfig API. No administrator rights needed.</summary>
public static class HdrControl
{
    const uint OutputOther = 0xFFFFFFFF;
    const uint OutputIndirectWired = 16;
    const uint OutputIndirectVirtual = 17;
    const uint ColorModeHdr = 2;

    static readonly string[] VirtualHints = ["virtual", "parsec", "sudomaker", "iddsample", "vdd", "spacedesk", "duet", "luna"];

    public static List<DisplayInfo> GetDisplays()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                return [];

            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            int result = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == ERROR_INSUFFICIENT_BUFFER) continue;
            if (result != 0) return [];

            var list = new List<DisplayInfo>();
            var seen = new HashSet<(long, uint)>();
            for (int i = 0; i < pathCount; i++)
            {
                var target = paths[i].TargetInfo;
                if (!seen.Add((target.AdapterId.Value, target.Id))) continue; // clone mode
                list.Add(Describe(target, list.Count + 1));
            }
            return list;
        }
        return [];
    }

    static DisplayInfo Describe(PathTargetInfo target, int index)
    {
        var name = new TargetDeviceName
        {
            Header = Header(DeviceInfoType.GetTargetName, Marshal.SizeOf<TargetDeviceName>(), target.AdapterId, target.Id),
        };
        bool named = DisplayConfigGetDeviceInfo(ref name) == 0;

        var friendly = named && !string.IsNullOrWhiteSpace(name.MonitorFriendlyDeviceName)
            ? name.MonitorFriendlyDeviceName.Trim()
            : Loc.T($"Дисплей {index}", $"Display {index}");
        var key = named && !string.IsNullOrWhiteSpace(name.MonitorDevicePath)
            ? name.MonitorDevicePath
            : $"target:{target.AdapterId.Value:X}:{target.Id}";
        var edid = named && (name.Flags & 0x4) != 0 ? DecodeEdid(name.EdidManufactureId, name.EdidProductCodeId) : "";
        uint tech = named ? name.OutputTechnology : target.OutputTechnology;

        var (supported, on, active) = ReadHdr(target.AdapterId, target.Id);
        bool isVirtual = tech is OutputIndirectWired or OutputIndirectVirtual or OutputOther
            || VirtualHints.Any(h => friendly.Contains(h, StringComparison.OrdinalIgnoreCase)
                                     || key.Contains(h, StringComparison.OrdinalIgnoreCase));

        return new DisplayInfo(key, friendly, edid, tech, target.AdapterId, target.Id, supported, on, active, isVirtual);
    }

    /// <summary>Returns (HDR supported, HDR turned on by the user, HDR actually active).</summary>
    static (bool Supported, bool On, bool Active) ReadHdr(LUID adapter, uint id)
    {
        var info2 = new AdvancedColorInfo2
        {
            Header = Header(DeviceInfoType.GetAdvancedColorInfo2, Marshal.SizeOf<AdvancedColorInfo2>(), adapter, id),
        };
        if (DisplayConfigGetDeviceInfo(ref info2) == 0)
        {
            bool supported = (info2.Value & 0x10) != 0;   // highDynamicRangeSupported
            bool userEnabled = (info2.Value & 0x20) != 0; // highDynamicRangeUserEnabled
            bool active = info2.ActiveColorMode == ColorModeHdr;
            return (supported, userEnabled || active, active);
        }

        // Windows before 24H2
        var info = new AdvancedColorInfo
        {
            Header = Header(DeviceInfoType.GetAdvancedColorInfo, Marshal.SizeOf<AdvancedColorInfo>(), adapter, id),
        };
        if (DisplayConfigGetDeviceInfo(ref info) != 0) return (false, false, false);
        bool wideColorEnforced = (info.Value & 0x4) != 0;
        bool sup = (info.Value & 0x1) != 0 && !wideColorEnforced;
        bool enabled = (info.Value & 0x2) != 0 && !wideColorEnforced;
        return (sup, enabled, enabled);
    }

    public static bool SetHdr(DisplayInfo display, bool on)
    {
        var request = new SetState
        {
            Header = Header(DeviceInfoType.SetHdrState, Marshal.SizeOf<SetState>(), display.AdapterId, display.TargetId),
            Value = on ? 1u : 0u,
        };
        int result = DisplayConfigSetDeviceInfo(ref request);
        if (result != 0)
        {
            request.Header.Type = DeviceInfoType.SetAdvancedColorState;
            result = DisplayConfigSetDeviceInfo(ref request);
        }
        if (result != 0) Log.Warn(Loc.T($"Не удалось {(on ? "включить" : "выключить")} HDR на «{display.Name}», код {result}",
            $"Could not turn HDR {(on ? "on" : "off")} on “{display.Name}”, code {result}"));
        return result == 0;
    }

    /// <summary>Waits until HDR becomes active or inactive on the given displays.</summary>
    public static async Task<bool> WaitForStateAsync(IReadOnlyCollection<string> keys, bool on, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var displays = GetDisplays().Where(d => keys.Contains(d.Key)).ToList();
            if (displays.All(d => d.HdrActive == on && d.HdrOn == on)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(150);
        }
    }

    public static bool WaitForState(IReadOnlyCollection<string> keys, bool on, TimeSpan timeout) =>
        WaitForStateAsync(keys, on, timeout).GetAwaiter().GetResult();

    static DeviceInfoHeader Header(DeviceInfoType type, int size, LUID adapter, uint id) =>
        new() { Type = type, Size = (uint)size, AdapterId = adapter, Id = id };

    /// <summary>EDID ID like “AUS27F1”: three letters of the PnP manufacturer and the model code.</summary>
    static string DecodeEdid(ushort manufacturer, ushort product)
    {
        static string Letters(int v)
        {
            char a = (char)('@' + ((v >> 10) & 0x1F));
            char b = (char)('@' + ((v >> 5) & 0x1F));
            char c = (char)('@' + (v & 0x1F));
            return $"{a}{b}{c}";
        }

        static bool Valid(string s) => s.All(ch => ch is >= 'A' and <= 'Z');

        // Windows stores the EDID bytes big-endian inside a ushort
        var swapped = Letters(((manufacturer & 0xFF) << 8) | (manufacturer >> 8));
        var letters = Valid(swapped) ? swapped : Letters(manufacturer);
        return Valid(letters) ? $"{letters}{product:X4}" : $"{manufacturer:X4}{product:X4}";
    }

    public static string DescribeConnection(uint tech) => tech switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        9 => "SDI",
        10 => "DisplayPort",
        11 => Loc.T("DisplayPort, встроенный", "DisplayPort, internal"),
        12 => "UDI",
        13 => Loc.T("UDI, встроенный", "UDI, internal"),
        15 => "Miracast",
        OutputIndirectWired or OutputIndirectVirtual => Loc.T("Виртуальный", "Virtual"),
        0x80000000 => Loc.T("Встроенный экран", "Built-in screen"),
        _ => Loc.T("Другое", "Other"),
    };
}
