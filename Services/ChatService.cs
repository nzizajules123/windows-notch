using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace WindowsNotch.Services;

/// <summary>A Windows toast as read through UI Automation, including its inline reply box when it has one.</summary>
public sealed class ToastInfo
{
    public string App { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public IReadOnlyList<string> Texts { get; init; } = Array.Empty<string>();
    public AutomationElement? ReplyBox { get; init; }
    public AutomationElement? SendButton { get; init; }
}

public sealed class ChatMessage
{
    public string From { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
    public bool Mine { get; init; }
    public HorizontalAlignment Align => Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public string TimeText => Time.ToString("HH:mm");
}

public sealed class ChatThread : INotifyPropertyChanged
{
    public string App { get; init; } = "";
    public string Title { get; init; } = "";
    public ImageSource? Icon { get; set; }
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    // live inline-reply target (only valid while the toast is on screen)
    internal AutomationElement? ReplyBox;
    internal AutomationElement? SendButton;
    internal DateTime ReplyUntil;

    public string LastText => Messages.Count == 0 ? "" : (Messages[^1].Mine ? "You: " : "") + Messages[^1].Text;
    public string Subtitle => $"{App}  ·  {(Messages.Count == 0 ? "" : Messages[^1].Time.ToString("HH:mm"))}";
    public string Initial => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "?";
    public int Unread { get => _unread; set { _unread = value; Changed(); Changed(nameof(HasUnread)); } }
    public Visibility HasUnread => _unread > 0 ? Visibility.Visible : Visibility.Collapsed;
    private int _unread;

    public void Touch() { Changed(nameof(LastText)); Changed(nameof(Subtitle)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public enum ReplyResult { SentInline, SentFromCenter, CopiedAndOpened, Failed }

/// <summary>
/// Collects WhatsApp / Instagram / Telegram / Messenger… messages from Windows notifications and replies to them:
/// 1) through the toast's own reply box while it's on screen, 2) through Notification Center,
/// 3) otherwise copy the reply and open the app so it's one paste away.
/// </summary>
public sealed class ChatService
{
    public ObservableCollection<ChatThread> Threads { get; } = new();

    private static readonly (string key, string app)[] Messengers =
    {
        ("whatsapp", "WhatsApp"), ("instagram", "Instagram"), ("telegram", "Telegram"), ("messenger", "Messenger"),
        ("signal", "Signal"), ("discord", "Discord"),
    };

    /// <summary>Messaging app name if this toast is a chat message, else null.</summary>
    public static string? MessengerOf(ToastInfo t)
    {
        var hay = (t.App + " " + string.Join(" ", t.Texts)).ToLowerInvariant();
        foreach (var (key, app) in Messengers)
            if (hay.Contains(key)) return app;
        return t.ReplyBox != null ? (string.IsNullOrWhiteSpace(t.App) ? "Chat" : t.App) : null;
    }

    public ChatThread Add(string app, ToastInfo t, ImageSource? icon)
    {
        // WhatsApp: title = contact / group, body = message ("Ana: hi" in groups)
        var title = string.IsNullOrWhiteSpace(t.Title) ? app : t.Title.Trim();
        title = System.Text.RegularExpressions.Regex.Replace(title, @"^(New message from|Message from)\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var thread = Threads.FirstOrDefault(x => x.App == app && x.Title == title);
        if (thread == null)
        {
            thread = new ChatThread { App = app, Title = title, Icon = icon };
            Threads.Insert(0, thread);
        }
        else if (Threads.IndexOf(thread) > 0) Threads.Move(Threads.IndexOf(thread), 0);
        thread.Icon ??= icon;
        thread.Messages.Add(new ChatMessage { From = title, Text = string.IsNullOrWhiteSpace(t.Body) ? "(message)" : t.Body });
        while (thread.Messages.Count > 30) thread.Messages.RemoveAt(0);
        thread.ReplyBox = t.ReplyBox;
        thread.SendButton = t.SendButton;
        thread.ReplyUntil = DateTime.Now.AddSeconds(20);
        thread.Unread++;
        thread.Touch();
        while (Threads.Count > 25) Threads.RemoveAt(Threads.Count - 1);
        return thread;
    }

    public async Task<ReplyResult> ReplyAsync(ChatThread thread, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ReplyResult.Failed;
        var last = thread.Messages.LastOrDefault(m => !m.Mine)?.Text ?? "";
        var result = await Task.Run(() =>
        {
            if (thread.ReplyBox != null && DateTime.Now < thread.ReplyUntil && TryInline(thread.ReplyBox, thread.SendButton, text))
                return ReplyResult.SentInline;
            if (TryNotificationCenter(thread, last, text)) return ReplyResult.SentFromCenter;
            return ReplyResult.Failed;
        });
        if (result == ReplyResult.Failed)
        {
            try { Clipboard.SetText(text); } catch { }
            OpenApp(thread.App);
            result = ReplyResult.CopiedAndOpened;
        }
        thread.Messages.Add(new ChatMessage { From = "You", Text = text, Mine = true });
        thread.Unread = 0;
        thread.Touch();
        App.Log($"chat reply → {thread.App}/{thread.Title}: {result}");
        return result;
    }

    // ------------------------------------------------------------------ 1) the toast itself
    private static bool TryInline(AutomationElement box, AutomationElement? send, string text)
    {
        try
        {
            // 1) put the text in the toast's reply box — and check it really landed there
            var typed = false;
            if (box.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                try { ((ValuePattern)vp).SetValue(text); Thread.Sleep(150); typed = ValueOf(box) == text; } catch { }
            }
            if (!typed)
            {
                // some toasts ignore SetValue: focus the box and type it like a keyboard would
                box.SetFocus();
                Thread.Sleep(120);
                TypeText(text);
                Thread.Sleep(200);
                typed = (ValueOf(box) ?? "").Contains(text.Length > 12 ? text[..12] : text);
            }
            App.Log($"inline reply: text in box = {typed}");
            if (!typed) return false;

            // 2) press Send once it's enabled, then make sure the toast actually went away
            send ??= FindSend(TreeWalker.ControlViewWalker.GetParent(box) ?? box);
            if (send == null || !send.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) return false;
            for (int i = 0; i < 10 && !send.Current.IsEnabled; i++) Thread.Sleep(80);
            ((InvokePattern)ip).Invoke();
            for (int i = 0; i < 12; i++)
            {
                Thread.Sleep(100);
                try { if (box.Current.IsOffscreen || ValueOf(box) != text) return true; }
                catch (ElementNotAvailableException) { return true; }   // toast closed = sent
            }
            App.Log("inline reply: toast still open after Send");
            return false;
        }
        catch (ElementNotAvailableException) { return true; }
        catch (Exception ex) { App.Log("inline reply: " + ex.Message); return false; }
    }

    private static string? ValueOf(AutomationElement e)
    {
        try { return e.TryGetCurrentPattern(ValuePattern.Pattern, out var p) ? ((ValuePattern)p).Current.Value : null; }
        catch { return null; }
    }

    // ---- unicode keyboard input (works for emoji and any language)
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT { public uint type; public KEYBDINPUT ki; public long pad1; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }

    private static void TypeText(string text)
    {
        var list = new List<INPUT>();
        foreach (var ch in text)
        {
            list.Add(new INPUT { type = 1, ki = new KEYBDINPUT { scan = ch, flags = 0x4 /*UNICODE*/ } });
            list.Add(new INPUT { type = 1, ki = new KEYBDINPUT { scan = ch, flags = 0x4 | 0x2 /*KEYUP*/ } });
        }
        var arr = list.ToArray();
        SendInput((uint)arr.Length, arr, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static AutomationElement? FindSend(AutomationElement scope)
    {
        var buttons = scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        AutomationElement? fallback = null;
        foreach (AutomationElement b in buttons)
        {
            var n = (b.Current.Name ?? "").ToLowerInvariant();
            if (n.Contains("send") || n.Contains("reply") || n.Contains("envoyer") || n.Contains("enviar") || n.Contains("senden") || n.Contains("ohereza"))
                return b;
            if (fallback == null && !n.Contains("close") && !n.Contains("dismiss") && !n.Contains("setting") && !n.Contains("expand")) fallback = b;
        }
        return fallback;
    }

    // ------------------------------------------------------------------ 2) Notification Center
    private static bool TryNotificationCenter(ChatThread thread, string lastText, string reply)
    {
        try
        {
            // Win + N opens Notification Center
            Native.keybd_event(0x5B, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x4E, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x4E, 0, 2, UIntPtr.Zero);
            Native.keybd_event(0x5B, 0, 2, UIntPtr.Zero);
            AutomationElement? item = null;
            for (int i = 0; i < 12 && item == null; i++)
            {
                Thread.Sleep(150);
                item = FindToastInCenter(thread, lastText);
            }
            if (item == null) { CloseCenter(); return false; }

            var box = item.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (box == null)
            {
                // the reply box is hidden until the notification is expanded
                var expand = item.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                    .Cast<AutomationElement>().FirstOrDefault(b => (b.Current.Name ?? "").Contains("Expand", StringComparison.OrdinalIgnoreCase)
                                                                || (b.Current.Name ?? "").Contains("more", StringComparison.OrdinalIgnoreCase));
                if (expand != null && expand.TryGetCurrentPattern(InvokePattern.Pattern, out var ep)) { ((InvokePattern)ep).Invoke(); Thread.Sleep(300); }
                box = item.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            }
            var ok = box != null && TryInline(box, FindSend(item), reply);
            Thread.Sleep(200);
            CloseCenter();
            return ok;
        }
        catch (Exception ex) { App.Log("center reply: " + ex.Message); CloseCenter(); return false; }
    }

    private static AutomationElement? FindToastInCenter(ChatThread thread, string lastText)
    {
        var root = AutomationElement.RootElement;
        var center = root.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "Notification Center"))
                  ?? root.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Windows.UI.Core.CoreWindow"));
        if (center == null) return null;
        var snippet = lastText.Length > 24 ? lastText[..24] : lastText;
        foreach (AutomationElement e in center.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
        {
            var n = e.Current.Name ?? "";
            if (n.Contains(thread.Title, StringComparison.OrdinalIgnoreCase) && (snippet.Length == 0 || n.Contains(snippet, StringComparison.OrdinalIgnoreCase)))
                return e;
        }
        return null;
    }

    private static void CloseCenter()
    {
        Native.keybd_event(0x1B, 0, 0, UIntPtr.Zero);   // Esc
        Native.keybd_event(0x1B, 0, 2, UIntPtr.Zero);
    }

    // ------------------------------------------------------------------ 3) open the app
    public static void OpenApp(string app)
    {
        var target = app switch
        {
            "WhatsApp" => "whatsapp:",
            "Instagram" => "https://www.instagram.com/direct/inbox/",
            "Telegram" => "tg:",
            "Messenger" => "https://www.messenger.com/",
            "Discord" => "discord:",
            _ => null,
        };
        if (target == null) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log("open app: " + ex.Message); }
    }
}
