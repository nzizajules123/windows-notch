using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WindowsNotch.Services;

/// <summary>
/// What the AI can do to the PC itself: sound, brightness, Wi-Fi / Bluetooth, dark mode, Settings pages, finding files,
/// arranging windows, typing and shortcuts, the clipboard, screenshots, PC health, apps using the most memory.
/// Everything here is local and immediate; anything destructive is guarded by the caller (countdowns, confirmation).
/// </summary>
public static class PcControl
{
    // ------------------------------------------------------------------ volume (Core Audio)
    private static IAudioEndpointVolume? Endpoint()
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.GetDefaultAudioEndpoint(0, 1, out var dev) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            dev.Activate(ref iid, 23, IntPtr.Zero, out var o);
            return (IAudioEndpointVolume)o;
        }
        catch { return null; }
    }

    /// <summary>(level 0–100, muted), or null without a sound device.</summary>
    public static (int level, bool muted)? GetVolume()
    {
        var v = Endpoint();
        if (v == null || v.GetMasterVolumeLevelScalar(out var l) != 0 || v.GetMute(out var m) != 0) return null;
        return ((int)Math.Round(l * 100), m);
    }

    public static bool SetVolume(int percent)
    {
        var v = Endpoint();
        if (v == null) return false;
        var g = Guid.Empty;
        v.SetMute(false, ref g);
        return v.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref g) == 0;
    }

    public static bool SetMute(bool mute)
    {
        var v = Endpoint();
        var g = Guid.Empty;
        return v != null && v.SetMute(mute, ref g) == 0;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // ------------------------------------------------------------------ brightness (laptop panels, WMI)
    public static int? GetBrightness()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    public static bool SetBrightness(int percent)
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (System.Management.ManagementObject o in s.Get())
            {
                o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)Math.Clamp(percent, 0, 100) });
                return true;
            }
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------------ Wi-Fi / Bluetooth radios
    /// <summary>Turns the Wi-Fi or Bluetooth radio on or off. Returns what happened, for the AI to say.</summary>
    public static async Task<string> SetRadioAsync(string kind, bool on)
    {
        try
        {
            var access = await Windows.Devices.Radios.Radio.RequestAccessAsync();
            if (access != Windows.Devices.Radios.RadioAccessStatus.Allowed)
                return $"Windows didn't allow changing the {kind} switch ({access}). I opened its settings instead.";
            var want = kind == "bluetooth" ? Windows.Devices.Radios.RadioKind.Bluetooth : Windows.Devices.Radios.RadioKind.WiFi;
            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            var r = radios.FirstOrDefault(x => x.Kind == want);
            if (r == null) return $"This PC has no {kind} adapter.";
            var res = await r.SetStateAsync(on ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off);
            return res == Windows.Devices.Radios.RadioAccessStatus.Allowed ? $"{(kind == "wifi" ? "Wi-Fi" : "Bluetooth")} is {(on ? "on" : "off")}."
                : $"Windows didn't allow it ({res}).";
        }
        catch (Exception ex) { return "Couldn't change it: " + ex.Message; }
    }

    // ------------------------------------------------------------------ dark / light mode
    public static string SetTheme(bool dark)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "ImmersiveColorSet", 2, 1000, out _);
            return $"Switched Windows to {(dark ? "dark" : "light")} mode.";
        }
        catch (Exception ex) { return "Couldn't change the theme: " + ex.Message; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    // ------------------------------------------------------------------ Settings pages
    private static readonly Dictionary<string, string> SettingsPages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wifi"] = "ms-settings:network-wifi", ["wi-fi"] = "ms-settings:network-wifi", ["network"] = "ms-settings:network-status",
        ["internet"] = "ms-settings:network-status", ["bluetooth"] = "ms-settings:bluetooth", ["devices"] = "ms-settings:bluetooth",
        ["display"] = "ms-settings:display", ["screen"] = "ms-settings:display", ["brightness"] = "ms-settings:display",
        ["night light"] = "ms-settings:nightlight", ["sound"] = "ms-settings:sound", ["audio"] = "ms-settings:sound",
        ["notifications"] = "ms-settings:notifications", ["focus"] = "ms-settings:focus", ["do not disturb"] = "ms-settings:notifications",
        ["power"] = "ms-settings:powersleep", ["battery"] = "ms-settings:batterysaver", ["storage"] = "ms-settings:storagesense",
        ["apps"] = "ms-settings:appsfeatures", ["installed apps"] = "ms-settings:appsfeatures", ["uninstall"] = "ms-settings:appsfeatures",
        ["default apps"] = "ms-settings:defaultapps", ["startup"] = "ms-settings:startupapps", ["startup apps"] = "ms-settings:startupapps",
        ["personalization"] = "ms-settings:personalization", ["background"] = "ms-settings:personalization-background",
        ["wallpaper"] = "ms-settings:personalization-background", ["colors"] = "ms-settings:personalization-colors",
        ["themes"] = "ms-settings:themes", ["lock screen"] = "ms-settings:lockscreen", ["taskbar"] = "ms-settings:taskbar",
        ["accounts"] = "ms-settings:yourinfo", ["sign-in"] = "ms-settings:signinoptions", ["password"] = "ms-settings:signinoptions",
        ["time"] = "ms-settings:dateandtime", ["date"] = "ms-settings:dateandtime", ["language"] = "ms-settings:regionlanguage",
        ["keyboard"] = "ms-settings:typing", ["mouse"] = "ms-settings:mousetouchpad", ["touchpad"] = "ms-settings:devices-touchpad",
        ["printers"] = "ms-settings:printers", ["camera"] = "ms-settings:privacy-webcam", ["microphone"] = "ms-settings:privacy-microphone",
        ["privacy"] = "ms-settings:privacy", ["windows update"] = "ms-settings:windowsupdate", ["update"] = "ms-settings:windowsupdate",
        ["updates"] = "ms-settings:windowsupdate", ["about"] = "ms-settings:about", ["system"] = "ms-settings:about",
        ["vpn"] = "ms-settings:network-vpn", ["hotspot"] = "ms-settings:network-mobilehotspot", ["airplane mode"] = "ms-settings:network-airplanemode",
        ["accessibility"] = "ms-settings:easeofaccess", ["gaming"] = "ms-settings:gaming-gamebar", ["clipboard"] = "ms-settings:clipboard",
        ["multitasking"] = "ms-settings:multitasking", ["security"] = "windowsdefender:", ["windows security"] = "windowsdefender:",
        ["task manager"] = "taskmgr", ["control panel"] = "control", ["device manager"] = "devmgmt.msc", ["disk management"] = "diskmgmt.msc",
    };

    public static string OpenSettings(string page)
    {
        var p = page.Trim().ToLowerInvariant().Replace(" settings", "").Replace("settings", "").Trim();
        var target = p.Length == 0 ? "ms-settings:" : SettingsPages.TryGetValue(p, out var t) ? t
            : SettingsPages.FirstOrDefault(kv => p.Contains(kv.Key) || kv.Key.Contains(p)).Value;
        if (target == null) { Shell("ms-settings:"); return $"I don't know a settings page for \"{page}\"; opened Settings so you can search it there."; }
        Shell(target);
        return $"Opened {(p.Length == 0 ? "Settings" : page)}.";
    }

    public static void Shell(string target, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); } catch { }
    }

    // ------------------------------------------------------------------ finding files
    public sealed record Found(string Path, DateTime Modified, long Size);

    private static readonly string[] SkipDirs = { "node_modules", ".git", "AppData", "$Recycle.Bin", "Windows", "Program Files", "Program Files (x86)",
        "ProgramData", "obj", "bin", ".vs", ".gradle", "__pycache__", "venv", ".venv", "site-packages", "System Volume Information" };

    private static readonly Dictionary<string, string[]> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["document"] = new[] { ".docx", ".doc", ".pdf", ".txt", ".odt", ".rtf", ".md" }, ["pdf"] = new[] { ".pdf" },
        ["word"] = new[] { ".docx", ".doc" }, ["excel"] = new[] { ".xlsx", ".xls", ".csv" }, ["spreadsheet"] = new[] { ".xlsx", ".xls", ".csv", ".ods" },
        ["powerpoint"] = new[] { ".pptx", ".ppt" }, ["presentation"] = new[] { ".pptx", ".ppt", ".odp" },
        ["image"] = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".bmp" }, ["photo"] = new[] { ".jpg", ".jpeg", ".png", ".heic", ".webp" },
        ["video"] = new[] { ".mp4", ".mkv", ".mov", ".avi", ".webm" }, ["music"] = new[] { ".mp3", ".m4a", ".wav", ".flac", ".ogg" },
        ["code"] = new[] { ".py", ".js", ".ts", ".cs", ".java", ".kt", ".cpp", ".c", ".html", ".css", ".php" },
        ["zip"] = new[] { ".zip", ".rar", ".7z" }, ["installer"] = new[] { ".exe", ".msi" },
    };

    /// <summary>Searches the user's folders (and other drives) for files whose names match the words; newest first. Folders too.</summary>
    public static List<Found> FindFiles(string query, string kind = "", int max = 8, int budgetMs = 5000)
    {
        var words = query.ToLowerInvariant().Split(new[] { ' ', '_', '-', '.', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && w is not ("my" or "the" or "file" or "files" or "folder" or "document" or "a" or "an" or "of")).ToArray();
        Kinds.TryGetValue(kind.Trim().TrimEnd('s'), out var exts);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<(string path, int depth)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Desktop), 6), (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 7),
            (Path.Combine(home, "Downloads"), 5), (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), 5),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), 4), (Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), 4),
            (Path.Combine(home, "OneDrive"), 6), (home, 2),
        };
        foreach (var d in DriveInfo.GetDrives())
            if (d.DriveType == DriveType.Fixed && d.IsReady && !d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase)) roots.Add((d.RootDirectory.FullName, 5));

        var sw = Stopwatch.StartNew();
        var hits = new Dictionary<string, (Found f, int score)>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Consider(string path, bool isDir)
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            if (exts != null && (isDir || !exts.Contains(Path.GetExtension(name)))) return;
            int score = 0;
            foreach (var w in words) if (name.Contains(w)) score += w.Length >= 4 ? 3 : 2;
            if (words.Length > 0 && score == 0) return;
            if (words.Length > 0 && words.All(w => name.Contains(w))) score += 5;
            try
            {
                var fi = isDir ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                hits[path] = (new Found(path, fi.LastWriteTime, isDir ? -1 : ((FileInfo)fi).Length), score);
            }
            catch { }
        }
        foreach (var (root, depth) in roots)
        {
            if (!Directory.Exists(root) || !seen.Add(root)) continue;
            var stack = new Stack<(string, int)>();
            stack.Push((root, 0));
            while (stack.Count > 0 && sw.ElapsedMilliseconds < budgetMs)
            {
                var (dir, level) = stack.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir)) Consider(f, false);
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var n = Path.GetFileName(sub);
                        if (n.StartsWith('.') || SkipDirs.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
                        if (words.Length > 0) Consider(sub, true);
                        if (level + 1 < depth) stack.Push((sub, level + 1));
                    }
                }
                catch { }
            }
        }
        return hits.Values.OrderByDescending(h => h.score).ThenByDescending(h => h.f.Modified).Take(max).Select(h => h.f).ToList();
    }

    /// <summary>Files the user opened recently (Windows' Recent list), newest first.</summary>
    public static List<Found> RecentFiles(int max = 10)
    {
        var res = new List<Found>();
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic? shell = shellType != null ? Activator.CreateInstance(shellType) : null;
            foreach (var lnk in new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(60))
            {
                string target;
                try { target = shell?.CreateShortcut(lnk.FullName).TargetPath ?? ""; } catch { continue; }
                if (target.Length == 0 || !File.Exists(target)) continue;
                res.Add(new Found(target, lnk.LastWriteTime, new FileInfo(target).Length));
                if (res.Count >= max) break;
            }
        }
        catch { }
        return res;
    }

    // ------------------------------------------------------------------ windows
    public static void ShowDesktop() => Keys("win+d");
    public static void TaskView() => Keys("win+tab");

    public static void Snap(IntPtr h, bool left)
    {
        Native.ShowWindow(h, 9 /*SW_RESTORE*/);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(h, 2), ref mi)) return;
        var wa = mi.rcWork;
        var half = (wa.Right - wa.Left) / 2;
        Native.SetWindowPos(h, IntPtr.Zero, left ? wa.Left : wa.Left + half, wa.Top, half, wa.Bottom - wa.Top, 0x0040 /*SHOWWINDOW*/ | 0x0004 /*NOZORDER*/);
        Native.SetForegroundWindow(h);
    }

    // ------------------------------------------------------------------ typing & shortcuts
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>Types text into whatever app is in front, as if typed on the keyboard (any language, emoji too).</summary>
    public static void TypeText(string text)
    {
        var list = new List<INPUT>();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { list.Add(Key(0x0D, false)); list.Add(Key(0x0D, true)); continue; }
            list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 /*UNICODE*/ } } });
            list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 | 0x0002 /*KEYUP*/ } } });
        }
        for (int i = 0; i < list.Count; i += 200)   // in chunks, so long texts don't overflow the input queue
        {
            var chunk = list.Skip(i).Take(200).ToArray();
            SendInput((uint)chunk.Length, chunk, Marshal.SizeOf<INPUT>());
            Thread.Sleep(15);
        }
    }

    private static INPUT Key(ushort vk, bool up) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 0x0002u : 0 } } };

    private static readonly Dictionary<string, ushort> Vk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["windows"] = 0x5B, ["enter"] = 0x0D,
        ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E,
        ["del"] = 0x2E, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["printscreen"] = 0x2C, ["insert"] = 0x2D, ["capslock"] = 0x14,
        ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["mute"] = 0xAD, ["playpause"] = 0xB3,
    };

    /// <summary>Presses a shortcut like "ctrl+s", "alt+tab", "win+shift+s", "f5". False if a key isn't known.</summary>
    public static bool Keys(string combo)
    {
        var codes = new List<ushort>();
        foreach (var part in combo.ToLowerInvariant().Replace(" ", "").Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Vk.TryGetValue(part, out var v)) codes.Add(v);
            else if (part.Length == 1 && char.IsLetterOrDigit(part[0])) codes.Add(char.ToUpperInvariant(part[0]));
            else if (part.Length is 2 or 3 && part[0] == 'f' && int.TryParse(part[1..], out var fn) && fn is >= 1 and <= 12) codes.Add((ushort)(0x70 + fn - 1));
            else return false;
        }
        if (codes.Count == 0) return false;
        var seq = codes.Select(c => Key(c, false)).Concat(Enumerable.Reverse(codes).Select(c => Key(c, true))).ToArray();
        SendInput((uint)seq.Length, seq, Marshal.SizeOf<INPUT>());
        return true;
    }

    // ------------------------------------------------------------------ screenshots
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    /// <summary>The whole screen (all monitors) as an image.</summary>
    public static BitmapSource CaptureScreen()
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000 /*SRCCOPY | CAPTUREBLT*/);
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Saves a screenshot in Pictures\Screenshots and returns its path.</summary>
    public static string SaveScreenshot()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(CaptureScreen()));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    /// <summary>The screen as a JPEG small enough to send to Gemini (about 1280 px wide).</summary>
    public static byte[] ScreenJpeg(int maxWidth = 1280)
    {
        BitmapSource img = CaptureScreen();
        if (img.PixelWidth > maxWidth)
        {
            var s = (double)maxWidth / img.PixelWidth;
            img = new TransformedBitmap(img, new System.Windows.Media.ScaleTransform(s, s));
        }
        var enc = new JpegBitmapEncoder { QualityLevel = 80 };
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ PC health
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public static int CpuPercent()
    {
        if (!GetSystemTimes(out var i1, out var k1, out var u1)) return -1;
        Thread.Sleep(400);
        if (!GetSystemTimes(out var i2, out var k2, out var u2)) return -1;
        var total = (k2 - k1) + (u2 - u1);
        return total <= 0 ? 0 : (int)Math.Round(100.0 * (total - (i2 - i1)) / total);
    }

    public static string Health()
    {
        var sb = new StringBuilder();
        var cpu = CpuPercent();
        if (cpu >= 0) sb.Append($"CPU {cpu}% busy. ");
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m))
            sb.Append($"Memory {m.dwMemoryLoad}% used ({(m.ullTotalPhys - m.ullAvailPhys) / 1e9:0.0} of {m.ullTotalPhys / 1e9:0.0} GB). ");
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            sb.Append($"Drive {d.Name.TrimEnd('\\')} {d.AvailableFreeSpace / 1e9:0} GB free of {d.TotalSize / 1e9:0} GB. ");
        var (pct, charging, has) = PowerService.Battery();
        if (has) sb.Append($"Battery {pct}%{(charging ? ", charging" : "")}. ");
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        sb.Append($"On for {(int)up.TotalDays} d {up.Hours} h {up.Minutes} min. ");
        var wifi = WifiName();
        sb.Append(System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() ? $"Online{(wifi != null ? $" (Wi-Fi: {wifi})" : "")}. " : "Offline. ");
        sb.Append($"Windows {Environment.OSVersion.Version.Build switch { >= 22000 => "11", _ => "10" }} (build {Environment.OSVersion.Version.Build}).");
        return sb.ToString();
    }

    public static string? WifiName()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            var o = p!.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            var line = o.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !l.Contains("BSSID"));
            return line?.Split(':', 2)[1].Trim() is { Length: > 0 } s ? s : null;
        }
        catch { return null; }
    }

    /// <summary>The apps using the most memory: (name, MB, how many processes).</summary>
    public static List<(string name, long mb, int count)> TopApps(int max = 6)
    {
        var rows = new List<(string, long, int)>();
        foreach (var g in Process.GetProcesses().GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            long ws = 0;
            foreach (var p in g) { try { ws += p.WorkingSet64; } catch { } finally { p.Dispose(); } }
            if (g.Key is "Idle" or "System" or "Registry" or "Memory Compression" or "vmmem" or "MsMpEng") continue;
            rows.Add((g.Key, ws / 1_048_576, g.Count()));
        }
        return rows.OrderByDescending(r => r.Item2).Take(max).ToList();
    }

    private static readonly string[] NeverKill = { "explorer", "csrss", "wininit", "winlogon", "services", "lsass", "smss", "svchost", "dwm",
        "System", "Registry", "fontdrvhost", "sihost", "ctfmon", "audiodg", "WindowsNotch", "MsMpEng", "SecurityHealthService" };

    /// <summary>Force-closes an app's processes (for a frozen app). Returns the names closed. Never touches Windows itself.</summary>
    public static List<string> ForceClose(string spoken)
    {
        var want = spoken.Trim().ToLowerInvariant().Replace(" ", "");
        want = want switch { "vscode" or "visualstudiocode" => "code", "googlechrome" => "chrome", "word" => "winword", "excel" => "excel", "edge" => "msedge", _ => want };
        var closed = new List<string>();
        if (want.Length < 2) return closed;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var n = p.ProcessName;
                if (NeverKill.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
                var title = p.MainWindowTitle.ToLowerInvariant().Replace(" ", "");
                if (n.ToLowerInvariant().Replace(" ", "") == want || n.ToLowerInvariant().StartsWith(want) || (want.Length >= 4 && title.Contains(want)))
                {
                    p.Kill(entireProcessTree: true);
                    if (!closed.Contains(n)) closed.Add(n);
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return closed;
    }

    // ------------------------------------------------------------------ recycle bin
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr h, string? root, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);

    public static (long items, long bytes) RecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64NumItems, info.i64Size) : (-1, -1);
    }

    public static bool EmptyRecycleBin() => SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4 /*no confirm, progress, sound*/) == 0;
}
