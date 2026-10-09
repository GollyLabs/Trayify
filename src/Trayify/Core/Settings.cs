using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trayify.Core;

public sealed class AppRule
{
    /// <summary>Executable file name, lower-case (e.g. "grok bot.exe"). Rules are keyed by this.</summary>
    public string Exe { get; set; } = "";
    public string? Path { get; set; }
    public string? DisplayName { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
}

public sealed class AppSettings
{
    public List<AppRule> Rules { get; set; } = new();
    /// <summary>Right-click on any window's minimize button sends it to the tray.</summary>
    public bool RightClickMinimize { get; set; } = true;
    /// <summary>Alt+F4 on a close-to-tray app sends it to the tray.</summary>
    public bool AltF4ToTray { get; set; } = true;
    /// <summary>Use UI Automation to find caption buttons drawn by the app itself (Discord etc.).</summary>
    public bool UiaFallback { get; set; } = true;
    /// <summary>Task Scheduler path used for "Start with Windows".</summary>
    public string StartupTaskPath { get; set; } = @"\Trayify\Trayify at sign-in";
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Options);
                if (s != null)
                {
                    s.Rules ??= new();
                    foreach (var r in s.Rules) r.Exe = r.Exe.ToLowerInvariant();
                    return s;
                }
            }
        }
        catch (Exception ex) { Log.Error("Failed to load settings", ex); }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception ex) { Log.Error("Failed to save settings", ex); }
    }
}
