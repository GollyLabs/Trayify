using System.Runtime.InteropServices;
using Trayify.Native;

namespace Trayify.Core;

/// <summary>
/// Hidden Win32 window that owns all notification-area icons: Trayify's own icon (menu of hidden
/// windows) plus one icon per hidden window (that app's icon; left-click restores it).
/// Lives on the UI thread, whose message loop dispatches its messages.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private const uint WM_TRAY = N.WM_APP + 1;
    private const uint MainIconId = 1;
    private const uint IDM_OPEN = 1, IDM_RESTORE_ALL = 2, IDM_TOGGLE_RCLICK = 3, IDM_QUIT = 4, IDM_RESTORE_ONE = 5, IDM_WINDOW_BASE = 1000;

    private readonly WndProc _wndProc;
    private readonly uint _taskbarCreated;
    private IntPtr _hwnd;
    private IntPtr _mainIcon;
    private readonly Dictionary<uint, HiddenWindow> _icons = new();

    public Action? OpenRequested;
    public Action<IntPtr>? RestoreRequested;
    public Action? RestoreAllRequested;
    public Action? QuitRequested;
    public Action<bool>? RightClickToggled;
    public Func<bool>? GetRightClickEnabled;
    public Func<IReadOnlyList<HiddenWindow>>? GetHidden;
    public Action? Tick;
    public Action<int>? HotkeyPressed;

    public IntPtr Handle => _hwnd;

    public TrayHost()
    {
        _wndProc = WndProc;
        _taskbarCreated = N.RegisterWindowMessage("TaskbarCreated");
        var hInst = N.GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInst,
            lpszClassName = "TrayifyTrayHost",
        };
        N.RegisterClassEx(ref wc);
        // A real (never shown) top-level window rather than HWND_MESSAGE, so it receives the
        // TaskbarCreated broadcast when Explorer restarts.
        _hwnd = N.CreateWindowEx(0, "TrayifyTrayHost", "Trayify tray host", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        // We run elevated; let the (medium-IL) shell reach us.
        N.ChangeWindowMessageFilterEx(_hwnd, _taskbarCreated, N.MSGFLT_ALLOW, IntPtr.Zero);
        N.ChangeWindowMessageFilterEx(_hwnd, WM_TRAY, N.MSGFLT_ALLOW, IntPtr.Zero);
        N.ChangeWindowMessageFilterEx(_hwnd, N.WM_COMMAND, N.MSGFLT_ALLOW, IntPtr.Zero);
        N.SetTimer(_hwnd, (IntPtr)1, 1500, IntPtr.Zero);

        _mainIcon = WindowUtil.LoadAppIcon(WindowUtil.TrayIconSize());
        AddMainIcon();
    }

    private NOTIFYICONDATA NewData(uint id) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = id,
        szTip = "", szInfo = "", szInfoTitle = "",
    };

    private void AddMainIcon()
    {
        var d = NewData(MainIconId);
        d.uFlags = N.NIF_MESSAGE | N.NIF_ICON | N.NIF_TIP | N.NIF_SHOWTIP;
        d.uCallbackMessage = WM_TRAY;
        d.hIcon = _mainIcon;
        d.szTip = "Trayify";
        if (!N.Shell_NotifyIcon(N.NIM_ADD, ref d)) Log.Warn("Shell_NotifyIcon(NIM_ADD) failed for main icon");
        d.uVersion = N.NOTIFYICON_VERSION_4;
        N.Shell_NotifyIcon(N.NIM_SETVERSION, ref d);
        UpdateMainTip();
    }

    public void UpdateMainTip()
    {
        int n = GetHidden?.Invoke().Count ?? 0;
        var d = NewData(MainIconId);
        d.uFlags = N.NIF_TIP | N.NIF_SHOWTIP;
        d.szTip = n == 0 ? "Trayify" : $"Trayify - {n} hidden window{(n == 1 ? "" : "s")}";
        N.Shell_NotifyIcon(N.NIM_MODIFY, ref d);
    }

    public void AddWindowIcon(HiddenWindow hw)
    {
        _icons[hw.IconId] = hw;
        var d = NewData(hw.IconId);
        d.uFlags = N.NIF_MESSAGE | N.NIF_ICON | N.NIF_TIP | N.NIF_SHOWTIP;
        d.uCallbackMessage = WM_TRAY;
        d.hIcon = hw.TrayIcon != IntPtr.Zero ? hw.TrayIcon : _mainIcon;
        d.szTip = Truncate($"{hw.Title}\n{hw.ExeName} - click to restore", 127);
        if (!N.Shell_NotifyIcon(N.NIM_ADD, ref d)) Log.Warn($"Shell_NotifyIcon(NIM_ADD) failed for {hw.ExeName}");
        d.uVersion = N.NOTIFYICON_VERSION_4;
        N.Shell_NotifyIcon(N.NIM_SETVERSION, ref d);
        UpdateMainTip();
    }

    public void RemoveWindowIcon(HiddenWindow hw)
    {
        if (!_icons.Remove(hw.IconId)) return;
        var d = NewData(hw.IconId);
        N.Shell_NotifyIcon(N.NIM_DELETE, ref d);
        UpdateMainTip();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAY)
            {
                uint ev = (uint)((long)lParam & 0xFFFF);
                uint id = (uint)(((long)lParam >> 16) & 0xFFFF);
                int x = (short)((long)wParam & 0xFFFF), y = (short)(((long)wParam >> 16) & 0xFFFF);
                OnTrayEvent(id, ev, x, y);
                return IntPtr.Zero;
            }
            if (msg == _taskbarCreated)
            {
                Log.Info("Explorer restarted; re-adding tray icons");
                AddMainIcon();
                foreach (var hw in _icons.Values.ToList()) AddWindowIcon(hw);
                return IntPtr.Zero;
            }
            if (msg == N.WM_TIMER) { Tick?.Invoke(); return IntPtr.Zero; }
            if (msg == N.WM_HOTKEY) { HotkeyPressed?.Invoke((int)wParam); return IntPtr.Zero; }
        }
        catch (Exception ex) { Log.Error("Tray WndProc error", ex); }
        return N.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnTrayEvent(uint id, uint ev, int x, int y)
    {
        bool leftClick = ev == N.NIN_SELECT || ev == N.NIN_KEYSELECT;
        bool rightClick = ev == N.WM_CONTEXTMENU;
        if (!leftClick && !rightClick) return;

        if (id == MainIconId)
        {
            if (leftClick) OpenRequested?.Invoke();
            else ShowMainMenu(x, y);
            return;
        }
        if (!_icons.TryGetValue(id, out var hw)) return;
        if (leftClick) RestoreRequested?.Invoke(hw.Hwnd);
        else ShowWindowMenu(hw, x, y);
    }

    private void ShowMainMenu(int x, int y)
    {
        var hidden = GetHidden?.Invoke() ?? Array.Empty<HiddenWindow>();
        var menu = N.CreatePopupMenu();
        try
        {
            N.AppendMenu(menu, N.MF_STRING, (UIntPtr)IDM_OPEN, "Open Trayify");
            N.SetMenuDefaultItem(menu, IDM_OPEN, 0);
            N.AppendMenu(menu, N.MF_SEPARATOR, UIntPtr.Zero, null);
            if (hidden.Count == 0)
                N.AppendMenu(menu, N.MF_STRING | N.MF_GRAYED, UIntPtr.Zero, "No hidden windows");
            for (int i = 0; i < hidden.Count; i++)
                N.AppendMenu(menu, N.MF_STRING, (UIntPtr)(IDM_WINDOW_BASE + i), Truncate($"Restore  {hidden[i].Title}  ({hidden[i].ExeName})", 80).Replace("&", "&&"));
            N.AppendMenu(menu, N.MF_STRING | (hidden.Count == 0 ? N.MF_GRAYED : 0), (UIntPtr)IDM_RESTORE_ALL, "Restore all");
            N.AppendMenu(menu, N.MF_SEPARATOR, UIntPtr.Zero, null);
            N.AppendMenu(menu, N.MF_STRING | (GetRightClickEnabled?.Invoke() == true ? N.MF_CHECKED : 0), (UIntPtr)IDM_TOGGLE_RCLICK, "Right-click minimize sends to tray");
            N.AppendMenu(menu, N.MF_SEPARATOR, UIntPtr.Zero, null);
            N.AppendMenu(menu, N.MF_STRING, (UIntPtr)IDM_QUIT, "Quit Trayify (restores hidden windows)");

            int cmd = Track(menu, x, y);
            if (cmd == IDM_OPEN) OpenRequested?.Invoke();
            else if (cmd == IDM_RESTORE_ALL) RestoreAllRequested?.Invoke();
            else if (cmd == IDM_TOGGLE_RCLICK) RightClickToggled?.Invoke(!(GetRightClickEnabled?.Invoke() ?? false));
            else if (cmd == IDM_QUIT) QuitRequested?.Invoke();
            else if (cmd >= IDM_WINDOW_BASE && cmd - IDM_WINDOW_BASE < hidden.Count) RestoreRequested?.Invoke(hidden[cmd - (int)IDM_WINDOW_BASE].Hwnd);
        }
        finally { N.DestroyMenu(menu); }
    }

    private void ShowWindowMenu(HiddenWindow hw, int x, int y)
    {
        var menu = N.CreatePopupMenu();
        try
        {
            N.AppendMenu(menu, N.MF_STRING, (UIntPtr)IDM_RESTORE_ONE, Truncate($"Restore  {hw.Title}", 80).Replace("&", "&&"));
            N.SetMenuDefaultItem(menu, IDM_RESTORE_ONE, 0);
            N.AppendMenu(menu, N.MF_STRING, (UIntPtr)IDM_RESTORE_ALL, "Restore all hidden windows");
            N.AppendMenu(menu, N.MF_SEPARATOR, UIntPtr.Zero, null);
            N.AppendMenu(menu, N.MF_STRING, (UIntPtr)IDM_OPEN, "Open Trayify");
            int cmd = Track(menu, x, y);
            if (cmd == IDM_RESTORE_ONE) RestoreRequested?.Invoke(hw.Hwnd);
            else if (cmd == IDM_RESTORE_ALL) RestoreAllRequested?.Invoke();
            else if (cmd == IDM_OPEN) OpenRequested?.Invoke();
        }
        finally { N.DestroyMenu(menu); }
    }

    private int Track(IntPtr menu, int x, int y)
    {
        N.GetCursorPos(out var cur);
        if (x == 0 && y == 0) { x = cur.X; y = cur.Y; }
        N.SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
        int cmd = N.TrackPopupMenuEx(menu, N.TPM_RETURNCMD | N.TPM_RIGHTBUTTON | N.TPM_NONOTIFY | N.TPM_BOTTOMALIGN, x, y, _hwnd, IntPtr.Zero);
        N.PostMessage(_hwnd, N.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        return cmd;
    }

    public void Dispose()
    {
        foreach (var hw in _icons.Values.ToList()) RemoveWindowIcon(hw);
        var d = NewData(MainIconId);
        N.Shell_NotifyIcon(N.NIM_DELETE, ref d);
        if (_hwnd != IntPtr.Zero) { N.KillTimer(_hwnd, (IntPtr)1); N.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_mainIcon != IntPtr.Zero) { N.DestroyIcon(_mainIcon); _mainIcon = IntPtr.Zero; }
    }
}
