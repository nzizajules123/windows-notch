using System.Runtime.InteropServices;

namespace WindowsNotch.UI;

/// <summary>
/// Real frosted-glass: Windows' acrylic blur behind the window, clipped (SetWindowRgn) to the notch's
/// exact outline so only the island — never the transparent rest of the window — blurs the desktop.
/// </summary>
public static class Backdrop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State, Flags; public uint Tint; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData { public int Attribute; public IntPtr Data; public int Size; }

    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);

    private const int RGN_OR = 2, RGN_DIFF = 4;
    private const int ACCENT_DISABLED = 0, ACCENT_ENABLE_BLURBEHIND = 3, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    /// <summary>Turns the acrylic blur on/off. <paramref name="tint"/> is ARGB (very light: the WPF fill adds the rest).</summary>
    public static void SetBlur(IntPtr hwnd, bool on, uint argbTint = 0x10000000)
    {
        // Acrylic expects AABBGGRR
        uint a = argbTint >> 24, r = (argbTint >> 16) & 0xFF, g = (argbTint >> 8) & 0xFF, b = argbTint & 0xFF;
        var policy = new AccentPolicy
        {
            State = on ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_DISABLED,
            Flags = 0,
            Tint = (a << 24) | (b << 16) | (g << 8) | r,
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttribData { Attribute = 19 /*WCA_ACCENT_POLICY*/, Data = ptr, Size = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>
    /// Clips the window to the island: a rectangle with rounded bottom corners plus the two concave
    /// 12-px "ears" at the top. All values in device pixels relative to the window.
    /// </summary>
    public static void SetShape(IntPtr hwnd, double x, double y, double w, double h, double radius, double ear)
    {
        int L = (int)Math.Round(x), T = (int)Math.Round(y), R = (int)Math.Round(x + w), B = (int)Math.Round(y + h);
        int rr = Math.Max(0, (int)Math.Round(radius * 2));
        int e = (int)Math.Round(ear);

        var rgn = CreateRectRgn(L, T, R, Math.Max(T, B - rr / 2));
        var round = CreateRoundRectRgn(L, T, R + 1, B + 1, rr, rr);
        CombineRgn(rgn, rgn, round, RGN_OR);
        DeleteObject(round);

        if (e > 0)
        {
            foreach (var (ex, cx) in new[] { (L - e, L - e), (R, R + e) })
            {
                var sq = CreateRectRgn(ex, T, ex + e, T + e);
                var hole = CreateEllipticRgn(cx - e, T, cx + e, T + 2 * e);
                CombineRgn(sq, sq, hole, RGN_DIFF);
                CombineRgn(rgn, rgn, sq, RGN_OR);
                DeleteObject(sq);
                DeleteObject(hole);
            }
        }
        SetWindowRgn(hwnd, rgn, true);   // the system owns rgn now
    }

    public static void ClearShape(IntPtr hwnd) => SetWindowRgn(hwnd, IntPtr.Zero, true);
}
