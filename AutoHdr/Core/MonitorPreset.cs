using static AutoHdr.Core.Native;

namespace AutoHdr.Core;

/// <summary>
/// Monitor picture mode over DDC/CI (dxva2.dll). No administrator rights needed.
/// The monitor sometimes does not answer (about 6% of requests on the P275MS+), so every read is retried.
/// </summary>
public static class MonitorPreset
{
    // shortcut: only monitors that keep the picture mode in VCP code E0 (checked on Titan Army P275MS+); store a code per monitor when another model needs it
    public const byte Code = 0xE0;

    static readonly object Gate = new();

    /// <summary>
    /// The current picture mode, or null if the monitor did not answer or was not found.
    /// <paramref name="timeout"/> — how long to keep asking: right after an HDR switch the monitor is silent for a few seconds.
    /// </summary>
    public static int? Read(string displayKey, TimeSpan timeout = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (Gate)
        {
            while (true)
            {
                var value = WithMonitor(displayKey, ReadLocked, null);
                if (value != null || DateTime.UtcNow >= deadline) return value;
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>Sets the picture mode and confirms it by reading it back. Right after an HDR switch the monitor is silent for a few seconds.</summary>
    public static bool Write(string displayKey, int value, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (Gate)
        {
            while (true)
            {
                if (WithMonitor(displayKey, h => SetVCPFeature(h, Code, (uint)value) && Confirm(h, value), false)) return true;
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(300);
            }
        }
    }

    static bool Confirm(IntPtr handle, int value)
    {
        // The monitor applies the mode within a few hundred milliseconds: until then it reports the old one
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(200);
            if (ReadLocked(handle) == value) return true;
        }
        return false;
    }

    static int? ReadLocked(IntPtr handle)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0) Thread.Sleep(50);
            if (GetVCPFeatureAndVCPFeatureReply(handle, Code, IntPtr.Zero, out uint current, out _)) return (int)current;
        }
        return null;
    }

    /// <summary>
    /// Finds the physical monitor by the device path (the display key) and runs <paramref name="action"/> on it.
    /// shortcut: a Windows display with two monitors (clone mode) is skipped, add matching by index if anyone needs it
    /// </summary>
    static T WithMonitor<T>(string displayKey, Func<IntPtr, T> action, T notFound)
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) => { handles.Add(monitor); return true; }, IntPtr.Zero);

        foreach (var monitor in handles)
        {
            var info = new MonitorInfoEx { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info)) continue;

            var active = ActiveDevicePaths(info.DeviceName);
            if (active.Count != 1 || !string.Equals(active[0], displayKey, StringComparison.OrdinalIgnoreCase)) continue;

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count != 1) return notFound;
            var physical = new PhysicalMonitor[1];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, 1, physical)) return notFound;
            try { return action(physical[0].Handle); }
            finally { DestroyPhysicalMonitor(physical[0].Handle); }
        }
        return notFound;
    }

    static List<string> ActiveDevicePaths(string adapterDevice)
    {
        var list = new List<string>();
        for (uint i = 0; ; i++)
        {
            var device = new DisplayDevice { Size = System.Runtime.InteropServices.Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(adapterDevice, i, ref device, EDD_GET_DEVICE_INTERFACE_NAME)) return list;
            if ((device.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0) list.Add(device.DeviceId);
        }
    }
}
