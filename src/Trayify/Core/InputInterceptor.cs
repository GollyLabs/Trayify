using System.Runtime.InteropServices;
using Trayify.Native;

namespace Trayify.Core;

/// <summary>
/// Global low-level mouse + keyboard hooks (WH_MOUSE_LL / WH_KEYBOARD_LL) running on a dedicated
/// thread with its own message loop. No DLL injection: the OS calls us before the click reaches the
/// target app, and returning non-zero swallows it.
///
///  * Left-click on the Close button of a window whose exe has a close-to-tray rule -> swallow the
///    down + matching up, hide the window on release (cancelled if released elsewhere).
///  * Right-click on any window's Minimize button (when enabled) -> swallow, hide on release.
///  * Alt+F4 while a close-to-tray app is in the foreground -> swallow, hide.
///
/// The callback must return fast (the OS silently unhooks slow LL hooks), so hit tests use
/// SendMessageTimeout(100 ms) and the hooks are periodically re-installed as a safety net.
/// </summary>
public sealed class InputInterceptor : IDisposable
{
    /// <summary>dwExtraInfo marker on input we inject ourselves, so our hooks ignore it.</summary>
    public static readonly IntPtr InjectionMarker = (IntPtr)0x54524159; // "TRAY"

    private const uint WM_REINSTALL = N.WM_APP + 10, WM_SEND_DUMMY_KEY = N.WM_APP + 11;
    private const uint VK_F4 = 0x73;

    private readonly CaptionButtonDetector _detector;
    private readonly Action<IntPtr, HideReason> _onHide;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook, _kbdHook;
    private HookProc? _mouseProc, _kbdProc; // keep delegates alive
    private readonly uint _selfPid = (uint)Environment.ProcessId;

    private volatile HashSet<string> _rules = new(StringComparer.OrdinalIgnoreCase);
    public volatile bool RightClickMinimize = true;
    public volatile bool AltF4ToTray = true;

    private IntPtr _pendingClose, _pendingMin;
    private bool _swallowF4Up;
    public long PendingClose => (long)_pendingClose;
    public long PendingMinimize => (long)_pendingMin;
    public bool HooksInstalled => _mouseHook != IntPtr.Zero && _kbdHook != IntPtr.Zero;
    public long Intercepts;

    public InputInterceptor(CaptionButtonDetector detector, Action<IntPtr, HideReason> onHide)
    {
        _detector = detector;
        _onHide = onHide;
        _detector.IsRelevant = h => RightClickMinimize || HasRule(h);
    }

    public void SetRules(IEnumerable<string> exes) => _rules = new HashSet<string>(exes, StringComparer.OrdinalIgnoreCase);

    public bool HasRule(IntPtr root)
    {
        var rules = _rules;
        if (rules.Count == 0) return false;
        var exe = ProcessInfo.GetExeName(N.ProcessIdOf(root));
        return exe != null && rules.Contains(exe);
    }

    public void Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => HookThread(ready)) { IsBackground = true, Name = "Trayify input hooks" };
        _thread.Start();
        ready.Wait(5000);
    }

    private void HookThread(ManualResetEventSlim ready)
    {
        _threadId = N.GetCurrentThreadId();
        _mouseProc = MouseProc;
        _kbdProc = KeyboardProc;
        Install();
        // Re-install every 30 s in case Windows silently removed a hook after a timeout.
        N.SetTimer(IntPtr.Zero, IntPtr.Zero, 30_000, IntPtr.Zero);
        ready.Set();
        while (N.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == N.WM_TIMER || msg.message == WM_REINSTALL) { Install(); continue; }
            if (msg.message == WM_SEND_DUMMY_KEY)
            {
                var inputs = new[] { N.Key(0xE8, false, InjectionMarker), N.Key(0xE8, true, InjectionMarker) };
                N.SendInput((uint)inputs.Length, inputs, N.InputSize);
                continue;
            }
            N.TranslateMessage(ref msg);
            N.DispatchMessage(ref msg);
        }
        Uninstall();
    }

    private void Install()
    {
        Uninstall();
        var hMod = N.GetModuleHandle(null);
        _mouseHook = N.SetWindowsHookEx(N.WH_MOUSE_LL, _mouseProc!, hMod, 0);
        _kbdHook = N.SetWindowsHookEx(N.WH_KEYBOARD_LL, _kbdProc!, hMod, 0);
        if (_mouseHook == IntPtr.Zero || _kbdHook == IntPtr.Zero)
            Log.Error($"SetWindowsHookEx failed: mouse={_mouseHook} kbd={_kbdHook} err={Marshal.GetLastWin32Error()}");
    }

    private void Uninstall()
    {
        if (_mouseHook != IntPtr.Zero) { N.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_kbdHook != IntPtr.Zero) { N.UnhookWindowsHookEx(_kbdHook); _kbdHook = IntPtr.Zero; }
    }

    private unsafe IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == N.HC_ACTION)
        {
            var p = (MSLLHOOKSTRUCT*)lParam;
            if (p->dwExtraInfo != InjectionMarker)
            {
                try
                {
                    if (HandleMouse((uint)wParam, p->pt)) return (IntPtr)1;
                }
                catch (Exception ex) { Log.Error("Mouse hook error", ex); }
            }
        }
        return N.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool HandleMouse(uint msg, POINT pt)
    {
        switch (msg)
        {
            case N.WM_MOUSEMOVE:
                if (_pendingClose == IntPtr.Zero && _pendingMin == IntPtr.Zero) _detector.NotifyHover(pt);
                return false;

            case N.WM_LBUTTONDOWN:
            {
                _pendingClose = IntPtr.Zero;
                if (_rules.Count == 0) return false;
                var root = N.RootAt(pt);
                if (!IsCandidate(root) || !HasRule(root)) return false;
                if (_detector.Classify(root, pt, out _) != CaptionButton.Close) return false;
                _pendingClose = root;
                return true;
            }
            case N.WM_LBUTTONUP:
            {
                if (_pendingClose == IntPtr.Zero) return false;
                var h = _pendingClose;
                _pendingClose = IntPtr.Zero;
                if (N.RootAt(pt) == h && _detector.Classify(h, pt, out _) == CaptionButton.Close)
                    Fire(h, HideReason.CloseButton);
                return true; // always swallow the up that matches a swallowed down
            }
            case N.WM_RBUTTONDOWN:
            {
                _pendingMin = IntPtr.Zero;
                if (!RightClickMinimize) return false;
                var root = N.RootAt(pt);
                if (!IsCandidate(root)) return false;
                if (_detector.Classify(root, pt, out _) != CaptionButton.Minimize) return false;
                _pendingMin = root;
                return true;
            }
            case N.WM_RBUTTONUP:
            {
                if (_pendingMin == IntPtr.Zero) return false;
                var h = _pendingMin;
                _pendingMin = IntPtr.Zero;
                if (N.RootAt(pt) == h && _detector.Classify(h, pt, out _) == CaptionButton.Minimize)
                    Fire(h, HideReason.MinimizeRightClick);
                return true;
            }
        }
        return false;
    }

    private bool IsCandidate(IntPtr root) =>
        root != IntPtr.Zero && N.ProcessIdOf(root) != _selfPid && !WindowUtil.IsProtected(root);

    private unsafe IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == N.HC_ACTION)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            try
            {
                if (k->dwExtraInfo != InjectionMarker && k->vkCode == VK_F4)
                {
                    uint msg = (uint)wParam;
                    if (msg == N.WM_SYSKEYDOWN && (k->flags & N.LLKHF_ALTDOWN) != 0 && AltF4ToTray && _rules.Count > 0)
                    {
                        var fg = N.GetForegroundWindow();
                        var root = fg == IntPtr.Zero ? IntPtr.Zero : N.GetAncestor(fg, N.GA_ROOT);
                        if (IsCandidate(root) && HasRule(root))
                        {
                            _swallowF4Up = true;
                            // Keep the app from treating the lone Alt release as "activate menu bar".
                            N.PostThreadMessage(_threadId, WM_SEND_DUMMY_KEY, IntPtr.Zero, IntPtr.Zero);
                            Fire(root, HideReason.AltF4);
                            return (IntPtr)1;
                        }
                    }
                    else if ((msg == N.WM_SYSKEYUP || msg == N.WM_KEYUP) && _swallowF4Up)
                    {
                        _swallowF4Up = false;
                        return (IntPtr)1;
                    }
                }
            }
            catch (Exception ex) { Log.Error("Keyboard hook error", ex); }
        }
        return N.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    private void Fire(IntPtr hwnd, HideReason reason)
    {
        Interlocked.Increment(ref Intercepts);
        _onHide(hwnd, reason);
    }

    public void Dispose()
    {
        if (_threadId != 0) N.PostThreadMessage(_threadId, N.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
        _detector.Dispose();
    }
}
