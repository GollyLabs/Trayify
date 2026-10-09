using System.Diagnostics;

namespace Trayify.Core;

/// <summary>
/// Watchdog process (`Trayify.exe --guardian &lt;pid&gt; &lt;startTime&gt;`). It waits for the main
/// Trayify process to exit; if that happened without a clean shutdown (crash, Task Manager kill),
/// hidden.json still lists hidden windows and the guardian shows them again. Normal exits clear the
/// file first, so the guardian then has nothing to do. If both processes are killed, the next
/// Trayify start does the same recovery.
/// </summary>
public static class Guardian
{
    public static void Spawn()
    {
        try
        {
            long start = ProcessInfo.QueryStartTime((uint)Environment.ProcessId);
            var psi = new ProcessStartInfo(Paths.ExePath, $"--guardian {Environment.ProcessId} {start}") { UseShellExecute = false };
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Error("Failed to start guardian", ex); }
    }

    public static int Run(string[] args)
    {
        Log.Tag = "guardian";
        if (args.Length < 3 || !int.TryParse(args[1], out int pid) || !long.TryParse(args[2], out long start)) return 2;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (ProcessInfo.QueryStartTime((uint)pid) != start) return 0; // pid was reused; nothing to guard
            p.WaitForExit();
        }
        catch (ArgumentException) { /* already gone */ }
        Thread.Sleep(300);
        int n = Recovery.RestoreFromFile("guardian");
        if (n > 0) Log.Warn($"Trayify (pid {pid}) exited without restoring; guardian restored {n} window(s)");
        return 0;
    }
}
