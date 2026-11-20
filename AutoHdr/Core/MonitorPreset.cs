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
    static readonly TimeSpan ConfirmSettle = TimeSpan.FromSeconds(1);
    /// <summary>A mode that changes the resolution or refresh rate resyncs the signal: the monitor is silent for several seconds.</summary>
    static readonly TimeSpan ResyncTimeout = TimeSpan.FromSeconds(15);
    /// <summary>How soon a resync starts after the mode is accepted.</summary>
    static readonly TimeSpan ResyncStart = TimeSpan.FromSeconds(1);

    /// <summary>Codes the monitor only reports (horizontal and vertical frequency): they follow a mode, writing them does nothing.</summary>
    static readonly HashSet<byte> ReportOnly = [0xAC, 0xAE];

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

    /// <summary>
    /// Sets the value and confirms it by reading it back, then waits out a signal resync if the mode starts one.
    /// Re-sent while the monitor answers with the old value: right after an HDR switch it drops writes.
    /// </summary>
    public static bool Write(string displayKey, byte code, int value, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var target = new Dictionary<byte, int> { [code] = value };
        lock (Gate)
        {
            while (true)
            {
                if (WithMonitor(displayKey, h => SetVCPFeature(h, code, (uint)value), false) && Settled(displayKey, [code], target, ConfirmSettle)) return true;
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
        var differing = game.Keys.Where(c => everyday.TryGetValue(c, out int a) && game[c] != a).Order().ToList();
        Log.Info(Loc.T("Поиск режима монитора: изменились коды ", "Monitor mode search: changed codes ") + (differing.Count == 0 ? "—" :
            string.Join(", ", differing.Select(c => $"{c:X2}: {everyday[c]} → {game[c]} → {(everydayAgain.TryGetValue(c, out int v) ? v.ToString() : "?")}"))));

        var changed = differing
            .Where(c => !ReportOnly.Contains(c) && everydayAgain.TryGetValue(c, out int a2) && a2 == everyday[c])
            .ToList();
        if (changed.Count == 0) return new(LearnOutcome.NoChange);

        // Manufacturer codes first: the mode usually lives there, then the standard display mode and color preset
        var candidates = changed.OrderBy(c => c >= 0xE0 ? 0 : c == 0xDC ? 1 : c == 0x14 ? 2 : 3).ThenBy(c => c).ToList();
        lock (Gate)
        {
            foreach (var code in candidates)
            {
                bool isMode = WithMonitor(displayKey, h => SetVCPFeature(h, code, (uint)game[code]), false) && Settled(displayKey, changed, game, SettleTimeout);
                bool restored = Write(displayKey, code, everyday[code], ResyncTimeout) && Settled(displayKey, changed, everyday, SettleTimeout);
                Log.Info(Loc.T($"Поиск режима монитора: код {code:X2} — {(isMode ? "переключает режим" : "не переключает режим")}, обычный режим {(restored ? "вернулся" : "не вернулся")}",
                    $"Monitor mode search: code {code:X2} — {(isMode ? "switches the mode" : "does not switch the mode")}, everyday mode {(restored ? "restored" : "not restored")}"));
                if (!restored) return new LearnResult(LearnOutcome.NotRestored);
                if (isMode) return new LearnResult(LearnOutcome.Found, code, game[code]);
            }
            return new LearnResult(LearnOutcome.NotFound);
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

    /// <summary>
    /// Waits until every code has its target value, then checks again after <see cref="ResyncStart"/>:
    /// a mode that changes the resolution blanks the screen only after the monitor has reported the new value.
    /// </summary>
    static bool Settled(string displayKey, IReadOnlyList<byte> codes, Dictionary<byte, int> target, TimeSpan settle)
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
    static bool WaitFor(string displayKey, IReadOnlyList<byte> codes, Dictionary<byte, int> target, TimeSpan settle)
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
