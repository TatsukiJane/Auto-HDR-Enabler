using System.ComponentModel;
using System.Diagnostics;
using AutoHdr.Core;

namespace AutoHdr;

/// <summary>Modes without a window: the launch options wrapper and HDR control from the console.</summary>
public static class Cli
{
    static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// AutoHdr.exe --launch %command%
    /// Turns HDR on before the game starts, waits until all its processes exit and restores the previous mode.
    /// </summary>
    public static int Launch(string[] command)
    {
        var settings = AppSettings.Load();
        Loc.Use(settings.Language);
        Log.Prefix = Loc.T("[запуск] ", "[launch] ");
        if (command.Length == 0)
        {
            Log.Warn(Loc.T("Не передана команда запуска игры", "No game launch command given"));
            return 2;
        }

        var monitors = MonitorRegistry.Load();
        var exe = command[0];
        try { exe = Path.GetFullPath(exe); } catch { }
        var gameName = Path.GetFileNameWithoutExtension(exe);

        var displays = HdrControl.GetDisplays();
        monitors.Sync(displays);
        var enabled = new List<string>();
        if (settings.Paused) Log.Info(Loc.T($"Автопереключение на паузе: {gameName} запускается без HDR", $"Auto switching paused: {gameName} starts without HDR"));
        foreach (var display in monitors.Targets(displays).Where(d => !d.HdrOn && !settings.Paused))
            if (HdrControl.SetHdr(display, true)) enabled.Add(display.Key);
        if (enabled.Count > 0)
        {
            bool ok = HdrControl.WaitForState(enabled, true, ConfirmTimeout);
            Log.Info(ok ? Loc.T($"HDR включён перед запуском {gameName}", $"HDR turned on before starting {gameName}")
                : Loc.T($"HDR не подтвердился перед запуском {gameName}", $"HDR not confirmed before starting {gameName}"));
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
            Log.Error(Loc.T($"Не удалось запустить {exe}", $"Could not start {exe}"), ex);
            exitCode = 1;
        }

        // The launcher may have handed over to another process from the game folder: wait until all of them exit
        var root = GameRoot(exe);
        var grace = TimeSpan.FromSeconds(Math.Clamp(settings.ExitDelay ? settings.ExitDelaySec : 0, 3, 600));
        var alive = new HashSet<uint>();
        using var watcher = new ProcessWatcher();
        watcher.Started += (pid, path) =>
        {
            if (pid != Environment.ProcessId && PathUtil.IsUnder(path, root)) lock (alive) alive.Add(pid);
        };
        watcher.Exited += pid => { lock (alive) alive.Remove(pid); };
        watcher.Start();
        var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow - quietSince < grace)
        {
            lock (alive)
            {
                if (alive.Count > 0) quietSince = DateTime.UtcNow;
            }
            Thread.Sleep(250);
        }

        if (enabled.Count > 0 && settings.RestoreAfterExit)
        {
            var toDisable = HdrControl.GetDisplays().Where(d => enabled.Contains(d.Key) && d.HdrOn).ToList();
            foreach (var display in toDisable) HdrControl.SetHdr(display, false);
            if (toDisable.Count > 0)
            {
                HdrControl.WaitForState(toDisable.Select(d => d.Key).ToList(), false, ConfirmTimeout);
                Log.Info(Loc.T($"{gameName} закрыта, HDR выключен", $"{gameName} closed, HDR turned off"));
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
            // Not an exe (shortcut, bat, URL): hand it to the Windows shell
            info.UseShellExecute = true;
            return Process.Start(info);
        }
    }

    /// <summary>For Steam, the whole game folder in steamapps\common, otherwise the exe folder.</summary>
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
    /// Exit code: 0 — HDR on, 1 — off, 2 — HDR not supported, 3 — error.
    /// </summary>
    public static int Hdr(string[] args)
    {
        AttachToParentConsole();
        Loc.Use(AppSettings.Load().Language);
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
                Console.WriteLine(Loc.T("Нет мониторов с поддержкой HDR", "No displays support HDR"));
                return 2;
            }
            bool on = command switch { "on" => true, "off" => false, _ => !targets.Any(d => d.HdrOn) };
            var changed = targets.Where(d => d.HdrOn != on && HdrControl.SetHdr(d, on)).Select(d => d.Key).ToList();
            if (changed.Count > 0) HdrControl.WaitForState(changed, on, ConfirmTimeout);
            displays = HdrControl.GetDisplays();
        }
        else if (command != "status")
        {
            Console.WriteLine(Loc.T("Использование", "Usage") + ": AutoHdr.exe --hdr [status|on|off|toggle]");
            return 3;
        }

        foreach (var d in displays)
        {
            var state = !d.HdrSupported ? Loc.T("HDR не поддерживается", "HDR not supported")
                : d.HdrOn ? Loc.T("HDR включён", "HDR on") : Loc.T("HDR выключен", "HDR off");
            var auto = monitors.IsAuto(d) ? Loc.T(", авто-HDR", ", auto HDR") : "";
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
