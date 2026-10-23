using System.Security.Principal;
using static AutoHdr.Core.Native;

namespace AutoHdr.Core;

public static class ProcessUtil
{
    static readonly SecurityIdentifier? CurrentUser = WindowsIdentity.GetCurrent().User;

    public static uint[] EnumPids()
    {
        var buffer = new uint[2048];
        while (true)
        {
            if (!K32EnumProcesses(buffer, (uint)(buffer.Length * sizeof(uint)), out var bytes)) return [];
            int count = (int)(bytes / sizeof(uint));
            if (count < buffer.Length) return buffer.AsSpan(0, count).ToArray();
            buffer = new uint[buffer.Length * 2];
        }
    }

    public static string? GetPath(uint pid)
    {
        if (pid == 0) return null;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Suspends only processes of the same user: a regular user edits the game list,
    /// and otherwise the elevated copy could suspend a service or another user's process.
    /// </summary>
    public static bool Suspend(uint pid) => IsOwnProcess(pid) && WithSuspendHandle(pid, h => NtSuspendProcess(h) == 0);

    public static bool Resume(uint pid) => WithSuspendHandle(pid, h => NtResumeProcess(h) == 0);

    static bool IsOwnProcess(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return false;
            try
            {
                using var identity = new WindowsIdentity(token);
                return CurrentUser != null && identity.User == CurrentUser;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    static bool WithSuspendHandle(uint pid, Func<IntPtr, bool> action)
    {
        var handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (handle == IntPtr.Zero) return false;
        try { return action(handle); }
        finally { CloseHandle(handle); }
    }
}

/// <summary>
/// Reports process starts and exits by polling the process list every 0.25 s.
/// WMI events (Win32_ProcessStartTrace) were removed: they arrived after 0.2–0.5 s and cost about 1.5 ms
/// for every start of any process in the system, while polling notices a start in about 0.08 ms.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    const int PollMs = 250;
    readonly HashSet<uint> known = [];
    readonly ManualResetEvent stop = new(false);

    public event Action<uint, string>? Started;
    public event Action<uint>? Exited;

    public void Start()
    {
        var initial = ProcessUtil.EnumPids();
        known.UnionWith(initial);

        // Games that were already running before the app started
        foreach (var pid in initial)
        {
            var path = ProcessUtil.GetPath(pid);
            if (path != null) Raise(() => Started?.Invoke(pid, path));
        }

        // A dedicated thread, not a Timer: a Timer wakes the timer thread and the pool on every tick, which costs twice as much
        new Thread(() => { while (!stop.WaitOne(PollMs)) Poll(); }) { IsBackground = true, Name = "ProcessWatcher" }.Start();
    }

    void Poll()
    {
        try
        {
            var current = ProcessUtil.EnumPids();
            var currentSet = new HashSet<uint>(current);
            var started = new List<uint>();
            var exited = new List<uint>();
            foreach (var pid in current)
                if (known.Add(pid)) started.Add(pid);
            foreach (var pid in known)
                if (!currentSet.Contains(pid)) exited.Add(pid);
            foreach (var pid in exited) known.Remove(pid);

            foreach (var pid in started)
            {
                var path = ProcessUtil.GetPath(pid);
                if (path != null) Raise(() => Started?.Invoke(pid, path));
            }
            foreach (var pid in exited) Raise(() => Exited?.Invoke(pid));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("Ошибка опроса процессов", "Process polling failed"), ex);
        }
    }

    static void Raise(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error(Loc.T("Ошибка обработчика процесса", "Process handler failed"), ex); }
    }

    public void Dispose() => stop.Set();
}
