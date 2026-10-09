using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Trayify.Native;

namespace Trayify.UI;

/// <summary>HICON -> WinUI ImageSource (premultiplied BGRA WriteableBitmap). No System.Drawing needed.</summary>
public static class IconImage
{
    public static ImageSource? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero || !N.GetIconInfo(hIcon, out var ii)) return null;
        try
        {
            if (ii.hbmColor == IntPtr.Zero) return null;
            N.GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bm);
            int w = bm.bmWidth, h = bm.bmHeight;
            if (w <= 0 || h <= 0 || w > 512 || h > 512) return null;

            var color = ReadBits(ii.hbmColor, w, h);
            if (color == null) return null;
            bool hasAlpha = false;
            for (int i = 3; i < color.Length; i += 4) if (color[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha)
            {
                var mask = ii.hbmMask != IntPtr.Zero ? ReadBits(ii.hbmMask, w, h) : null;
                for (int i = 0; i < color.Length; i += 4)
                    color[i + 3] = (byte)(mask != null && mask[i] != 0 ? 0 : 255);
            }
            for (int i = 0; i < color.Length; i += 4)
            {
                int a = color[i + 3];
                if (a == 255) continue;
                color[i] = (byte)(color[i] * a / 255);
                color[i + 1] = (byte)(color[i + 1] * a / 255);
                color[i + 2] = (byte)(color[i + 2] * a / 255);
            }
            var wb = new WriteableBitmap(w, h);
            using (var s = wb.PixelBuffer.AsStream()) s.Write(color, 0, color.Length);
            wb.Invalidate();
            return wb;
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) N.DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) N.DeleteObject(ii.hbmMask);
        }
    }

    private static byte[]? ReadBits(IntPtr hbm, int w, int h)
    {
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0,
            },
            bmiColors = new uint[256],
        };
        var buf = new byte[w * h * 4];
        var dc = N.GetDC(IntPtr.Zero);
        try { return N.GetDIBits(dc, hbm, 0, (uint)h, buf, ref bmi, 0) == h ? buf : null; }
        finally { N.ReleaseDC(IntPtr.Zero, dc); }
    }

    public static ImageSource? ForWindow(IntPtr hwnd, string? exePath, int size)
    {
        var icon = Core.WindowUtil.GetIcon(hwnd, exePath, size);
        try { return FromHIcon(icon); }
        finally { if (icon != IntPtr.Zero) N.DestroyIcon(icon); }
    }
}
