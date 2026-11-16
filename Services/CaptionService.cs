using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Automation;

namespace WindowsNotch.Services;

/// <summary>
/// Shows live captions for ANY audio (YouTube, Chrome, VLC, Teams…) by reading the text of
/// Windows 11's built-in Live Captions through UI Automation. The Live Captions window itself
/// is hidden so captions appear only inside the notch.
/// </summary>
public sealed class CaptionService
{
    private CancellationTokenSource? _cts;
    private IntPtr _lcHwnd;
    private bool _weLaunched;

    public event Action<string>? LineChanged;
    public event Action<string>? StatusChanged;
    public bool Running => _cts != null;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(() => Loop(token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        if (_weLaunched && _lcHwnd != IntPtr.Zero) Native.PostMessage(_lcHwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _weLaunched = false;
        _lcHwnd = IntPtr.Zero;
        LineChanged?.Invoke("");
    }

    private static AutomationElement? FindWindow() =>
        AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, "LiveCaptionsDesktopWindow"));

    private void Loop(CancellationToken token)
    {
        AutomationElement? win = null, text = null;
        var last = "";
        var launchedAt = DateTime.MinValue;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (win == null)
                {
                    win = FindWindow();
                    if (win == null)
                    {
                        if (launchedAt == DateTime.MinValue)
                        {
                            StatusChanged?.Invoke("Starting Windows Live Captions…");
                            try
                            {
                                Process.Start(new ProcessStartInfo("LiveCaptions.exe") { UseShellExecute = true });
                                _weLaunched = true;
                            }
                            catch (Win32Exception)
                            {
                                StatusChanged?.Invoke("Live Captions needs Windows 11 22H2 or newer");
                                return;
                            }
                            launchedAt = DateTime.Now;
                        }
                        else if ((DateTime.Now - launchedAt).TotalSeconds > 15)
                        {
                            StatusChanged?.Invoke("Live Captions didn't open — press Win+Ctrl+L once to set it up");
                            launchedAt = DateTime.Now;
                        }
                        Thread.Sleep(700);
                        continue;
                    }

                    var hwnd = new IntPtr(win.Current.NativeWindowHandle);
                    if (hwnd != _lcHwnd)
                    {
                        _lcHwnd = hwnd;
                        // Hide the Live Captions window: minimise + remove from taskbar/Alt-Tab.
                        Native.ShowWindow(hwnd, Native.SW_MINIMIZE);
                        Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW);
                    }
                    text = null;
                    StatusChanged?.Invoke("Live");
                }

                text ??= win.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptionsTextBlock"));
                if (text == null) { Thread.Sleep(500); continue; }

                var line = LastLine(text.Current.Name ?? "");
                if (line != last)
                {
                    last = line;
                    LineChanged?.Invoke(line);
                }
            }
            catch (Exception)
            {
                win = null;
                text = null;
            }
            Thread.Sleep(300);
        }
    }

    private static string LastLine(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        const int max = 95;
        if (line.Length <= max) return line;
        var tail = line[^max..];
        var sp = tail.IndexOf(' ');
        return sp > 0 && sp < 20 ? tail[(sp + 1)..] : tail;
    }
}
