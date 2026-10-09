// TrayifyTest - drives real input against real windows to verify Trayify end to end.
// All clicks on Close buttons use a fail-safe protocol: press, ask Trayify (via its control pipe)
// whether it swallowed the press, and only release on the button if it did; otherwise drag the
// pointer off the button before releasing, which cancels the click in every caption-button
// implementation, so the target window is never actually closed.
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

static class T
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public override string ToString() => $"[{L},{T},{R},{B}]"; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct U { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public U u; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, byte[] bmi, uint usage);

    static string Text(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
    static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    static uint Pid(IntPtr h) { GetWindowThreadProcessId(h, out uint p); return p; }
    static string ProcName(uint pid) { try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; } }

    static int Hit(IntPtr h, int x, int y)
    {
        var lp = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        return SendMessageTimeout(h, 0x84, IntPtr.Zero, lp, 2, 200, out var r) == IntPtr.Zero ? -999 : unchecked((int)(long)r);
    }

    // ---- pipe ----
    public static string? Pipe(string cmd)
    {
        try
        {
            var name = $"Trayify.Control.{Process.GetCurrentProcess().SessionId}";
            using var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            c.Connect(3000);
            var w = new StreamWriter(c, new UTF8Encoding(false)) { AutoFlush = true };
            var r = new StreamReader(c, Encoding.UTF8);
            w.WriteLine(cmd);
            var sb = new StringBuilder(); string? l;
            while ((l = r.ReadLine()) != null && l != ".") sb.AppendLine(l);
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) { return "PIPE-ERROR " + ex.Message; }
    }

    // ---- input ----
    static void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var i = new INPUT { type = 0 };
        i.u.mi.dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        i.u.mi.dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        i.u.mi.flags = 0x0001 | 0x8000 | 0x4000; // MOVE | ABSOLUTE | VIRTUALDESK
        SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
    }
    static void MouseButton(uint flag) { var i = new INPUT { type = 0 }; i.u.mi.flags = flag; SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>()); }
    static void Key(ushort vk, bool up) { var i = new INPUT { type = 1 }; i.u.ki.vk = vk; i.u.ki.flags = up ? 2u : 0u; SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>()); }

    /// <summary>Scan the title band with WM_NCHITTEST to find the centre of a caption button.</summary>
    static (int x, int y)? FindButton(IntPtr h, int ht)
    {
        GetWindowRect(h, out var r);
        double s = GetDpiForWindow(h) / 96.0;
        int top = Math.Max(r.T, MonitorTop(r)), right = r.R;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (int y = top; y < top + (int)(60 * s); y += 2)
            for (int x = right - 1; x > right - (int)(300 * s) && x > r.L; x -= 2)
                if (Hit(h, x, y) == ht) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (minX == int.MaxValue) return null;
        return ((minX + maxX) / 2, (minY + maxY) / 2);
    }
    static int MonitorTop(RECT r) => IsZoomed(IntPtr.Zero) ? r.T : (r.T < 0 ? 0 : r.T);

    static string Info(IntPtr h)
    {
        if (!IsWindow(h)) return $"hwnd={h} DESTROYED";
        GetWindowRect(h, out var r);
        uint pid = Pid(h);
        return $"hwnd={h} visible={IsWindowVisible(h)} iconic={IsIconic(h)} zoomed={IsZoomed(h)} foreground={GetForegroundWindow() == h} rect={r} pid={pid} proc={ProcName(pid)} alive={ProcessAlive(pid)} title='{Text(h)}'";
    }
    static bool ProcessAlive(uint pid) { try { return !Process.GetProcessById((int)pid).HasExited; } catch { return false; } }

    /// <summary>Fail-safe click on a caption button. Returns true if Trayify intercepted it.</summary>
    static bool SafeClick(IntPtr h, bool close, (int x, int y)? at = null)
    {
        int ht = close ? 20 : 8;
        var pos = at ?? FindButton(h, ht);
        if (pos == null) { Console.WriteLine($"ABORT: no {(close ? "HTCLOSE" : "HTMINBUTTON")} region found"); return false; }
        var (x, y) = pos.Value;
        if (at != null)
        {
            // App-drawn buttons: hover first so Trayify's UI Automation worker can identify the button.
            MoveTo(x - 3, y); Thread.Sleep(150); MoveTo(x, y); Thread.Sleep(900);
        }
        var probe = Pipe($"probe {x} {y}") ?? "";
        Console.WriteLine($"button at {x},{y}; probe: {probe}");
        var expect = close ? "leftClickWouldTray=True" : "rightClickWouldTray=True";
        if (!probe.Contains(expect) || !probe.Contains($"hwnd={(long)h} ")) { Console.WriteLine("ABORT: Trayify would not intercept this click; not clicking."); return false; }

        GetCursorPos(out var saved);
        bool ok = false;
        try
        {
            MoveTo(x, y); Thread.Sleep(350);
            MouseButton(close ? 0x0002u : 0x0008u); // down
            Thread.Sleep(150);
            var pending = Pipe("pending") ?? "";
            ok = pending.Contains((close ? "close=" : "min=") + (long)h);
            Console.WriteLine($"after press: {pending} -> {(ok ? "SWALLOWED" : "NOT swallowed")}");
            if (!ok)
            {
                // Cancel: drag off the button (to the middle of the window) before releasing.
                GetWindowRect(h, out var r);
                MoveTo((r.L + r.R) / 2, (r.T + r.B) / 2); Thread.Sleep(150);
            }
            MouseButton(close ? 0x0004u : 0x0010u); // up
            if (!ok && !close) { Thread.Sleep(200); Key(0x1B, false); Key(0x1B, true); } // dismiss a system menu, if any
        }
        finally
        {
            Thread.Sleep(100);
            MoveTo(saved.X, saved.Y);
        }
        Thread.Sleep(600);
        return ok;
    }

    static void Screenshot(string path, int x, int y, int w, int hgt)
    {
        var sdc = GetDC(IntPtr.Zero); var mdc = CreateCompatibleDC(sdc); var bmp = CreateCompatibleBitmap(sdc, w, hgt);
        var old = SelectObject(mdc, bmp);
        BitBlt(mdc, 0, 0, w, hgt, sdc, x, y, 0x00CC0020 | 0x40000000);
        SelectObject(mdc, old);
        var bmi = new byte[40 + 1024];
        BitConverter.GetBytes(40).CopyTo(bmi, 0); BitConverter.GetBytes(w).CopyTo(bmi, 4); BitConverter.GetBytes(-hgt).CopyTo(bmi, 8);
        BitConverter.GetBytes((short)1).CopyTo(bmi, 12); BitConverter.GetBytes((short)32).CopyTo(bmi, 14);
        var bits = new byte[w * hgt * 4];
        GetDIBits(mdc, bmp, 0, (uint)hgt, bits, bmi, 0);
        DeleteObject(bmp); DeleteDC(mdc); ReleaseDC(IntPtr.Zero, sdc);
        using var f = File.Create(path); using var bw = new BinaryWriter(f);
        bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(54 + bits.Length); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(-hgt); bw.Write((short)1); bw.Write((short)32); bw.Write(0); bw.Write(bits.Length); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        bw.Write(bits);
    }

    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length == 0) { Console.WriteLine("usage: windows [filter] | info <hwnd> | find <hwnd> close|min | click-close <hwnd> | rclick-min <hwnd> | alt-f4 <hwnd> | foreground <hwnd> | pipe <cmd> | screenshot <file.bmp> x y w h | wm-close <hwnd> | hitscan <hwnd>"); return 1; }
        IntPtr H(int i) => (IntPtr)long.Parse(a[i]);
        switch (a[0])
        {
            case "windows":
                EnumWindows((h, _) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    var t = Text(h); if (t.Length == 0) return true;
                    var line = $"{h}\t{ProcName(Pid(h))}\t{Cls(h)}\t{t}";
                    if (a.Length < 2 || line.Contains(a[1], StringComparison.OrdinalIgnoreCase)) Console.WriteLine(line);
                    return true;
                }, IntPtr.Zero);
                return 0;
            case "info": Console.WriteLine(Info(H(1))); return 0;
            case "find":
            {
                var p = FindButton(H(1), a[2] == "close" ? 20 : 8);
                Console.WriteLine(p == null ? "none" : $"{p.Value.x} {p.Value.y}");
                return 0;
            }
            case "hitscan":
            {
                var h = H(1); GetWindowRect(h, out var r);
                for (int y = Math.Max(r.T, 0); y < Math.Max(r.T, 0) + 80; y += 8)
                {
                    var sb = new StringBuilder($"y={y}: ");
                    for (int x = r.R - 2; x > r.R - 260; x -= 12) sb.Append(Hit(h, x, y)).Append(' ');
                    Console.WriteLine(sb);
                }
                return 0;
            }
            case "click-close":
            case "rclick-min":
            {
                var h = H(1);
                Console.WriteLine("before: " + Info(h));
                bool ok = SafeClick(h, a[0] == "click-close");
                Console.WriteLine("after:  " + Info(h));
                Console.WriteLine(ok ? "RESULT: intercepted" : "RESULT: not intercepted (click cancelled safely)");
                return ok ? 0 : 3;
            }
            case "click-close-at":
            case "rclick-min-at":
            {
                // same fail-safe protocol, at an explicit point (for app-drawn caption buttons)
                var h = H(1);
                Console.WriteLine("before: " + Info(h));
                bool ok = SafeClick(h, a[0] == "click-close-at", (int.Parse(a[2]), int.Parse(a[3])));
                Console.WriteLine("after:  " + Info(h));
                Console.WriteLine(ok ? "RESULT: intercepted" : "RESULT: not intercepted (click cancelled safely)");
                return ok ? 0 : 3;
            }
            case "custom-window":
            {
                // A borderless window whose caption buttons are ordinary client-area buttons (like
                // Electron/Discord custom title bars): WM_NCHITTEST returns HTCLIENT there, so only
                // Trayify's UI Automation fallback can recognise them.
                System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
                System.Windows.Forms.Application.EnableVisualStyles();
                var f = new System.Windows.Forms.Form
                {
                    Text = "Trayify custom title bar test", FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    Bounds = new System.Drawing.Rectangle(400, 300, 1100, 600), BackColor = System.Drawing.Color.FromArgb(32, 32, 36),
                };
                var bar = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Top, Height = 48, BackColor = System.Drawing.Color.FromArgb(45, 45, 52) };
                var label = new System.Windows.Forms.Label { Text = "Custom title bar (test window)", ForeColor = System.Drawing.Color.White, AutoSize = true, Location = new System.Drawing.Point(14, 14) };
                System.Windows.Forms.Button Btn(string glyph, string name, int right) => new()
                {
                    Text = glyph, AccessibleName = name, Width = 70, Height = 48, FlatStyle = System.Windows.Forms.FlatStyle.Flat,
                    ForeColor = System.Drawing.Color.White, Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right,
                    Location = new System.Drawing.Point(1100 - right, 0),
                };
                var close = Btn("✕", "Close", 70); close.FlatAppearance.BorderSize = 0; close.Click += (_, _) => f.Close();
                var min = Btn("—", "Minimize", 140); min.FlatAppearance.BorderSize = 0; min.Click += (_, _) => f.WindowState = System.Windows.Forms.FormWindowState.Minimized;
                bar.Controls.AddRange(new System.Windows.Forms.Control[] { label, min, close });
                f.Controls.Add(bar);
                // Pin the buttons to the bar's right edge in real pixels (independent of DPI autoscaling).
                bar.Layout += (_, _) => { close.Left = bar.ClientSize.Width - close.Width; close.Top = 0; min.Left = close.Left - min.Width; min.Top = 0; };
                f.Shown += (_, _) => bar.PerformLayout();
                System.Windows.Forms.Application.Run(f);
                return 0;
            }
            case "alt-f4":
            {
                var h = H(1);
                var probe = Pipe("status") ?? "";
                if (Pid(h) == 0 || ProcName(Pid(h)).Contains("Grok", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("REFUSED: never Alt+F4 Grok Bot"); return 4; }
                Console.WriteLine("before: " + Info(h));
                SetForegroundWindow(h); Thread.Sleep(300);
                if (GetForegroundWindow() != h) { Console.WriteLine("ABORT: could not focus target"); return 5; }
                Key(0x12, false); Key(0x73, false); Key(0x73, true); Key(0x12, true);
                Thread.Sleep(700);
                Console.WriteLine("after:  " + Info(h));
                return 0;
            }
            case "foreground": SetForegroundWindow(H(1)); Thread.Sleep(200); Console.WriteLine(Info(H(1))); return 0;
            case "wm-close": PostMessage(H(1), 0x10, IntPtr.Zero, IntPtr.Zero); return 0;
            case "pipe": Console.WriteLine(Pipe(string.Join(' ', a.Skip(1)))); return 0;
            case "screenshot": Screenshot(a[1], int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]), int.Parse(a[5])); return 0;
            case "cursor": GetCursorPos(out var c); Console.WriteLine($"{c.X} {c.Y}"); return 0;
            case "click":
            {
                // click <x> <y> [left|right]  (plain click, e.g. on a tray icon; cursor is put back afterwards)
                GetCursorPos(out var saved);
                bool right = a.Length > 3 && a[3] == "right";
                MoveTo(int.Parse(a[1]), int.Parse(a[2])); Thread.Sleep(250);
                MouseButton(right ? 0x0008u : 0x0002u); Thread.Sleep(60); MouseButton(right ? 0x0010u : 0x0004u);
                Thread.Sleep(right ? 0 : 150);
                if (!right) MoveTo(saved.X, saved.Y);
                return 0;
            }
            case "key":
            {
                // key <vk-hex> : press and release a key (e.g. 1B = Esc)
                var vk = Convert.ToUInt16(a[1], 16); Key(vk, false); Key(vk, true); return 0;
            }
        }
        Console.WriteLine("unknown command");
        return 1;
    }
}
