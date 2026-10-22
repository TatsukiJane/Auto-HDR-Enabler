using System.Management;
using static AutoHdr.Core.Native;

namespace AutoHdr.Core;

public static class ProcessUtil
{
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

    public static bool Suspend(uint pid) => WithSuspendHandle(pid, h => NtSuspendProcess(h) == 0);
    public static bool Resume(uint pid) => WithSuspendHandle(pid, h => NtResumeProcess(h) == 0);

    static bool WithSuspendHandle(uint pid, Func<IntPtr, bool> action)
    {
        var handle = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (handle == IntPtr.Zero) return false;
        try { return action(handle); }
        finally { CloseHandle(handle); }
    }

    /// <summary>Есть ли запущенный процесс, exe которого лежит внутри папки.</summary>
    public static bool AnyProcessUnder(string dir, uint excludePid = 0)
    {
        foreach (var pid in EnumPids())
        {
            if (pid == excludePid) continue;
            var path = GetPath(pid);
            if (path != null && PathUtil.IsUnder(path, dir)) return true;
        }
        return false;
    }
}

/// <summary>
/// Сообщает о запуске и завершении процессов.
/// С правами администратора старт ловится событиями ядра (Win32_ProcessStartTrace) почти мгновенно,
/// без них — опросом списка процессов раз в 0,5 с. Завершение всегда определяется опросом.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    readonly object gate = new();
    readonly HashSet<uint> known = [];
    Timer? timer;
    ManagementEventWatcher? startTrace;
    int polling;

    public event Action<uint, string>? Started;
    public event Action<uint>? Exited;

    public bool KernelEvents { get; private set; }

    public void Start()
    {
        var initial = ProcessUtil.EnumPids();
        lock (gate) known.UnionWith(initial);

        if (Elevation.IsElevated)
        {
            try
            {
                // EventQuery, а не WqlEventQuery: парсер WqlEventQuery отвергает запрос со списком полей
                startTrace = new ManagementEventWatcher(new EventQuery("SELECT ProcessID FROM Win32_ProcessStartTrace"));
                startTrace.EventArrived += OnStartTrace;
                startTrace.Start();
                KernelEvents = true;
            }
            catch (Exception ex)
            {
                Log.Error("События запуска процессов недоступны, работает опрос", ex);
                startTrace?.Dispose();
                startTrace = null;
            }
        }

        // Игры, которые уже были запущены до старта приложения
        foreach (var pid in initial)
        {
            var path = ProcessUtil.GetPath(pid);
            if (path != null) Raise(() => Started?.Invoke(pid, path));
        }

        timer = new Timer(_ => Poll(), null, 500, 500);
    }

    void OnStartTrace(object sender, EventArrivedEventArgs e)
    {
        try
        {
            uint pid = Convert.ToUInt32(e.NewEvent["ProcessID"]);
            // PID уже в known, если опрос успел раньше или PID освободился меньше 0,5 с назад. Повтор движку не вредит
            lock (gate) known.Add(pid);
            var path = ProcessUtil.GetPath(pid);
            if (path != null) Raise(() => Started?.Invoke(pid, path));
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка обработки события запуска процесса", ex);
        }
    }

    void Poll()
    {
        if (Interlocked.Exchange(ref polling, 1) == 1) return;
        try
        {
            var current = ProcessUtil.EnumPids();
            var currentSet = new HashSet<uint>(current);
            var started = new List<uint>();
            var exited = new List<uint>();
            lock (gate)
            {
                foreach (var pid in current)
                    if (known.Add(pid)) started.Add(pid);
                foreach (var pid in known)
                    if (!currentSet.Contains(pid)) exited.Add(pid);
                foreach (var pid in exited) known.Remove(pid);
            }

            foreach (var pid in started)
            {
                var path = ProcessUtil.GetPath(pid);
                if (path != null) Raise(() => Started?.Invoke(pid, path));
            }
            foreach (var pid in exited) Raise(() => Exited?.Invoke(pid));
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка опроса процессов", ex);
        }
        finally
        {
            Volatile.Write(ref polling, 0);
        }
    }

    static void Raise(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error("Ошибка обработчика процесса", ex); }
    }

    public void Dispose()
    {
        timer?.Dispose();
        timer = null;
        if (startTrace != null)
        {
            try { startTrace.Stop(); } catch { }
            startTrace.Dispose();
            startTrace = null;
        }
    }
}
