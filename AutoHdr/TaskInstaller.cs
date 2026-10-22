using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using AutoHdr.Core;

namespace AutoHdr;

/// <summary>
/// Автозапуск через планировщик заданий с наивысшими правами:
/// приложение стартует при входе в Windows с правами администратора и без окна UAC.
/// </summary>
public static class TaskInstaller
{
    public const string TaskName = "AutoHDR Enabler";

    public static bool IsInstalled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>Установить или удалить задачу. Без прав администратора перезапускает себя через UAC.</summary>
    public static bool SetInstalled(bool install)
    {
        var arg = install ? "--install-task" : "--uninstall-task";
        if (Elevation.IsElevated) return (install ? Install() : Uninstall()) == 0;
        return RunElevated(arg) == 0;
    }

    /// <summary>Вызывается только с правами администратора.</summary>
    public static int Install()
    {
        // Задача с правами администратора запускает копию из Program Files: папку сборки или загрузки может изменить обычный пользователь
        var exe = AppPaths.InstalledExe;
        try
        {
            if (!string.Equals(AppPaths.ExePath, exe, StringComparison.OrdinalIgnoreCase))
            {
                var target = Path.GetDirectoryName(exe)!;
                Directory.CreateDirectory(target);
                File.Copy(AppPaths.ExePath, exe, overwrite: true);
                // Self-contained-сборка: родные библиотеки WPF лежат рядом с exe. Только они, а не всё подряд из папки загрузки:
                // посторонняя DLL в Program Files загрузилась бы в процесс с правами администратора
                foreach (var dll in Directory.GetFiles(Path.GetDirectoryName(AppPaths.ExePath)!, "*_cor3.dll"))
                    File.Copy(dll, Path.Combine(target, Path.GetFileName(dll)), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T($"Не удалось скопировать приложение в {Path.GetDirectoryName(exe)}", $"Could not copy the app to {Path.GetDirectoryName(exe)}"), ex);
            return -1;
        }

        // Не в %TEMP% пользователя: там файл до чтения schtasks может подменить обычный процесс
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", $"AutoHdrTask-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, BuildXml(exe), Encoding.Unicode);
            int code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            if (code == 0) Log.Info(Loc.T("Автозапуск включён: задача планировщика создана", "Autostart on: scheduled task created"));
            else Log.Warn(Loc.T($"Не удалось создать задачу планировщика, код {code}", $"Could not create the scheduled task, code {code}"));
            return code;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static int Uninstall()
    {
        int code = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        if (code == 0) Log.Info(Loc.T("Автозапуск выключен: задача планировщика удалена", "Autostart off: scheduled task removed"));
        return code;
    }

    /// <summary>Запускает копию приложения с правами администратора. -1, если пользователь отказался в UAC.</summary>
    public static int RunElevated(string args, bool wait = true)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(AppPaths.ExePath, args)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process == null) return -1;
            if (!wait) return 0;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception)
        {
            return -1; // отмена в окне UAC
        }
    }

    static string BuildXml(string exePath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = SecurityElement.Escape(identity.Name);
        var exe = SecurityElement.Escape(exePath);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{Loc.T("Auto HDR: включает HDR при запуске игр и возвращает SDR после выхода", "Auto HDR: turns HDR on when games start and back to SDR after they exit")}</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>6</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>--tray</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    static int RunSchtasks(string args)
    {
        try
        {
            // Полный путь: по короткому имени Windows сначала ищет программу в папке приложения
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process == null) return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("Не удалось запустить schtasks", "Could not run schtasks"), ex);
            return -1;
        }
    }
}
