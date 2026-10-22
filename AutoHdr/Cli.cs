using System.ComponentModel;
using System.Diagnostics;
using AutoHdr.Core;

namespace AutoHdr;

/// <summary>Режимы без окна: обёртка для параметров запуска и управление HDR из консоли.</summary>
public static class Cli
{
    static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// AutoHdr.exe --launch %command%
    /// Включает HDR до старта игры, ждёт закрытия всех её процессов и возвращает прежний режим.
    /// </summary>
    public static int Launch(string[] command)
    {
        Log.Prefix = "[запуск] ";
        if (command.Length == 0)
        {
            Log.Warn("Не передана команда запуска игры");
            return 2;
        }

        var settings = AppSettings.Load();
        var monitors = MonitorRegistry.Load();
        var exe = command[0];
        try { exe = Path.GetFullPath(exe); } catch { }
        var gameName = Path.GetFileNameWithoutExtension(exe);

        var displays = HdrControl.GetDisplays();
        monitors.Sync(displays);
        var enabled = new List<string>();
        foreach (var display in monitors.Targets(displays).Where(d => !d.HdrOn))
            if (HdrControl.SetHdr(display, true)) enabled.Add(display.Key);
        if (enabled.Count > 0)
        {
            bool ok = HdrControl.WaitForState(enabled, true, ConfirmTimeout);
            Log.Info(ok ? $"HDR включён перед запуском {gameName}" : $"HDR не подтвердился перед запуском {gameName}");
        }

        int exitCode;
        try
        {
            using var process = StartGame(exe, command.Skip(1));
            if (process == null) return 1;
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось запустить {exe}", ex);
            exitCode = 1;
        }

        // Лаунчер мог передать управление другому процессу из папки игры: ждём, пока закроются все
        var root = GameRoot(exe);
        var grace = TimeSpan.FromSeconds(Math.Clamp(settings.GracePeriodSec, 3, 600));
        var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow - quietSince < grace)
        {
            if (ProcessUtil.AnyProcessUnder(root, (uint)Environment.ProcessId)) quietSince = DateTime.UtcNow;
            Thread.Sleep(1000);
        }

        if (enabled.Count > 0 && settings.RestoreAfterExit)
        {
            var toDisable = HdrControl.GetDisplays().Where(d => enabled.Contains(d.Key) && d.HdrOn).ToList();
            foreach (var display in toDisable) HdrControl.SetHdr(display, false);
            if (toDisable.Count > 0)
            {
                HdrControl.WaitForState(toDisable.Select(d => d.Key).ToList(), false, ConfirmTimeout);
                Log.Info($"{gameName} закрыта, HDR выключен");
            }
        }
        return exitCode;
    }

    static Process? StartGame(string exe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        try
        {
            return Process.Start(info);
        }
        catch (Win32Exception)
        {
            // Не exe (ярлык, bat, ссылка): отдаём оболочке Windows
            info.UseShellExecute = true;
            return Process.Start(info);
        }
    }

    /// <summary>Для Steam — вся папка игры в steamapps\common, иначе папка exe.</summary>
    static string GameRoot(string exe)
    {
        const string marker = @"\steamapps\common\";
        int i = exe.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            int end = exe.IndexOf('\\', i + marker.Length);
            if (end > 0) return exe[..end];
        }
        return Path.GetDirectoryName(exe) ?? exe;
    }

    /// <summary>
    /// AutoHdr.exe --hdr [status|on|off|toggle]
    /// Код выхода: 0 — HDR включён, 1 — выключен, 2 — HDR не поддерживается, 3 — ошибка.
    /// </summary>
    public static int Hdr(string[] args)
    {
        AttachToParentConsole();
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        var monitors = MonitorRegistry.Load();
        var displays = HdrControl.GetDisplays();
        monitors.Sync(displays);

        var targets = monitors.Targets(displays);
        if (targets.Count == 0) targets = displays.Where(d => d.HdrSupported).ToList();

        if (command is "on" or "off" or "toggle")
        {
            if (targets.Count == 0)
            {
                Console.WriteLine("Нет мониторов с поддержкой HDR");
                return 2;
            }
            bool on = command switch { "on" => true, "off" => false, _ => !targets.Any(d => d.HdrOn) };
            var changed = targets.Where(d => d.HdrOn != on && HdrControl.SetHdr(d, on)).Select(d => d.Key).ToList();
            if (changed.Count > 0) HdrControl.WaitForState(changed, on, ConfirmTimeout);
            displays = HdrControl.GetDisplays();
        }
        else if (command != "status")
        {
            Console.WriteLine("Использование: AutoHdr.exe --hdr [status|on|off|toggle]");
            return 3;
        }

        foreach (var d in displays)
        {
            var state = !d.HdrSupported ? "HDR не поддерживается" : d.HdrOn ? "HDR включён" : "HDR выключен";
            var auto = monitors.IsAuto(d) ? ", авто-HDR" : "";
            Console.WriteLine($"{d.Name} ({d.Connection}{auto}): {state}");
        }

        var relevant = displays.Where(d => targets.Any(t => t.Key == d.Key)).ToList();
        if (relevant.Count == 0) return 2;
        return relevant.Any(d => d.HdrOn) ? 0 : 1;
    }

    static void AttachToParentConsole()
    {
        if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS)) return;
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine();
    }
}
