using static AutoHdr.Core.Native;

namespace AutoHdr.Core;

public enum LearnOutcome { Found, NoChange, NotFound, NotRestored }

/// <summary>Result of the game mode search: the VCP code that switches the whole picture mode and its value for the game mode.</summary>
public sealed record LearnResult(LearnOutcome Outcome, int Code = 0, int Value = 0);

/// <summary>
/// Monitor picture mode over DDC/CI (dxva2.dll). No administrator rights needed.
/// Which VCP code holds the mode differs between models, so it is learned: the user switches modes with the monitor buttons,
/// the app compares the settings and checks each changed code by writing it.
/// The monitor sometimes does not answer (about 6% of requests on the P275MS+), so every read is retried.
/// Codes from <see cref="Vendor"/> up are registers behind the manufacturer prefix of VIEW MORE WIDGET monitors (P275MS+, P275MV MAX):
/// a read is GET 99 (answers FAFA) then GET register, a write is SET 99 = value then SET register = value.
/// The P275MV MAX reports its picture mode only there, in register 22.
/// </summary>
public static class MonitorPreset
{
    static readonly object Gate = new();
    static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(4);
    static readonly TimeSpan ConfirmSettle = TimeSpan.FromSeconds(1);
    /// <summary>A mode that changes the resolution or refresh rate resyncs the signal: the monitor is silent for several seconds.</summary>
    static readonly TimeSpan ResyncTimeout = TimeSpan.FromSeconds(15);
    /// <summary>How soon a resync starts after the mode is accepted.</summary>
    static readonly TimeSpan ResyncStart = TimeSpan.FromSeconds(1);

    /// <summary>Codes the monitor only reports (horizontal and vertical frequency): they follow a mode, writing them does nothing.</summary>
    static readonly HashSet<int> ReportOnly = [0xAC, 0xAE];

    /// <summary>Offset of the registers behind the manufacturer prefix: 0x122 is register 22 behind 99.</summary>
    public const int Vendor = 0x100;
    const byte VendorPrefix = 0x99;
    const uint VendorAnswer = 0xFAFA;
    /// <summary>Picture mode register of VIEW MORE WIDGET monitors. The prefix gives access to more registers, but only the mode is needed.</summary>
    static readonly int[] VendorCodes = [Vendor | 0x22];

    /// <summary>The code as shown in the log and on the Displays tab: E0, or 99:22 for a register behind the prefix.</summary>
    public static string Name(int code) => code >= Vendor ? $"{VendorPrefix:X2}:{code & 0xFF:X2}" : $"{code:X2}";

    /// <summary>
    /// The current value of the code, or null if the monitor did not answer or was not found.
    /// <paramref name="timeout"/> — how long to keep asking: right after an HDR switch the monitor is silent for a few seconds.
    /// </summary>
    public static int? Read(string displayKey, int code, TimeSpan timeout = default)
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

    /// <summary>
    /// Sets the value and confirms it by reading it back, then waits out a signal resync if the mode starts one.
    /// Re-sent while the monitor answers with the old value: right after an HDR switch it drops writes.
    /// </summary>
    public static bool Write(string displayKey, int code, int value, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var target = new Dictionary<int, int> { [code] = value };
        lock (Gate)
        {
            while (true)
            {
                if (WithMonitor(displayKey, h => SetOnce(h, code, value), false) && Settled(displayKey, [code], target, ConfirmSettle)) return true;
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>
    /// Stable values of the given codes (all 256 if null): a code is kept only if two reads give the same value.
    /// Null means all 256 codes, plus the registers behind the manufacturer prefix if the monitor answers it.
    /// Empty if the monitor was not found or did not answer. All codes take about 30 s, the ones a monitor answers about 10 s.
    /// </summary>
    public static Dictionary<int, int> Snapshot(string displayKey, IReadOnlyCollection<int>? codes = null)
    {
        // The prefix itself is left out: reading it would turn the next plain code into a register behind the prefix
        lock (Gate) return WithMonitor(displayKey, h => SnapshotLocked(h, codes ??
            [.. Enumerable.Range(0, 256).Where(c => c != VendorPrefix), .. ReadLocked(h, VendorCodes[0]) != null ? VendorCodes : []]), []);
    }

    /// <summary>
    /// Finds the code that switches the whole picture mode. Snapshots: the everyday mode, the game mode, the everyday mode again.
    /// Codes that differ between the two everyday snapshots drift by themselves (usage hours and the like) and are ignored.
    /// Each code that changed is written with its game value: if all changed codes follow, it is the mode code.
    /// Then the everyday value is written back, so the monitor ends in the everyday mode.
    /// Only values the monitor itself reported for that code are written.
    /// </summary>
    public static LearnResult FindModeCode(string displayKey, Dictionary<int, int> everyday, Dictionary<int, int> game, Dictionary<int, int> everydayAgain)
    {
        var differing = game.Keys.Where(c => everyday.TryGetValue(c, out int a) && game[c] != a).Order().ToList();
        Log.Info(Loc.T("Поиск режима монитора: изменились коды ", "Monitor mode search: changed codes ") + (differing.Count == 0 ? "—" :
            string.Join(", ", differing.Select(c => $"{Name(c)}: {everyday[c]} → {game[c]} → {(everydayAgain.TryGetValue(c, out int v) ? v.ToString() : "?")}"))));

        var changed = differing
            .Where(c => !ReportOnly.Contains(c) && everydayAgain.TryGetValue(c, out int a2) && a2 == everyday[c])
            .ToList();
        if (changed.Count == 0) return new(LearnOutcome.NoChange);

        // Manufacturer codes first: the mode usually lives there, then the register behind the prefix, the standard display mode and color preset
        var candidates = changed.OrderBy(c => c is >= 0xE0 and < Vendor ? 0 : c >= Vendor ? 1 : c == 0xDC ? 2 : c == 0x14 ? 3 : 4).ThenBy(c => c).ToList();
        lock (Gate)
        {
            foreach (var code in candidates)
            {
                bool isMode = WithMonitor(displayKey, h => SetOnce(h, code, game[code]), false) && Settled(displayKey, changed, game, SettleTimeout);
                bool restored = Write(displayKey, code, everyday[code], ResyncTimeout) && Settled(displayKey, changed, everyday, SettleTimeout);
                Log.Info(Loc.T($"Поиск режима монитора: код {Name(code)} — {(isMode ? "переключает режим" : "не переключает режим")}, обычный режим {(restored ? "вернулся" : "не вернулся")}",
                    $"Monitor mode search: code {Name(code)} — {(isMode ? "switches the mode" : "does not switch the mode")}, everyday mode {(restored ? "restored" : "not restored")}"));
                if (!restored) return new LearnResult(LearnOutcome.NotRestored);
                if (isMode) return new LearnResult(LearnOutcome.Found, code, game[code]);
            }
            return new LearnResult(LearnOutcome.NotFound);
        }
    }

    static Dictionary<int, int> SnapshotLocked(IntPtr handle, IReadOnlyCollection<int> codes)
    {
        // A single read misses about a third of the codes on a full scan, so the silent ones get a second pass
        var answered = new Dictionary<int, int>();
        var pending = codes.ToList();
        for (int pass = 0; pass < 2 && pending.Count > 0; pass++)
        {
            foreach (var code in pending)
                if (ReadOnce(handle, code) is int value) answered[code] = value;
            pending = pending.Where(c => !answered.ContainsKey(c)).ToList();
        }

        var stable = new Dictionary<int, int>();
        foreach (var (code, value) in answered)
            if ((ReadOnce(handle, code) ?? ReadOnce(handle, code)) == value) stable[code] = value;
        return stable;
    }

    /// <summary>
    /// Waits until every code has its target value, then checks again after <see cref="ResyncStart"/>:
    /// a mode that changes the resolution blanks the screen only after the monitor has reported the new value.
    /// </summary>
    static bool Settled(string displayKey, IReadOnlyList<int> codes, Dictionary<int, int> target, TimeSpan settle)
    {
        if (!WaitFor(displayKey, codes, target, settle)) return false;
        Thread.Sleep(ResyncStart);
        return WaitFor(displayKey, codes, target, settle);
    }

    /// <summary>
    /// Waits until every code has its target value. <paramref name="settle"/> counts from the last moment the monitor was silent,
    /// so a signal resync (several seconds without answers) does not fail the wait, up to <see cref="ResyncTimeout"/>.
    /// The monitor is found anew on every read: after a resync Windows may give it a new handle.
    /// </summary>
    static bool WaitFor(string displayKey, IReadOnlyList<int> codes, Dictionary<int, int> target, TimeSpan settle)
    {
        var start = DateTime.UtcNow;
        var answeredSince = start;
        while (true)
        {
            var values = codes.Select(c => WithMonitor(displayKey, h => ReadLocked(h, c), null)).ToList();
            if (codes.Select((c, i) => values[i] == target[c]).All(match => match)) return true;
            var now = DateTime.UtcNow;
            if (values.Contains(null)) answeredSince = now;
            if (now - answeredSince >= settle || now - start >= ResyncTimeout) return false;
            Thread.Sleep(200);
        }
    }

    static int? ReadLocked(IntPtr handle, int code)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0) Thread.Sleep(50);
            if (ReadOnce(handle, code) is int value) return value;
        }
        return null;
    }

    /// <summary>
    /// One read. After GET 99 the monitor treats the next request as a register behind the prefix and ignores another GET 99,
    /// even when the answer to the prefix was lost. So the register is always read after the prefix, as VIEW MORE WIDGET does,
    /// and the value counts only if the prefix answered: otherwise it may be the plain code.
    /// </summary>
    static int? ReadOnce(IntPtr handle, int code)
    {
        bool prefixed = code < Vendor
            || GetVCPFeatureAndVCPFeatureReply(handle, VendorPrefix, IntPtr.Zero, out uint answer, out _) && answer == VendorAnswer;
        bool read = GetVCPFeatureAndVCPFeatureReply(handle, (byte)code, IntPtr.Zero, out uint current, out _);
        return prefixed && read ? (int)current : null;
    }

    static bool SetOnce(IntPtr handle, int code, int value) =>
        (code < Vendor || SetVCPFeature(handle, VendorPrefix, (uint)value)) && SetVCPFeature(handle, (byte)code, (uint)value);

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
