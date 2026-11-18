using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;

namespace WindowsNotch.Services;

// =====================================================================================
//  Volume — Core Audio (IAudioEndpointVolume), polled cheaply on the UI thread
// =====================================================================================
public sealed class VolumeWatcher : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private IAudioEndpointVolume? _vol;
    private float _last = -1;
    private bool _lastMute;
    private int _refresh;

    /// <summary>(level 0..1, muted). Raised only when the user changes it.</summary>
    public event Action<float, bool>? Changed;

    public void Start()
    {
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        try
        {
            if (_vol == null || ++_refresh % 40 == 0) Acquire();  // follow default-device changes
            if (_vol == null) return;
            Marshal.ThrowExceptionForHR(_vol.GetMasterVolumeLevelScalar(out var level));
            Marshal.ThrowExceptionForHR(_vol.GetMute(out var mute));
            if (_last >= 0 && (Math.Abs(level - _last) > 0.004 || mute != _lastMute)) Changed?.Invoke(level, mute);
            _last = level;
            _lastMute = mute;
        }
        catch { _vol = null; }
    }

    private void Acquire()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        if (enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var device) != 0) { _vol = null; return; }
        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var o);
        _vol = (IAudioEndpointVolume)o;
    }

    public void Dispose() => _timer.Stop();

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
}

// =====================================================================================
//  Brightness — WMI WmiMonitorBrightness (laptop panels); polled on a background thread
// =====================================================================================
public sealed class BrightnessWatcher : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    public event Action<int>? Changed;   // 0..100

    public void Start() => Task.Run(async () =>
    {
        int last = -1, fails = 0;
        while (!_cts.IsCancelled())
        {
            try
            {
                using var s = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (var o in s.Get())
                {
                    var b = Convert.ToInt32(o["CurrentBrightness"]);
                    if (last >= 0 && b != last) Changed?.Invoke(b);
                    last = b;
                    break;
                }
                fails = 0;
            }
            catch
            {
                if (++fails > 5) return;   // no WMI brightness (desktop monitor) → give up quietly
            }
            await Task.Delay(300);
        }
    });

    public void Dispose() => _cts.Cancel();
}

internal static class CtsExt
{
    public static bool IsCancelled(this CancellationTokenSource c) => c.IsCancellationRequested;
}

// =====================================================================================
//  Devices — USB drives, phones, headsets, keyboards, mice… (WMI Win32_PnPEntity events)
// =====================================================================================
public sealed record DeviceEvent(bool Connected, string Name, string Kind, string Glyph);

public sealed class DeviceWatcher : IDisposable
{
    private System.Management.ManagementEventWatcher? _watcher;
    private readonly List<(bool add, string id, string name, string cls)> _batch = new();
    private readonly object _lock = new();
    private System.Threading.Timer? _flush;

    public event Action<DeviceEvent>? Changed;

    public void Start()
    {
        var q = new System.Management.WqlEventQuery(
            "SELECT * FROM __InstanceOperationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity'");
        _watcher = new System.Management.ManagementEventWatcher(q);
        _watcher.EventArrived += (_, e) =>
        {
            try
            {
                var kind = e.NewEvent.ClassPath.ClassName;
                if (kind != "__InstanceCreationEvent" && kind != "__InstanceDeletionEvent") return;
                var t = (System.Management.ManagementBaseObject)e.NewEvent["TargetInstance"];
                var id = t["DeviceID"]?.ToString() ?? "";
                var name = t["Name"]?.ToString() ?? "";
                var cls = t["PNPClass"]?.ToString() ?? "";
                lock (_lock)
                {
                    _batch.Add((kind == "__InstanceCreationEvent", id, name, cls));
                    _flush?.Dispose();
                    _flush = new System.Threading.Timer(_ => Flush(), null, 1300, Timeout.Infinite);  // one plug = many entities
                }
            }
            catch { }
        };
        _watcher.Start();
    }

    private static readonly (string cls, string kind, string glyph)[] Priority =
    {
        ("DiskDrive", "USB drive", ""), ("WPD", "Phone", ""), ("AudioEndpoint", "Audio", ""),
        ("Media", "Audio", ""), ("Camera", "Camera", ""), ("Image", "Camera", ""),
        ("Keyboard", "Keyboard", ""), ("Mouse", "Mouse", ""), ("Printer", "Printer", ""),
        ("Bluetooth", "Bluetooth", ""), ("Net", "Network", ""), ("HIDClass", "Accessory", ""),
        ("USB", "USB", ""),
    };

    private void Flush()
    {
        List<(bool add, string id, string name, string cls)> items;
        lock (_lock) { items = _batch.ToList(); _batch.Clear(); }
        foreach (var add in new[] { true, false })
        {
            var group = items.Where(i => i.add == add).ToList();
            // only real plug-in hardware (not software devices that come and go)
            if (!group.Any(i => i.id.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || i.id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)
                                || i.id.StartsWith("SWD\\WPDBUSENUM", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var (cls, kind, glyph) in Priority)
            {
                var hit = group.FirstOrDefault(i => string.Equals(i.cls, cls, StringComparison.OrdinalIgnoreCase) && !Generic(i.name));
                if (hit.name == null) continue;
                App.Log($"device {(add ? "connected" : "removed")}: {hit.name} ({hit.cls})");
                Changed?.Invoke(new DeviceEvent(add, Clean(hit.name), kind, glyph));
                break;
            }
        }
    }

    private static bool Generic(string n) =>
        string.IsNullOrWhiteSpace(n) || n.StartsWith("USB Composite", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("USB Root", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Generic USB Hub", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("Input Device", StringComparison.OrdinalIgnoreCase) && !n.Contains("Keyboard") && !n.Contains("Mouse");

    private static string Clean(string n) => System.Text.RegularExpressions.Regex.Replace(n, @"\s+USB Device$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    public void Dispose()
    {
        try { _watcher?.Stop(); _watcher?.Dispose(); } catch { }
        _flush?.Dispose();
    }
}

// =====================================================================================
//  Internet — WinRT NetworkInformation
// =====================================================================================
public sealed class NetworkWatcher
{
    private bool? _online;
    private string _name = "";

    /// <summary>(online, network name, isWifi). Raised when internet access appears/disappears or the network changes.</summary>
    public event Action<bool, string, bool>? Changed;

    public void Start()
    {
        Check(initial: true);
        Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += _ => Check(false);
    }

    private void Check(bool initial)
    {
        try
        {
            var p = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            var online = p != null && p.GetNetworkConnectivityLevel() == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;
            var wifi = p?.IsWlanConnectionProfile == true;
            var name = p == null ? "" : wifi ? p.WlanConnectionProfileDetails?.GetConnectedSsid() ?? p.ProfileName : p.ProfileName;
            if (!initial && (online != _online || (online && name != _name))) Changed?.Invoke(online, name ?? "", wifi);
            _online = online;
            _name = name ?? "";
        }
        catch (Exception ex) { App.Log("network: " + ex.Message); }
    }
}

// =====================================================================================
//  Notifications — watch for Windows toast pop-ups and read their text via UI Automation.
//  (UserNotificationListener needs package identity, which a plain .exe doesn't have.)
// =====================================================================================
public sealed class NotificationWatcher : IDisposable
{
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);

    private const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_NAMECHANGE = 0x800C;
    private readonly WinEventProc _proc;   // keep alive
    private IntPtr _hook, _hook2;
    private DateTime _lastFire = DateTime.MinValue;
    private string _lastText = "";

    /// <summary>The toast that just appeared (text + its inline reply box, if any).</summary>
    public event Action<ToastInfo>? Received;

    public NotificationWatcher() => _proc = OnEvent;

    public void Start()
    {
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, 0 /*OUTOFCONTEXT*/);
        _hook2 = SetWinEventHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _proc, 0, 0, 0);
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != 0 /*OBJID_WINDOW*/) return;
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() != "Windows.UI.Core.CoreWindow") return;
        var title = new StringBuilder(128);
        GetWindowText(hwnd, title, title.Capacity);
        var t = title.ToString();
        // English "New notification"; other languages still contain the toast's own text, so also accept
        // any CoreWindow whose UIA tree holds a toast (checked below).
        if (ev == EVENT_OBJECT_NAMECHANGE && !t.Contains("notification", StringComparison.OrdinalIgnoreCase)) return;
        var h = hwnd;
        Task.Run(async () =>
        {
            await Task.Delay(250); // let the toast finish building its UI
            try { Read(h, t); } catch (Exception ex) { App.Log("notification read: " + ex.Message); }
        });
    }

    private void Read(IntPtr hwnd, string windowTitle)
    {
        var root = AutomationElement.FromHandle(hwnd);
        if (root == null) return;
        // the toast is a ListItem / Window whose Name looks like "New notification from App, Title, Body"
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        string? header = null;
        var texts = new List<string>();
        AutomationElement? replyBox = null, sendButton = null;
        foreach (AutomationElement e in all)
        {
            string name;
            ControlType ct0;
            try { name = e.Current.Name ?? ""; ct0 = e.Current.ControlType; } catch { continue; }
            if (ct0 == ControlType.Edit && replyBox == null) replyBox = e;
            if (ct0 == ControlType.Button && sendButton == null)
            {
                var bn = name.ToLowerInvariant();
                if (bn.Contains("send") || bn.Contains("reply") || bn.Contains("envoyer") || bn.Contains("enviar") || bn.Contains("senden")) sendButton = e;
            }
            if (string.IsNullOrWhiteSpace(name)) continue;
            var ct = ct0;
            if (header == null && (ct == ControlType.ListItem || ct == ControlType.Window || ct == ControlType.Group) &&
                (name.Contains("notification", StringComparison.OrdinalIgnoreCase) || name.Contains(',')))
                header = name;
            if (ct == ControlType.Text && name.Length > 0 && texts.Count < 8 && !texts.Contains(name)) texts.Add(name);
        }
        if (header == null && texts.Count == 0) return;
        if (header == null && !windowTitle.Contains("notification", StringComparison.OrdinalIgnoreCase)) return;

        // "New notification from WhatsApp. Ana: hi there" / "…from WhatsApp, Ana, hi there"
        string app = "", title = "", body = "";
        if (header != null)
        {
            var m = System.Text.RegularExpressions.Regex.Match(header, @"from\s+(.+?)[\.,:]\s*(.*)$");
            if (m.Success) { app = m.Groups[1].Value.Trim(); var rest = m.Groups[2].Value; var parts = rest.Split(new[] { ", ", ". ", ": " }, 2, StringSplitOptions.RemoveEmptyEntries); title = parts.ElementAtOrDefault(0) ?? ""; body = parts.ElementAtOrDefault(1) ?? ""; }
        }
        if (texts.Count > 0)
        {
            // Text elements are usually: app name, title, body (app name sometimes missing)
            if (app.Length == 0 && texts.Count >= 3) { app = texts[0]; title = texts[1]; body = texts[2]; }
            else
            {
                var rest = texts.Where(x => !x.Equals(app, StringComparison.OrdinalIgnoreCase)).ToList();
                if (rest.Count > 0) title = rest[0];
                if (rest.Count > 1) body = rest[1];
            }
        }
        if (title.Length == 0 && body.Length == 0) return;
        // Notification Center / the calendar flyout itself (date header) is not a notification
        if (app.Equals("Notifications", StringComparison.OrdinalIgnoreCase) || windowTitle.Contains("Notification Center", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(title.Replace("‎", ""), @"^\d{1,2}:\d{2}(:\d{2})?$")) return;

        var key = app + title + body;
        if (key == _lastText && DateTime.Now - _lastFire < TimeSpan.FromSeconds(4)) return;
        _lastText = key;
        _lastFire = DateTime.Now;
        App.Log($"notification: [{app}] {title} — {body}{(replyBox != null ? "  (has reply box)" : "")}");
        Received?.Invoke(new ToastInfo
        {
            App = app, Title = title, Body = body, Texts = texts.Concat(header != null ? new[] { header } : Array.Empty<string>()).ToList(),
            ReplyBox = replyBox, SendButton = sendButton,
        });
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        if (_hook2 != IntPtr.Zero) UnhookWinEvent(_hook2);
    }
}
