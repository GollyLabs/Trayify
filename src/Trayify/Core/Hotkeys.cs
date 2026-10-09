using System.Runtime.InteropServices;
using System.Text;
using Trayify.Native;

namespace Trayify.Core;

/// <summary>A key combination: MOD_* flags + virtual-key code.</summary>
public readonly record struct Hotkey(uint Modifiers, uint Key)
{
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public bool IsEmpty => Key == 0;

    public override string ToString()
    {
        if (IsEmpty) return "";
        var sb = new StringBuilder();
        if ((Modifiers & MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((Modifiers & MOD_ALT) != 0) sb.Append("Alt+");
        if ((Modifiers & MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((Modifiers & MOD_WIN) != 0) sb.Append("Win+");
        sb.Append(KeyName(Key));
        return sb.ToString();
    }

    public static string KeyName(uint vk)
    {
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";
        if (vk is >= 0x60 and <= 0x69) return $"Num{vk - 0x60}";
        switch (vk)
        {
            case 0x20: return "Space"; case 0x0D: return "Enter"; case 0x09: return "Tab"; case 0x1B: return "Esc";
            case 0x08: return "Backspace"; case 0x2E: return "Delete"; case 0x2D: return "Insert";
            case 0x24: return "Home"; case 0x23: return "End"; case 0x21: return "PageUp"; case 0x22: return "PageDown";
            case 0x25: return "Left"; case 0x26: return "Up"; case 0x27: return "Right"; case 0x28: return "Down";
            case 0x13: return "Pause"; case 0x2C: return "PrintScreen";
        }
        // OEM keys (; , . / ` [ ] etc.): ask the keyboard layout.
        uint scan = N.MapVirtualKey(vk, 0);
        var name = new StringBuilder(64);
        if (scan != 0 && N.GetKeyNameText((int)(scan << 16), name, name.Capacity) > 0) return name.ToString();
        return $"0x{vk:X2}";
    }

    /// <summary>Parses "Ctrl+Alt+G", "Win+Shift+F5", "none".</summary>
    public static bool TryParse(string text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text) || text.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        uint mods = 0, key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var t = raw.ToLowerInvariant();
            switch (t)
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win" or "windows": mods |= MOD_WIN; continue;
            }
            if (key != 0) return false;
            if (t.Length == 1 && char.IsLetterOrDigit(t[0])) key = char.ToUpperInvariant(t[0]);
            else if (t[0] == 'f' && int.TryParse(t[1..], out int f) && f is >= 1 and <= 24) key = (uint)(0x6F + f);
            else
            {
                for (uint vk = 1; vk < 256 && key == 0; vk++)
                    if (KeyName(vk).Equals(raw, StringComparison.OrdinalIgnoreCase)) key = vk;
                if (key == 0) return false;
            }
        }
        if (key == 0) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }
}

/// <summary>
/// Registers the per-rule global shortcuts with RegisterHotKey on the tray host window.
/// RegisterHotKey is system-wide and works whatever window is focused (including elevated ones).
/// Registration fails if another app (or another rule) already owns the combination.
/// </summary>
public sealed class HotkeyManager
{
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, string> _byId = new();
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AppRule> _rules = Array.Empty<AppRule>();
    private bool _suspended;

    public HotkeyManager(IntPtr hwnd) { _hwnd = hwnd; }

    public event Action? StatusChanged;

    public string? ErrorFor(string exe) => _errors.TryGetValue(exe, out var e) ? e : null;
    public string? ExeForId(int id) => _byId.TryGetValue(id, out var exe) ? exe : null;

    public void Apply(IReadOnlyList<AppRule> rules)
    {
        _rules = rules.ToList();
        if (!_suspended) RegisterAll();
    }

    /// <summary>Temporarily releases all shortcuts (while the recorder is capturing a new one).</summary>
    public void Suspend() { _suspended = true; UnregisterAll(); }
    public void Resume() { _suspended = false; RegisterAll(); }

    private void UnregisterAll()
    {
        foreach (var id in _byId.Keys) N.UnregisterHotKey(_hwnd, id);
        _byId.Clear();
    }

    private void RegisterAll()
    {
        UnregisterAll();
        _errors.Clear();
        int id = 1;
        foreach (var r in _rules)
        {
            if (r.HotkeyKey == 0) continue;
            var hk = new Hotkey(r.HotkeyModifiers, r.HotkeyKey);
            if (N.RegisterHotKey(_hwnd, id, r.HotkeyModifiers | Hotkey.MOD_NOREPEAT, r.HotkeyKey))
            {
                _byId[id] = r.Exe;
                Log.Info($"Hotkey {hk} registered for {r.Exe}");
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                _errors[r.Exe] = err == 1409
                    ? $"{hk} is already in use by another app (or another rule). Pick a different shortcut."
                    : $"Couldn't register {hk} (error {err}).";
                Log.Warn($"Hotkey {hk} for {r.Exe} failed: error {err}");
            }
            id++;
        }
        StatusChanged?.Invoke();
    }

    public string Describe() => string.Join("\n", _rules.Where(r => r.HotkeyKey != 0).Select(r =>
        $"{r.Exe}\t{new Hotkey(r.HotkeyModifiers, r.HotkeyKey)}\t{(ErrorFor(r.Exe) is { } e ? "FAILED: " + e : _suspended ? "suspended" : "registered")}"));
}
