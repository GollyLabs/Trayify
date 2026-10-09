using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace Trayify.Core;

/// <summary>
/// "Start with Windows" = a Task Scheduler logon task with RunLevel=Highest. A Run-key entry would
/// trigger a UAC prompt at every sign-in (the exe requires elevation); a highest-privilege task does not.
/// Trayify.exe is a GUI app, so no console host is involved.
/// </summary>
public static class StartupTask
{
    public static bool IsEnabled(string taskPath) => RunSchtasks($"/Query /TN \"{taskPath}\"", out _) == 0;

    public static bool Enable(string taskPath, out string error)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>Trayify</Author>
    <Description>Starts Trayify (elevated, in the tray) when {SecurityElement.Escape(user)} signs in.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{SecurityElement.Escape(user)}</UserId>
      <Delay>PT5S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{SecurityElement.Escape(user)}</UserId>
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
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{SecurityElement.Escape(Paths.ExePath)}</Command>
      <Arguments>--startup</Arguments>
      <WorkingDirectory>{SecurityElement.Escape(AppContext.BaseDirectory)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
        var tmp = Path.Combine(Path.GetTempPath(), $"trayify-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int rc = RunSchtasks($"/Create /TN \"{taskPath}\" /XML \"{tmp}\" /F", out error);
            Log.Info($"Startup task create '{taskPath}' rc={rc} {error}");
            return rc == 0;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static bool Disable(string taskPath, out string error)
    {
        int rc = RunSchtasks($"/Delete /TN \"{taskPath}\" /F", out error);
        Log.Info($"Startup task delete '{taskPath}' rc={rc} {error}");
        return rc == 0 || !IsEnabled(taskPath);
    }

    private static int RunSchtasks(string args, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true, // no console window (and no Windows Terminal) flashes
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit(15000);
        output = (stdout.Result + stderr.Result).Trim();
        return p.HasExited ? p.ExitCode : -1;
    }
}
