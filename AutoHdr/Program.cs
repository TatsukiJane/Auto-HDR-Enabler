using AutoHdr.Core;

namespace AutoHdr;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // --data <folder>: a separate settings folder (for tests). Must come first, before AppPaths.DataDir is accessed.
        if (args.Length >= 2 && args[0].Equals("--data", StringComparison.OrdinalIgnoreCase))
        {
            AppPaths.DataDirOverride = args[1];
            args = args[2..];
        }

        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--launch":
                    return Cli.Launch(args[1..]);
                case "--hdr":
                    return Cli.Hdr(args[1..]);
                case "--install-task":
                    return Elevation.IsElevated ? TaskInstaller.Install() : TaskInstaller.RunElevated("--install-task");
                case "--uninstall-task":
                    return Elevation.IsElevated ? TaskInstaller.Uninstall() : TaskInstaller.RunElevated("--uninstall-task");
                case "--exit":
                    return InstanceSignals.Signal(InstanceSignals.ExitEventName) ? 0 : 1;
            }
        }

        StartupTrace.Mark($"main: {string.Join(' ', args)}; elevated={Elevation.IsElevated}; data={AppPaths.DataDir}");
        try
        {
            var app = new App(args);
            app.InitializeComponent();
            StartupTrace.Mark("app created");
            int code = app.Run();
            StartupTrace.Mark($"app exited {code}");
            return code;
        }
        catch (Exception ex)
        {
            StartupTrace.Mark("main crashed: " + ex);
            throw;
        }
    }
}
