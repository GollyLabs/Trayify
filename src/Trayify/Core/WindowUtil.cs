using Trayify.Native;

namespace Trayify.Core;

public sealed record AppWindowInfo(IntPtr Hwnd, string Title, uint Pid, string ExeName, string? ExePath);

public static class WindowUtil
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow", "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
    };

    /// <summary>True if this is a window we must never hide (desktop, taskbar, our own windows...).</summary>
    public static bool IsProtected(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !N.IsWindow(hwnd)) return true;
        if (N.ProcessIdOf(hwnd) == (uint)Environment.ProcessId) return true;
        return ShellClasses.Contains(N.GetClass(hwnd));
    }

    /// <summary>Approximation of the Alt+Tab list: visible, uncloaked, titled, unowned app windows.</summary>
    public static List<AppWindowInfo> GetAppWindows()
    {
        var result = new List<AppWindowInfo>();
        uint self = (uint)Environment.ProcessId;
        N.EnumWindows((h, _) =>
        {
            try
            {
                if (!IsAppWindow(h)) return true;
                var title = N.GetText(h);
                uint pid = N.ProcessIdOf(h);
                if (pid == self) return true;
                var path = ProcessInfo.GetExePath(pid);
                var name = ProcessInfo.GetExeName(pid) ?? "(unknown)";
                result.Add(new AppWindowInfo(h, title, pid, name, path));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Alt+Tab-style test: visible, uncloaked, titled, unowned (or WS_EX_APPWINDOW), not a tool window.</summary>
    public static bool IsAppWindow(IntPtr h)
    {
        if (!N.IsWindowVisible(h) || N.IsCloaked(h)) return false;
        long ex = N.ExStyle(h);
        if ((ex & N.WS_EX_TOOLWINDOW) != 0 && (ex & N.WS_EX_APPWINDOW) == 0) return false;
        if ((ex & N.WS_EX_NOACTIVATE) != 0 && (ex & N.WS_EX_APPWINDOW) == 0) return false;
        if (N.GetWindow(h, N.GW_OWNER) != IntPtr.Zero && (ex & N.WS_EX_APPWINDOW) == 0) return false;
        if (N.GetWindowTextLength(h) == 0) return false;
        return !ShellClasses.Contains(N.GetClass(h));
    }

    /// <summary>The next app window below <paramref name="hwnd"/> in Z-order (what Windows would activate on close).</summary>
    public static IntPtr NextAppWindowBelow(IntPtr hwnd)
    {
        const uint GW_HWNDNEXT = 2;
        var h = N.GetWindow(hwnd, GW_HWNDNEXT);
        for (int i = 0; h != IntPtr.Zero && i < 2000; i++, h = N.GetWindow(h, GW_HWNDNEXT))
            if (N.GetWindow(h, N.GW_OWNER) != hwnd && IsAppWindow(h)) return h;
        return IntPtr.Zero;
    }

    /// <summary>Visible top-level windows owned by <paramref name="owner"/> (dialogs, tool palettes).</summary>
    public static List<IntPtr> GetVisibleOwnedWindows(IntPtr owner)
    {
        var list = new List<IntPtr>();
        N.EnumWindows((h, _) =>
        {
            if (h != owner && N.IsWindowVisible(h) && N.GetWindow(h, N.GW_OWNER) == owner) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Gets an owned HICON for a window/exe at the requested pixel size. Caller must DestroyIcon.</summary>
    public static IntPtr GetIcon(IntPtr hwnd, string? exePath, int size)
    {
        // 1) Exe resource at exactly the right size (crisp at any DPI)
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            try
            {
                var icons = new IntPtr[1];
                if (N.PrivateExtractIcons(exePath, 0, size, size, icons, null, 1, 0) == 1 && icons[0] != IntPtr.Zero)
                    return icons[0];
            }
            catch { }
        }
        // 2) The window's own icon (shared handle -> copy it)
        if (hwnd != IntPtr.Zero)
        {
            foreach (var which in size > 24 ? new[] { N.ICON_BIG, N.ICON_SMALL2, N.ICON_SMALL } : new[] { N.ICON_SMALL2, N.ICON_SMALL, N.ICON_BIG })
            {
                if (N.SendMessageTimeout(hwnd, N.WM_GETICON, (IntPtr)which, IntPtr.Zero, N.SMTO_ABORTIFHUNG, 100, out var hi) != IntPtr.Zero && hi != IntPtr.Zero)
                    return N.CopyIcon(hi);
            }
            var cls = N.GetClassLongPtr(hwnd, size > 24 ? N.GCLP_HICON : N.GCLP_HICONSM);
            if (cls == IntPtr.Zero) cls = N.GetClassLongPtr(hwnd, N.GCLP_HICON);
            if (cls != IntPtr.Zero) return N.CopyIcon(cls);
        }
        // 3) Trayify's own icon
        return LoadAppIcon(size);
    }

    public static IntPtr LoadAppIcon(int size) =>
        N.LoadImage(IntPtr.Zero, Paths.IconFile, N.IMAGE_ICON, size, size, N.LR_LOADFROMFILE);

    public static int TrayIconSize() => N.GetSystemMetricsForDpi(N.SM_CXSMICON, N.GetDpiForSystem());

    /// <summary>
    /// Bring a window to the foreground reliably. SetForegroundWindow is subject to the foreground lock;
    /// when we were invoked from the tray the shell has already granted us the right. Otherwise we fall
    /// back to attaching to the foreground thread's input queue, then to a harmless injected key.
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (N.IsIconic(hwnd)) N.ShowWindow(hwnd, N.SW_RESTORE);
        if (N.SetForegroundWindow(hwnd) && N.GetForegroundWindow() == hwnd) return true;

        var fg = N.GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? N.GetWindowThreadProcessId(fg, out _) : 0;
        uint me = N.GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me)
        {
            N.AttachThreadInput(me, fgThread, true);
            try
            {
                N.BringWindowToTop(hwnd);
                N.SetForegroundWindow(hwnd);
            }
            finally { N.AttachThreadInput(me, fgThread, false); }
        }
        if (N.GetForegroundWindow() == hwnd) return true;

        // Unassigned virtual key (0xE8): makes us "the process that received the last input" without
        // side effects in the target app (unlike the classic Alt trick that can open menus).
        var inputs = new[] { N.Key(0xE8, false, InputInterceptor.InjectionMarker), N.Key(0xE8, true, InputInterceptor.InjectionMarker) };
        N.SendInput((uint)inputs.Length, inputs, N.InputSize);
        Thread.Sleep(30);
        N.SetForegroundWindow(hwnd);
        N.BringWindowToTop(hwnd);
        return N.GetForegroundWindow() == hwnd;
    }
}
