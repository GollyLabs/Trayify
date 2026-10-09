using System.Text.RegularExpressions;
using Interop.UIAutomationClient;
using Trayify.Native;

namespace Trayify.Core;

public enum CaptionButton { None, Close, Minimize, Maximize }

/// <summary>
/// Figures out whether a screen point is on a window's Close / Minimize caption button.
///
/// 1. WM_NCHITTEST (sent with a short timeout). Standard windows and apps that implement Windows'
///    caption-button contract (Electron's titleBarOverlay / WCO, WinUI, Chromium) answer HTCLOSE /
///    HTMINBUTTON here - this is the path used for Grok Bot.
/// 2. Apps that draw their own buttons in client area (e.g. Discord) answer HTCLIENT. For those a
///    background "hover" worker asks UI Automation what is under the cursor while the mouse is in
///    the title-bar band and caches the button rectangle, so the click itself (inside the hook,
///    which must return quickly) is just a rectangle test.
/// </summary>
public sealed class CaptionButtonDetector : IDisposable
{
    private sealed record UiaHit(IntPtr Hwnd, CaptionButton Kind, RECT ButtonRect, RECT WindowRect, long At);

    private static readonly Regex CloseName = new(@"^(close|close window|schlie(ß|ss)en|fermer|cerrar)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MinName = new(@"^(minimi[sz]e|minimi[sz]e window|minimieren|réduire|minimizar)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const int UIA_ButtonControlTypeId = 50000;

    private volatile UiaHit? _cache;
    private long _hoverPacked;
    private readonly AutoResetEvent _hoverSignal = new(false);
    private readonly Thread _worker;
    private volatile bool _stop;
    private IntPtr _lastUiaHwnd; private POINT _lastUiaPt; private long _lastUiaAt;

    public volatile bool UiaEnabled = true;
    /// <summary>Decides if a window is interesting for hover probing (set by the interceptor).</summary>
    public Func<IntPtr, bool>? IsRelevant;

    public CaptionButtonDetector()
    {
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Trayify UIA hover" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    /// <summary>Height of the band (from the window top) in which caption buttons can live.</summary>
    private static bool InCaptionBand(IntPtr root, POINT pt, out RECT wr)
    {
        if (!N.GetWindowRect(root, out wr)) return false;
        double scale = Math.Max(1, N.GetDpiForWindow(root)) / 96.0;
        int band = (int)(64 * scale);
        int top = wr.Top;
        if (N.IsZoomed(root)) top += Math.Max(0, (int)(8 * scale)); // maximized windows hang over the monitor edge
        return pt.Y >= wr.Top && pt.Y <= top + band && pt.X >= wr.Left && pt.X < wr.Right;
    }

    public static int NcHitTest(IntPtr hwnd, POINT screenPt)
    {
        var p = screenPt;
        // DPI-unaware / system-aware windows expect logical coordinates.
        N.PhysicalToLogicalPointForPerMonitorDPI(hwnd, ref p);
        if (N.SendMessageTimeout(hwnd, N.WM_NCHITTEST, IntPtr.Zero, N.MakeLParam(p.X, p.Y),
                N.SMTO_ABORTIFHUNG | N.SMTO_BLOCK, 100, out var res) == IntPtr.Zero)
            return N.HTERROR;
        return unchecked((int)(long)res);
    }

    private static CaptionButton FromHit(int ht) => ht switch
    {
        N.HTCLOSE => CaptionButton.Close,
        N.HTMINBUTTON => CaptionButton.Minimize,
        N.HTMAXBUTTON => CaptionButton.Maximize,
        _ => CaptionButton.None,
    };

    /// <summary>Hit-test only (no UIA). Safe to call from the hook thread.</summary>
    public CaptionButton Classify(IntPtr root, POINT pt, out string how)
    {
        how = "none";
        if (!InCaptionBand(root, pt, out var wr)) { how = "outside-caption-band"; return CaptionButton.None; }

        int ht = NcHitTest(root, pt);
        var kind = FromHit(ht);
        if (kind != CaptionButton.None) { how = $"nchittest(root)={ht}"; return kind; }

        var child = N.WindowFromPoint(pt);
        if (child != IntPtr.Zero && child != root)
        {
            int ht2 = NcHitTest(child, pt);
            kind = FromHit(ht2);
            if (kind != CaptionButton.None) { how = $"nchittest(child)={ht2}"; return kind; }
        }

        var c = _cache;
        if (c != null && c.Hwnd == root && c.WindowRect.SameAs(wr) && c.ButtonRect.Contains(pt) && Environment.TickCount64 - c.At < 60_000)
        {
            how = "uia-cache";
            return c.Kind;
        }
        how = $"nchittest={ht}";
        return CaptionButton.None;
    }

    /// <summary>Called from the hook thread on every mouse move; must be cheap.</summary>
    public void NotifyHover(POINT pt)
    {
        if (!UiaEnabled) return;
        Interlocked.Exchange(ref _hoverPacked, ((long)pt.X << 32) | (uint)pt.Y);
        _hoverSignal.Set();
    }

    private void WorkerLoop()
    {
        CUIAutomation8? uia = null;
        while (!_stop)
        {
            _hoverSignal.WaitOne();
            if (_stop) break;
            Thread.Sleep(50); // debounce: only probe where the mouse settles
            long packed = Interlocked.Read(ref _hoverPacked);
            var pt = new POINT((int)(packed >> 32), (int)(packed & 0xFFFFFFFF));
            try
            {
                uia ??= new CUIAutomation8();
                ProbeUia(uia, pt);
            }
            catch (Exception ex) { Log.Warn($"UIA probe failed: {ex.Message}"); }
        }
    }

    private void ProbeUia(IUIAutomation uia, POINT pt)
    {
        var root = N.RootAt(pt);
        if (root == IntPtr.Zero || WindowUtil.IsProtected(root)) return;
        if (IsRelevant != null && !IsRelevant(root)) return;
        if (!InCaptionBand(root, pt, out var wr)) return;

        var c = _cache;
        if (c != null && c.Hwnd == root && c.WindowRect.SameAs(wr) && c.ButtonRect.Contains(pt)) return; // already known

        // Only fall back to UIA if the window does not answer NCHITTEST with caption buttons.
        int ht = NcHitTest(root, pt);
        if (FromHit(ht) != CaptionButton.None || ht == N.HTERROR) return;

        // Throttle per window/location.
        long now = Environment.TickCount64;
        if (_lastUiaHwnd == root && Math.Abs(_lastUiaPt.X - pt.X) < 6 && Math.Abs(_lastUiaPt.Y - pt.Y) < 6 && now - _lastUiaAt < 1000) return;
        _lastUiaHwnd = root; _lastUiaPt = pt; _lastUiaAt = now;

        var found = FindButtonAt(uia, pt, wr, out var rect, out var name);
        if (found != CaptionButton.None)
        {
            _cache = new UiaHit(root, found, rect, wr, now);
            Log.Info($"UIA: '{name}' {found} button of hwnd={root} at {rect}");
        }
    }

    /// <summary>UIA lookup of a caption-like button at a point (also used by the probe command).</summary>
    public static CaptionButton FindButtonAt(IUIAutomation uia, POINT pt, RECT windowRect, out RECT rect, out string name)
    {
        rect = default; name = "";
        var el = uia.ElementFromPoint(new tagPOINT { x = pt.X, y = pt.Y });
        var walker = uia.ControlViewWalker;
        for (int depth = 0; el != null && depth < 4; depth++)
        {
            if (el.CurrentControlType == UIA_ButtonControlTypeId)
            {
                name = (el.CurrentName ?? "").Trim();
                var kind = CloseName.IsMatch(name) ? CaptionButton.Close : MinName.IsMatch(name) ? CaptionButton.Minimize : CaptionButton.None;
                if (kind != CaptionButton.None)
                {
                    var r = el.CurrentBoundingRectangle;
                    rect = new RECT { Left = r.left, Top = r.top, Right = r.right, Bottom = r.bottom };
                    // Sanity: caption buttons are small and sit in the right half of the title band.
                    bool plausible = rect.Width > 0 && rect.Width < 200 && rect.Height < 150 && rect.Left > windowRect.Left + windowRect.Width / 2;
                    return plausible ? kind : CaptionButton.None;
                }
                return CaptionButton.None;
            }
            el = walker.GetParentElement(el);
        }
        return CaptionButton.None;
    }

    public string DescribeCache()
    {
        var c = _cache;
        return c == null ? "none" : $"hwnd={c.Hwnd} kind={c.Kind} rect={c.ButtonRect} ageMs={Environment.TickCount64 - c.At}";
    }

    public void Dispose()
    {
        _stop = true;
        _hoverSignal.Set();
    }
}
