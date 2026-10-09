namespace Trayify.Core;

public static class Paths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trayify");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string HiddenFile => Path.Combine(DataDir, "hidden.json");
    public static string LogFile => Path.Combine(DataDir, "trayify.log");
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Trayify.exe");
    public static string IconFile => Path.Combine(AppContext.BaseDirectory, "Assets", "Trayify.ico");
    public static string PipeName => $"Trayify.Control.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
}

public static class Log
{
    private static readonly object Gate = new();
    public static string Tag { get; set; } = "app";

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDir);
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    File.Copy(Paths.LogFile, Paths.LogFile + ".1", true);
                    File.WriteAllText(Paths.LogFile, string.Empty);
                }
                File.AppendAllText(Paths.LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Tag}:{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
