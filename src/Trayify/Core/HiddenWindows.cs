using System.Text.Json;
using Trayify.Native;

namespace Trayify.Core;

public enum HideReason { CloseButton, MinimizeRightClick, AltF4, Manual, Command }

/// <summary>Persisted record of a window Trayify hid (used for crash/kill recovery).</summary>
public sealed class HiddenRecord
{
    public long Hwnd { get; set; }
    public uint Pid { get; set; }
    public long ProcessStart { get; set; }
    public string Exe { get; set; } = "";
    public string Title { get; set; } = "";
    public List<long> Owned { get; set; } = new();
}

public sealed class HiddenWindow
{
    public required IntPtr Hwnd { get; init; }
    public required uint Pid { get; init; }
    public required long ProcessStart { get; init; }
    public required string ExeName { get; init; }
    public string? ExePath { get; init; }
    public required string Title { get; set; }
    public required RECT Rect { get; init; }
    public required bool WasMaximized { get; init; }
    public required HideReason Reason { get; init; }
    public DateTime HiddenAt { get; init; } = DateTime.Now;
    public List<IntPtr> Owned { get; init; } = new();
    public uint IconId { get; set; }
    public IntPtr TrayIcon { get; set; }

    public HiddenRecord ToRecord() => new()
    {
        Hwnd = (long)Hwnd, Pid = Pid, ProcessStart = ProcessStart, Exe = ExeName, Title = Title,
        Owned = Owned.Select(o => (long)o).ToList(),
    };
}

/// <summary>Restores windows recorded in hidden.json by a previous Trayify instance that died.</summary>
public static class Recovery
{
    public static List<HiddenRecord> ReadFile()
    {
        try
        {
            if (File.Exists(Paths.HiddenFile))
                return JsonSerializer.Deserialize<List<HiddenRecord>>(File.ReadAllText(Paths.HiddenFile)) ?? new();
        }
        catch (Exception ex) { Log.Error("Failed to read hidden.json", ex); }
        return new();
    }

    public static void WriteFile(IEnumerable<HiddenRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var tmp = Paths.HiddenFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(records.ToList()));
            File.Move(tmp, Paths.HiddenFile, true);
        }
        catch (Exception ex) { Log.Error("Failed to write hidden.json", ex); }
    }

    /// <summary>Shows every still-hidden window from the file (validated by pid + process start time), then clears it.</summary>
    public static int RestoreFromFile(string why)
    {
        var records = ReadFile();
        int restored = 0;
        foreach (var r in records)
        {
            var h = (IntPtr)r.Hwnd;
            try
            {
                if (!N.IsWindow(h) || N.ProcessIdOf(h) != r.Pid) continue;
                if (r.ProcessStart != 0 && ProcessInfo.QueryStartTime(r.Pid) != r.ProcessStart) continue;
                if (!N.IsWindowVisible(h))
                {
                    N.ShowWindow(h, N.SW_SHOWNA);
                    restored++;
                }
                foreach (var o in r.Owned)
                    if (N.IsWindow((IntPtr)o) && N.ProcessIdOf((IntPtr)o) == r.Pid && !N.IsWindowVisible((IntPtr)o))
                        N.ShowWindow((IntPtr)o, N.SW_SHOWNA);
                Log.Info($"Recovery ({why}): restored {r.Exe} '{r.Title}' hwnd={r.Hwnd}");
            }
            catch (Exception ex) { Log.Error($"Recovery failed for hwnd={r.Hwnd}", ex); }
        }
        if (records.Count > 0) WriteFile(Array.Empty<HiddenRecord>());
        return restored;
    }
}

/// <summary>Owns the set of windows Trayify has hidden. UI-thread only.</summary>
public sealed class HiddenWindowManager
{
    private readonly List<HiddenWindow> _hidden = new();
    private uint _nextIconId = 100;

    public IReadOnlyList<HiddenWindow> Hidden => _hidden;
    public event Action<HiddenWindow>? WindowHidden;
    public event Action<HiddenWindow>? WindowRemoved;
    public event Action? Changed;

    public HiddenWindow? Find(IntPtr hwnd) => _hidden.FirstOrDefault(w => w.Hwnd == hwnd);

    public HiddenWindow? Hide(IntPtr hwnd, HideReason reason)
    {
        var root = N.GetAncestor(hwnd, N.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        if (WindowUtil.IsProtected(root)) { Log.Warn($"Refusing to hide protected window {root}"); return null; }
        if (Find(root) != null) return null;
        if (!N.IsWindowVisible(root)) return null;

        uint pid = N.ProcessIdOf(root);
        N.GetWindowRect(root, out var rect);
        var hw = new HiddenWindow
        {
            Hwnd = root,
            Pid = pid,
            ProcessStart = ProcessInfo.QueryStartTime(pid),
            ExeName = ProcessInfo.GetExeName(pid) ?? "(unknown)",
            ExePath = ProcessInfo.GetExePath(pid),
            Title = N.GetText(root),
            Rect = rect,
            WasMaximized = N.IsZoomed(root),
            Reason = reason,
            IconId = _nextIconId++,
        };

        var next = WindowUtil.NextAppWindowBelow(root);

        // Owned windows (dialogs, palettes) go with their owner.
        foreach (var o in WindowUtil.GetVisibleOwnedWindows(root))
        {
            N.ShowWindow(o, N.SW_HIDE);
            hw.Owned.Add(o);
        }
        N.ShowWindow(root, N.SW_HIDE);
        if (N.IsWindowVisible(root))
        {
            Log.Warn($"Could not hide {hw.ExeName} hwnd={root} (insufficient privileges?)");
            foreach (var o in hw.Owned) N.ShowWindow(o, N.SW_SHOWNA);
            return null;
        }

        // A hidden window can stay "foreground" (and keep receiving keystrokes). Hand activation to
        // the next window in Z-order, as Windows does when a window closes.
        var fg = N.GetForegroundWindow();
        if (next != IntPtr.Zero && (fg == root || fg == IntPtr.Zero || hw.Owned.Contains(fg) || N.GetAncestor(fg, N.GA_ROOTOWNER) == root))
        {
            bool ok = WindowUtil.ForceForeground(next);
            Log.Info($"Activated next window {next} '{N.GetText(next)}' ok={ok}");
        }
        else Log.Info($"No re-activation: next={next} fg={fg}");

        hw.TrayIcon = WindowUtil.GetIcon(root, hw.ExePath, WindowUtil.TrayIconSize());
        _hidden.Add(hw);
        Persist();
        Log.Info($"Hid {hw.ExeName} '{hw.Title}' hwnd={root} reason={reason} rect={rect} max={hw.WasMaximized}");
        WindowHidden?.Invoke(hw);
        Changed?.Invoke();
        return hw;
    }

    public bool Restore(IntPtr hwnd, bool focus = true)
    {
        var hw = Find(hwnd);
        if (hw == null) return false;
        Remove(hw);

        if (!N.IsWindow(hw.Hwnd)) { Log.Info($"Window {hw.Hwnd} no longer exists"); return false; }

        N.ShowWindow(hw.Hwnd, focus ? N.SW_SHOW : N.SW_SHOWNA);
        // Put it back exactly where it was if the app (or a DPI change) moved it while hidden.
        if (!hw.WasMaximized && !N.IsZoomed(hw.Hwnd) && N.GetWindowRect(hw.Hwnd, out var now) && !now.SameAs(hw.Rect))
            N.SetWindowPos(hw.Hwnd, IntPtr.Zero, hw.Rect.Left, hw.Rect.Top, hw.Rect.Width, hw.Rect.Height, N.SWP_NOZORDER | N.SWP_NOACTIVATE);
        foreach (var o in hw.Owned)
            if (N.IsWindow(o)) N.ShowWindow(o, N.SW_SHOWNA);

        bool fg = !focus || WindowUtil.ForceForeground(hw.Hwnd);
        Log.Info($"Restored {hw.ExeName} '{hw.Title}' hwnd={hw.Hwnd} foreground={fg}");
        return true;
    }

    public int RestoreAll(bool focusLast = true)
    {
        var all = _hidden.ToList();
        for (int i = 0; i < all.Count; i++)
            Restore(all[i].Hwnd, focusLast && i == all.Count - 1);
        Persist();
        return all.Count;
    }

    /// <summary>Drops windows that were destroyed or re-shown by their app (e.g. relaunching it).</summary>
    public void Prune()
    {
        foreach (var hw in _hidden.ToList())
        {
            if (!N.IsWindow(hw.Hwnd) || N.ProcessIdOf(hw.Hwnd) != hw.Pid)
            {
                Log.Info($"Hidden window {hw.Hwnd} ({hw.ExeName}) was destroyed");
                Remove(hw);
            }
            else if (N.IsWindowVisible(hw.Hwnd))
            {
                Log.Info($"Hidden window {hw.Hwnd} ({hw.ExeName}) was shown by its app");
                Remove(hw);
            }
        }
    }

    private void Remove(HiddenWindow hw)
    {
        if (!_hidden.Remove(hw)) return;
        Persist();
        WindowRemoved?.Invoke(hw);
        if (hw.TrayIcon != IntPtr.Zero) { N.DestroyIcon(hw.TrayIcon); hw.TrayIcon = IntPtr.Zero; }
        Changed?.Invoke();
    }

    private void Persist() => Recovery.WriteFile(_hidden.Select(h => h.ToRecord()));
}
