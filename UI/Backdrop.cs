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

    /// <summary>Accent blur on any window (state 3 = plain blur, 4 = acrylic).</summary>
    internal static void SetAccent(IntPtr hwnd, int state, uint abgrTint)
    {
        var policy = new AccentPolicy { State = state, Flags = state == 0 ? 0 : 2, Tint = abgrTint };
        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttribData { Attribute = 19, Data = ptr, Size = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    internal static IntPtr ShapeRegion(int x, int y, int w, int h, double radius, int ear)
    {
        int L = x, T = y, R = x + w, B = y + h;
        int rr = Math.Max(0, (int)Math.Round(radius * 2));
        var rgn = CreateRectRgn(L, T, R, Math.Max(T, B - rr / 2));
        var round = CreateRoundRectRgn(L, T, R + 1, B + 1, rr, rr);
        CombineRgn(rgn, rgn, round, RGN_OR);
        DeleteObject(round);
        if (ear > 0)
        {
            foreach (var (ex, cx) in new[] { (L - ear, L - ear), (R, R + ear) })
            {
                var sq = CreateRectRgn(ex, T, ex + ear, T + ear);
                var hole = CreateEllipticRgn(cx - ear, T, cx + ear, T + 2 * ear);
                CombineRgn(sq, sq, hole, RGN_DIFF);
                CombineRgn(rgn, rgn, sq, RGN_OR);
                DeleteObject(sq);
                DeleteObject(hole);
            }
        }
        return rgn;
    }

    internal static void ApplyRegion(IntPtr hwnd, IntPtr rgn) => SetWindowRgn(hwnd, rgn, true);
}

/// <summary>
/// A separate, non-layered, click-through window that only shows the blurred desktop, sized and clipped to the
/// island and kept directly underneath the notch window. (Windows ignores clip regions for the blur of a
/// per-pixel-transparent window, which would frost the whole invisible window instead.)
/// </summary>
public sealed class BlurBacking : IDisposable
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins m);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int L, R, T, B; }

    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;
    private readonly System.Windows.Interop.HwndSource _src;
    private bool _visible;

    public IntPtr Handle => _src.Handle;

    public BlurBacking()
    {
        var p = new System.Windows.Interop.HwndSourceParameters("Notch blur")
        {
            WindowStyle = unchecked((int)0x80000000),                  // WS_POPUP
            ExtendedWindowStyle = 0x80 | 0x08000000 | 0x20 | 0x8,       // TOOLWINDOW | NOACTIVATE | TRANSPARENT (click-through) | TOPMOST
            PositionX = -32000, PositionY = -32000, Width = 10, Height = 10,
            UsesPerPixelOpacity = false,
        };
        _src = new System.Windows.Interop.HwndSource(p);
        _src.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
        DwmExtendFrameIntoClientArea(_src.Handle, ref m);
        Backdrop.SetAccent(_src.Handle, 3 /*blur behind*/, 0x01000000);
    }

    /// <summary>Places the blur exactly under the island (device pixels, screen coordinates), just below <paramref name="above"/>.</summary>
    public void Place(IntPtr above, int x, int y, int w, int h, double radius, int ear)
    {
        int bx = x - ear, bw = w + 2 * ear;
        if (bw <= 0 || h <= 0) return;
        var rgn = Backdrop.ShapeRegion(ear, 0, w, h, radius, ear);
        Backdrop.ApplyRegion(_src.Handle, rgn);
        SetWindowPos(_src.Handle, above, bx, y, bw, h, SWP_NOACTIVATE | SWP_NOOWNERZORDER | (_visible ? 0 : SWP_SHOWWINDOW));
        _visible = true;
    }

    public void Hide()
    {
        if (!_visible) return;
        ShowWindow(_src.Handle, 0);
        _visible = false;
    }

    public void Dispose() => _src.Dispose();

    [DllImport("user32.dll")] private static extern int GetWindowRgnBox(IntPtr hwnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    /// <summary>True when the window currently has a clip region no bigger than <paramref name="maxW"/> × <paramref name="maxH"/> px.</summary>
    public static bool HasShape(IntPtr hwnd, int maxW, int maxH)
    {
        var type = GetWindowRgnBox(hwnd, out var r);   // 0 error, 1 null region (=none), 2 simple, 3 complex
        return type >= 2 && r.R - r.L > 0 && r.R - r.L <= maxW && r.B - r.T <= maxH;
    }
}
