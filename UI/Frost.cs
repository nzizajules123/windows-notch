using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowsNotch.UI;

/// <summary>
/// "Frosted glass" without DWM: grabs what's on screen behind a spot (a still image, taken just before a card
/// appears) so WPF can blur it heavily under the card — the look of macOS vibrancy, on any Windows build.
/// </summary>
public static class Frost
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    private const int SRCCOPY = 0x00CC0020;

    /// <summary>Screen pixels in a rectangle (device pixels, virtual-screen coordinates), or null.</summary>
    public static BitmapSource? Capture(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        IntPtr screen = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screen = GetDC(IntPtr.Zero);
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, w, h);
            old = SelectObject(mem, bmp);
            // no CAPTUREBLT: layered windows (the notch itself) are left out of the picture
            BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY);
            SelectObject(mem, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Captures what's behind <paramref name="el"/>'s future screen area (DIP rect relative to <paramref name="window"/>).</summary>
    public static BitmapSource? Behind(Window window, Rect dipRect, double pad = 40)
    {
        var src = PresentationSource.FromVisual(window);
        if (src?.CompositionTarget == null) return null;
        var m = src.CompositionTarget.TransformToDevice;
        var origin = window.PointToScreen(new Point(0, 0));                // device pixels
        var x = origin.X + (dipRect.X - pad) * m.M11;
        var y = origin.Y + (dipRect.Y - pad) * m.M22;
        var w = (dipRect.Width + 2 * pad) * m.M11;
        var h = (dipRect.Height + 2 * pad) * m.M22;
        return Capture((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h));
    }

    /// <summary>
    /// True when a capture came back all black — what BitBlt returns on some PCs (remote desktop, some GPU drivers,
    /// protected video). Blurring that would just make the notch black, so callers skip it.
    /// </summary>
    public static bool LooksBlank(BitmapSource? src)
    {
        if (src == null) return true;
        try
        {
            var conv = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Bgr32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            if (w <= 0 || h <= 0) return true;
            var row = new byte[w * 4];
            int max = 0;
            for (int i = 1; i <= 5; i++)
            {
                conv.CopyPixels(new Int32Rect(0, h * i / 6, w, 1), row, row.Length, 0);
                for (int x = 0; x < row.Length; x += 4)
                    max = Math.Max(max, Math.Max(row[x], Math.Max(row[x + 1], row[x + 2])));
                if (max > 6) return false;
            }
            return true;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ idle time (for the standby clock)
    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    public static TimeSpan IdleTime()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - li.dwTime));
    }
}
