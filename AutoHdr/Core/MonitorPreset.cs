using static AutoHdr.Core.Native;

namespace AutoHdr.Core;

public enum LearnOutcome { Found, NoChange, NotFound, NotRestored }

/// <summary>Result of the game mode search: the VCP code that switches the whole picture mode and its value for the game mode.</summary>
public sealed record LearnResult(LearnOutcome Outcome, byte Code = 0, int Value = 0);

/// <summary>
/// Monitor picture mode over DDC/CI (dxva2.dll). No administrator rights needed.
/// Which VCP code holds the mode differs between models, so it is learned: the user switches modes with the monitor buttons,
/// the app compares the settings and checks each changed code by writing it.
/// The monitor sometimes does not answer (about 6% of requests on the P275MS+), so every read is retried.
/// </summary>
public static class MonitorPreset
{
    static readonly object Gate = new();
    static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The current value of the code, or null if the monitor did not answer or was not found.
    /// <paramref name="timeout"/> — how long to keep asking: right after an HDR switch the monitor is silent for a few seconds.
    /// </summary>
    public static int? Read(string displayKey, byte code, TimeSpan timeout = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (Gate)
        {
            while (true)
            {
                var value = WithMonitor(displayKey, h => ReadLocked(h, code), null);
                if (value != null || DateTime.UtcNow >= deadline) return value;
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>Sets the value and confirms it by reading it back. Right after an HDR switch the monitor is silent for a few seconds.</summary>
    public static bool Write(string displayKey, byte code, int value, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (Gate)
        {
            while (true)
            {
                if (WithMonitor(displayKey, h => SetVCPFeature(h, code, (uint)value) && Confirm(h, code, value), false)) return true;
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>
    /// Stable values of the given codes (all 256 if null): a code is kept only if two reads give the same value.
    /// Empty if the monitor was not found or did not answer. All codes take about 30 s, the ones a monitor answers about 10 s.
    /// </summary>
    public static Dictionary<byte, int> Snapshot(string displayKey, IReadOnlyCollection<byte>? codes = null)
    {
        lock (Gate) return WithMonitor(displayKey, h => SnapshotLocked(h, codes ?? Enumerable.Range(0, 256).Select(c => (byte)c).ToList()), []);
    }

    /// <summary>
    /// Finds the code that switches the whole picture mode. Snapshots: the everyday mode, the game mode, the everyday mode again.
    /// Codes that differ between the two everyday snapshots drift by themselves (usage hours and the like) and are ignored.
    /// Each code that changed is written with its game value: if all changed codes follow, it is the mode code.
    /// Then the everyday value is written back, so the monitor ends in the everyday mode.
    /// Only values the monitor itself reported for that code are written.
    /// </summary>
    public static LearnResult FindModeCode(string displayKey, Dictionary<byte, int> everyday, Dictionary<byte, int> game, Dictionary<byte, int> everydayAgain)
    {
        var changed = game.Keys
            .Where(c => everyday.TryGetValue(c, out int a) && everydayAgain.TryGetValue(c, out int a2) && a == a2 && game[c] != a)
            .ToList();
        if (changed.Count == 0) return new(LearnOutcome.NoChange);

        // Manufacturer codes first: the mode usually lives there, then the standard display mode and color preset
        var candidates = changed.OrderBy(c => c >= 0xE0 ? 0 : c == 0xDC ? 1 : c == 0x14 ? 2 : 3).ThenBy(c => c).ToList();
        lock (Gate)
        {
            return WithMonitor(displayKey, h =>
            {
                foreach (var code in candidates)
                {
                    bool isMode = SetVCPFeature(h, code, (uint)game[code]) && WaitFor(h, changed, game);
                    bool restored = SetVCPFeature(h, code, (uint)everyday[code]) && WaitFor(h, changed, everyday);
                    Log.Info(Loc.T($"Поиск режима монитора: код {code:X2} — {(isMode ? "переключает режим" : "не переключает режим")}, обычный режим {(restored ? "вернулся" : "не вернулся")}",
                        $"Monitor mode search: code {code:X2} — {(isMode ? "switches the mode" : "does not switch the mode")}, everyday mode {(restored ? "restored" : "not restored")}"));
                    if (!restored) return new LearnResult(LearnOutcome.NotRestored);
                    if (isMode) return new LearnResult(LearnOutcome.Found, code, game[code]);
                }
                return new LearnResult(LearnOutcome.NotFound);
            }, new LearnResult(LearnOutcome.NotFound));
        }
    }

    static Dictionary<byte, int> SnapshotLocked(IntPtr handle, IReadOnlyCollection<byte> codes)
    {
        // A single read misses about a third of the codes on a full scan, so the silent ones get a second pass
        var answered = new Dictionary<byte, int>();
        var pending = codes.ToList();
        for (int pass = 0; pass < 2 && pending.Count > 0; pass++)
        {
            foreach (var code in pending)
                if (ReadOnce(handle, code) is int value) answered[code] = value;
            pending = pending.Where(c => !answered.ContainsKey(c)).ToList();
        }

        var stable = new Dictionary<byte, int>();
        foreach (var (code, value) in answered)
            if ((ReadOnce(handle, code) ?? ReadOnce(handle, code)) == value) stable[code] = value;
        return stable;
    }

    /// <summary>Waits until every code has its target value: the monitor applies a mode within a second or two.</summary>
    static bool WaitFor(IntPtr handle, IReadOnlyList<byte> codes, Dictionary<byte, int> target)
    {
        var deadline = DateTime.UtcNow + SettleTimeout;
        while (true)
        {
            Thread.Sleep(200);
            if (codes.All(c => ReadLocked(handle, c) == target[c])) return true;
            if (DateTime.UtcNow >= deadline) return false;
        }
    }

    static bool Confirm(IntPtr handle, byte code, int value)
    {
        // The monitor applies the mode within a few hundred milliseconds: until then it reports the old one
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(200);
            if (ReadLocked(handle, code) == value) return true;
        }
        return false;
    }

    static int? ReadLocked(IntPtr handle, byte code)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0) Thread.Sleep(50);
            if (ReadOnce(handle, code) is int value) return value;
        }
        return null;
    }

    static int? ReadOnce(IntPtr handle, byte code) =>
        GetVCPFeatureAndVCPFeatureReply(handle, code, IntPtr.Zero, out uint current, out _) ? (int)current : null;

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
