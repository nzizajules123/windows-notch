using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace WindowsNotch.Services;

public sealed class AppWindowItem
{
    public IntPtr Hwnd { get; init; }
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public Visibility InitialVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Lists real, user-facing top-level windows (VS Code, VLC, Terminal, Chrome…) and controls them.</summary>
public sealed class AppsService
{
    public ObservableCollection<AppWindowItem> Items { get; } = new();

    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
        { "Electron", "Application Frame Host", "App", "javaw", "python" };
    private static readonly HashSet<string> SkipClasses = new(StringComparer.Ordinal)
        { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow" };

    /// <returns>true when the list changed.</returns>
    public bool Refresh()
    {
        var self = (uint)Environment.ProcessId;
        var found = new List<AppWindowItem>();

        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (!IsAppWindow(h)) return true;
                Native.GetWindowThreadProcessId(h, out var pid);
                if (pid == self) return true;

                var title = Title(h);
                var exe = ExePath(pid) ?? "";
                var isFrameHost = exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
                var name = isFrameHost ? title : FriendlyName(exe);
                var icon = isFrameHost ? WindowIcon(h) : (ExeIcon(exe) ?? WindowIcon(h));
                found.Add(new AppWindowItem { Hwnd = h, Title = title, Name = Shorten(name), Icon = icon });
            }
            catch { /* skip windows that vanish mid-enumeration */ }
            return true;
        }, IntPtr.Zero);

        // Several windows of one app (or a generic Electron shell) → label them by window title instead.
        var dupes = found.GroupBy(f => f.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        for (int i = 0; i < found.Count; i++)
        {
            var f = found[i];
            if (!dupes.Contains(f.Name) && !GenericNames.Contains(f.Name)) continue;
            var t = f.Title.Split(new[] { " - ", " — ", " | " }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(t)) found[i] = new AppWindowItem { Hwnd = f.Hwnd, Title = f.Title, Name = Shorten(t), Icon = f.Icon };
        }

        // Only rebuild when something actually changed (avoids hover flicker).
        if (found.Count == Items.Count && found.Zip(Items).All(p => p.First.Hwnd == p.Second.Hwnd && p.First.Title == p.Second.Title))
            return false;
        Items.Clear();
        foreach (var f in found) Items.Add(f);
        return true;
    }

    private static bool IsAppWindow(IntPtr h)
    {
        if (!Native.IsWindowVisible(h) || Native.GetWindowTextLength(h) == 0) return false;
        if (Native.GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return false;
        var ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return false;
        if (Native.DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var cls = new StringBuilder(256);
        Native.GetClassName(h, cls, cls.Capacity);
        return !SkipClasses.Contains(cls.ToString());
    }

    private static string Title(IntPtr h)
    {
        var sb = new StringBuilder(Native.GetWindowTextLength(h) + 1);
        Native.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? ExePath(uint pid)
    {
        var hp = Native.OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return Native.QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(hp); }
    }

    private string FriendlyName(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return "App";
        if (_nameCache.TryGetValue(exe, out var n)) return n;
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(exe).FileDescription;
            n = string.IsNullOrWhiteSpace(desc) ? Path.GetFileNameWithoutExtension(exe) : desc!;
        }
        catch { n = Path.GetFileNameWithoutExtension(exe); }
        n = n switch
        {
            "Google Chrome" => "Chrome",
            "VLC media player" => "VLC",
            "Visual Studio Code" => "VS Code",
            "Microsoft Edge" => "Edge",
            _ => n,
        };
        return _nameCache[exe] = n;
    }

    private static string Shorten(string s) => s.Length > 22 ? s[..21] + "…" : s;

    private ImageSource? ExeIcon(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        if (_iconCache.TryGetValue(exe, out var cached)) return cached;
        var large = new IntPtr[1];
        ImageSource? icon = null;
        if (Native.ExtractIconEx(exe, 0, large, null, 1) > 0) icon = Native.IconToBitmap(large[0], true);
        return _iconCache[exe] = icon;
    }

    private static ImageSource? WindowIcon(IntPtr h)
    {
        Native.SendMessageTimeout(h, Native.WM_GETICON, new IntPtr(1 /*ICON_BIG*/), IntPtr.Zero, 0x2, 100, out var hIcon);
        if (hIcon == IntPtr.Zero) hIcon = Native.GetClassLongPtr(h, -14 /*GCLP_HICON*/);
        return Native.IconToBitmap(hIcon, false);
    }

    public static void Focus(IntPtr h)
    {
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(h) && Native.GetForegroundWindow() == h) return;
        // Fallback: a synthetic Alt tap satisfies Windows' foreground-lock rules.
        Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
        Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
        Native.SetForegroundWindow(h);
    }

    public static void Minimize(IntPtr h) => Native.ShowWindow(h, Native.SW_MINIMIZE);

    /// <summary>A window's title (shortened), for the AI to say which window it acted on.</summary>
    public static string ShortTitle(IntPtr h)
    {
        var len = Native.GetWindowTextLength(h);
        if (len <= 0) return "the window";
        var sb = new System.Text.StringBuilder(len + 1);
        Native.GetWindowText(h, sb, sb.Capacity);
        var t = sb.ToString();
        return t.Length > 60 ? t[..58] + "…" : t;
    }
    public static void Close(IntPtr h) => Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
}
