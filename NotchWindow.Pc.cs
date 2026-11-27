using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using WindowsNotch.Services;
using WindowsNotch.UI;

namespace WindowsNotch;

/// <summary>
/// The AI's hands on the PC: sound, brightness, Wi-Fi / Bluetooth, dark mode, Settings, finding and opening files,
/// arranging windows, typing and shortcuts, the clipboard, screenshots, seeing the screen, PC health, frozen apps,
/// installing apps, power (behind the cancellable countdown) and the recycle bin (only after you confirm).
/// </summary>
public partial class NotchWindow
{
    private static object Obj(object properties, params string[] required) =>
        required.Length == 0 ? new { type = "OBJECT", properties } : new { type = "OBJECT", properties, required };

    private static object Choice(string description, params string[] values) => new { type = "STRING", description, @enum = values };
    private static object Int(string description) => new { type = "INTEGER", description };
    private static object Bool(string description) => new { type = "BOOLEAN", description };

    private static object[] PcTools() => new object[]
    {
        new { name = "set_volume", description = "Change the PC's sound volume: set a level, turn it up or down, mute or unmute.",
              parameters = Obj(new { action = Choice("What to do", "set", "up", "down", "mute", "unmute", "get"), level = Int("For 'set': 0–100. For up/down: how much (default 10).") }, "action") },
        new { name = "set_brightness", description = "Change the screen brightness (laptop screens): set a level, or turn it up or down.",
              parameters = Obj(new { action = Choice("What to do", "set", "up", "down", "get"), level = Int("For 'set': 0–100. For up/down: how much (default 15).") }, "action") },
        new { name = "system_toggle", description = "Turn Wi-Fi or Bluetooth on or off, or switch Windows between dark and light mode.",
              parameters = Obj(new { setting = Choice("Which", "wifi", "bluetooth", "dark_mode"), on = Bool("true = on (for dark_mode: dark), false = off (light)") }, "setting", "on") },
        new { name = "open_settings", description = "Open a Windows Settings page or system tool: wifi, bluetooth, display, night light, sound, notifications, battery, storage, apps (to uninstall), default apps, startup apps, wallpaper, themes, taskbar, time, language, keyboard, mouse, printers, privacy, camera, microphone, windows update, about, vpn, hotspot, accessibility, windows security, task manager, control panel, device manager.",
              parameters = Obj(new { page = Str("Which page, e.g. 'bluetooth' or 'windows update'") }, "page") },
        new { name = "find_files", description = "Find files or folders on the PC by name (Desktop, Documents, Downloads, Pictures, OneDrive and other drives). Use it when the user wants to open, find or check a file they mention ('open my CV', 'where's the report I downloaded', 'show my holiday photos'). Then open the right one with open_path, or set open_first when the best match is clearly what they meant.",
              parameters = Obj(new { query = Str("Words from the file or folder name, e.g. 'cv' or 'maths report'"), kind = Choice("Optional type of file (leave out for any)", "document", "pdf", "word", "excel", "powerpoint", "image", "photo", "video", "music", "code", "zip", "installer"), open_first = Bool("Open the best match straight away") }, "query") },
        new { name = "recent_files", description = "List the files the user opened most recently (to continue work, e.g. 'open what I was working on')." },
        new { name = "window_action", description = "Arrange windows: minimize, maximize, restore, snap to the left or right half, bring to the front, show the desktop (minimize everything), or open Task View. Without an app it acts on the window in front.",
              parameters = Obj(new { action = Choice("What to do", "minimize", "maximize", "restore", "snap_left", "snap_right", "focus", "show_desktop", "task_view"), app = Str("Which app's window, e.g. 'Chrome' (optional)") }, "action") },
        new { name = "type_text", description = "Type text into the app in front (or into the app named), as if typed on the keyboard: e.g. write a message in WhatsApp, a sentence in Word, a search in a box. Compose the full text yourself.",
              parameters = Obj(new { text = Str("Exactly what to type. Use \\n for a new line."), app = Str("Optional: the app to type into (it's brought to the front first)") }, "text") },
        new { name = "press_keys", description = "Press a keyboard shortcut in the app in front (or the app named): e.g. ctrl+s to save, ctrl+z undo, ctrl+t new tab, alt+f4 close, enter, f5 refresh, win+shift+s snipping tool, ctrl+shift+esc Task Manager.",
              parameters = Obj(new { keys = Str("The shortcut, keys joined with +, e.g. 'ctrl+s'"), app = Str("Optional: the app to send it to") }, "keys") },
        new { name = "clipboard", description = "Read what's on the clipboard (only when the user asks, e.g. 'read what I copied', 'summarize what I copied'), or put text on it for them to paste.",
              parameters = Obj(new { action = Choice("read or write", "read", "write"), text = Str("For write: the text to copy") }, "action") },
        new { name = "take_screenshot", description = "Take a screenshot of the whole screen and save it in Pictures\\Screenshots." },
        new { name = "look_at_screen", description = "See what's on the user's screen right now (you receive it as an image). Use it when they ask about something on their screen: 'what's this error?', 'help me with this', 'read this for me', 'what am I looking at?'.",
              parameters = Obj(new { question = Str("What the user wants to know about their screen") }) },
        new { name = "pc_status", description = "Check the PC's health: CPU, memory, disk space, battery, Wi-Fi, uptime, and the apps using the most memory (e.g. 'why is my PC slow?', 'how much space is left?')." },
        new { name = "force_close_app", description = "Force-close a frozen or stuck app (unsaved work in it is lost). Use close_app first for a normal close; use this when the user says it's frozen / not responding or asks to force it. Windows' own processes are never closed.",
              parameters = Obj(new { name = Str("The app, e.g. 'Chrome' or 'Word'") }, "name") },
        new { name = "power_action", description = "Restart, shut down, sleep or sign out of the PC — only when the user clearly asks. Restart, shut down and sign out show a countdown on the notch first, so the user can still cancel; 'cancel' stops a countdown that's running. Remind them to save their work.",
              parameters = Obj(new { action = Choice("What to do", "restart", "shut_down", "sleep", "sign_out", "cancel") }, "action") },
        new { name = "empty_recycle_bin", description = "Permanently delete everything in the Recycle Bin. First call it without confirmed to learn how much is there, tell the user, and ask them; call it again with confirmed=true only after they clearly say yes.",
              parameters = Obj(new { confirmed = Bool("true only after the user confirmed out loud") }) },
        new { name = "install_app", description = "Install an app with Windows' own installer (winget), in a window the user can see. Ask the user to confirm the exact app first. E.g. VLC, Spotify, Zoom, Python, Git, 7-Zip, Steam.",
              parameters = Obj(new { name = Str("The app's name, e.g. 'VLC media player'") }, "name") },
    };

    private static int ArgInt(JsonElement args, string name, int def) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : def;

    private static bool ArgBool(JsonElement args, string name, bool def) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.ValueKind == JsonValueKind.True : def;

    /// <summary>The window to act on: the app named, else the one in front (never the notch itself).</summary>
    private IntPtr TargetWindow(string app)
    {
        if (app.Length > 0) return FindOpenApps(app).FirstOrDefault()?.Hwnd ?? IntPtr.Zero;
        var fg = Native.GetForegroundWindow();
        return fg == _hwnd ? IntPtr.Zero : fg;
    }

    private static string Describe(PcControl.Found f) =>
        $"{f.Path} ({(f.Size < 0 ? "folder" : f.Size >= 1_048_576 ? $"{f.Size / 1_048_576.0:0.0} MB" : $"{Math.Max(1, f.Size / 1024)} KB")}, changed {f.Modified:d MMM yyyy HH:mm})";

    /// <summary>The PC actions. Null = not one of these.</summary>
    private async Task<(string, bool)?> RunPcActionAsync(string name, JsonElement args)
    {
        switch (name)
        {
            case "set_volume":
            {
                var now = PcControl.GetVolume();
                if (now == null) return ("There's no sound device to change.", false);
                var (level, muted) = now.Value;
                var step = ArgInt(args, "level", 10);
                switch (Arg(args, "action").ToLowerInvariant())
                {
                    case "get": return ($"The volume is at {level}%{(muted ? " (muted)" : "")}.", false);
                    case "mute": PcControl.SetMute(true); return ("Muted.", false);
                    case "unmute": PcControl.SetMute(false); return ($"Sound is back on, at {level}%.", false);
                    case "up": level = Math.Min(100, level + Math.Abs(step)); break;
                    case "down": level = Math.Max(0, level - Math.Abs(step)); break;
                    default: level = Math.Clamp(ArgInt(args, "level", level), 0, 100); break;
                }
                PcControl.SetVolume(level);
                return ($"The volume is now {level}%.", false);
            }
            case "set_brightness":
            {
                var now = await Task.Run(PcControl.GetBrightness);
                if (now == null) return ("This screen's brightness can't be changed from Windows (it's probably an external monitor: use its buttons).", false);
                var level = now.Value;
                var step = ArgInt(args, "level", 15);
                switch (Arg(args, "action").ToLowerInvariant())
                {
                    case "get": return ($"Brightness is at {level}%.", false);
                    case "up": level = Math.Min(100, level + Math.Abs(step)); break;
                    case "down": level = Math.Max(0, level - Math.Abs(step)); break;
                    default: level = Math.Clamp(ArgInt(args, "level", level), 0, 100); break;
                }
                return (await Task.Run(() => PcControl.SetBrightness(level)) ? $"Brightness is now {level}%." : "Couldn't change the brightness.", false);
            }
            case "system_toggle":
            {
                var on = ArgBool(args, "on", true);
                switch (Arg(args, "setting").ToLowerInvariant())
                {
                    case "dark_mode": return (PcControl.SetTheme(on), false);
                    case "bluetooth":
                    {
                        var r = await PcControl.SetRadioAsync("bluetooth", on);
                        if (r.StartsWith("Windows didn't")) PcControl.OpenSettings("bluetooth");
                        return (r, false);
                    }
                    default:
                    {
                        var r = await PcControl.SetRadioAsync("wifi", on);
                        if (r.StartsWith("Windows didn't")) PcControl.OpenSettings("wifi");
                        return (r, false);
                    }
                }
            }
            case "open_settings":
                return (PcControl.OpenSettings(Arg(args, "page")), false);
            case "find_files":
            {
                var q = Arg(args, "query");
                var kind = Arg(args, "kind");
                var found = await Task.Run(() => PcControl.FindFiles(q, kind));
                if (found.Count == 0) return ($"No files or folders matching \"{q}\" were found in the usual places.", false);
                if (ArgBool(args, "open_first", false))
                {
                    var opened = FileWriter.Open(found[0].Path);
                    Eyes.Express(EyeMood.Happy, 1000);
                    return ($"Opened {opened}." + (found.Count > 1 ? " Other matches: " + string.Join("; ", found.Skip(1).Take(4).Select(Describe)) : ""), false);
                }
                return ("Found:\n" + string.Join("\n", found.Select(Describe)), false);
            }
            case "recent_files":
            {
                var r = await Task.Run(() => PcControl.RecentFiles());
                return (r.Count == 0 ? "Windows has no recent files listed." : "Recently opened:\n" + string.Join("\n", r.Select(Describe)), false);
            }
            case "window_action":
            {
                var action = Arg(args, "action").ToLowerInvariant();
                if (action == "show_desktop") { Collapse(); PcControl.ShowDesktop(); return ("Showing the desktop.", false); }
                if (action == "task_view") { PcControl.TaskView(); return ("Opened Task View.", false); }
                var app = Arg(args, "app");
                var h = TargetWindow(app);
                if (h == IntPtr.Zero) return (app.Length > 0 ? $"No open window called \"{app}\"." : "There's no window in front to act on.", false);
                var title = AppsService.ShortTitle(h);
                switch (action)
                {
                    case "minimize": Native.ShowWindow(h, Native.SW_MINIMIZE); return ($"Minimized {title}.", false);
                    case "maximize": Native.ShowWindow(h, 3); Native.SetForegroundWindow(h); return ($"Maximized {title}.", false);
                    case "restore": Native.ShowWindow(h, 9); Native.SetForegroundWindow(h); return ($"Restored {title}.", false);
                    case "snap_left": PcControl.Snap(h, true); return ($"Snapped {title} to the left half.", false);
                    case "snap_right": PcControl.Snap(h, false); return ($"Snapped {title} to the right half.", false);
                    default: AppsService.Focus(h); return ($"Brought {title} to the front.", false);
                }
            }
            case "type_text":
            {
                var text = Arg(args, "text").Replace("\\n", "\n");
                if (text.Length == 0) return ("Nothing to type.", false);
                var app = Arg(args, "app");
                if (app.Length > 0)
                {
                    var h = TargetWindow(app);
                    if (h == IntPtr.Zero) return ($"No open window called \"{app}\" to type into.", false);
                    AppsService.Focus(h);
                    await Task.Delay(450);
                }
                await Task.Run(() => PcControl.TypeText(text));
                return ($"Typed {text.Length} characters{(app.Length > 0 ? " into " + app : "")}.", false);
            }
            case "press_keys":
            {
                var keys = Arg(args, "keys");
                var app = Arg(args, "app");
                if (app.Length > 0)
                {
                    var h = TargetWindow(app);
                    if (h == IntPtr.Zero) return ($"No open window called \"{app}\".", false);
                    AppsService.Focus(h);
                    await Task.Delay(400);
                }
                return (PcControl.Keys(keys) ? $"Pressed {keys}." : $"I don't know the key \"{keys}\".", false);
            }
            case "clipboard":
            {
                if (Arg(args, "action").Equals("write", StringComparison.OrdinalIgnoreCase))
                {
                    var t = Arg(args, "text");
                    try { Clipboard.SetText(t); return ("Copied to the clipboard — paste it with Ctrl+V.", false); }
                    catch (Exception ex) { return ("Couldn't copy: " + ex.Message, false); }
                }
                try
                {
                    if (!Clipboard.ContainsText()) return (Clipboard.ContainsImage() ? "The clipboard holds a picture, not text." : "The clipboard is empty.", false);
                    var t = Clipboard.GetText();
                    return ("The clipboard says:\n" + (t.Length > 4000 ? t[..4000] + "…" : t), false);
                }
                catch (Exception ex) { return ("Couldn't read the clipboard: " + ex.Message, false); }
            }
            case "take_screenshot":
            {
                Collapse();
                await Task.Delay(350);   // let the notch shrink out of the way
                try
                {
                    var path = PcControl.SaveScreenshot();
                    var dir = System.IO.Path.GetDirectoryName(path) ?? "";
                    ShowNotif(new NotifData("Notch", "Screenshot saved", System.IO.Path.GetFileName(path), null, "", null,
                        Color.FromRgb(0x5E, 0x8B, 0xFF), Act: () => FileWriter.Open(path), ActLabel: "Open"));
                    return ($"Saved the screenshot to {path}.", false);
                }
                catch (Exception ex) { return ("Couldn't take the screenshot: " + ex.Message, false); }
            }
            case "look_at_screen":
            {
                Collapse();
                await Task.Delay(300);
                byte[] jpeg;
                try { jpeg = PcControl.ScreenJpeg(); }
                catch (Exception ex) { return ("Couldn't capture the screen: " + ex.Message, false); }
                await _talk.SendImageAsync(jpeg);
                var q = Arg(args, "question");
                return ("The user's screen was just sent to you as an image. Look at it carefully and answer" +
                        (q.Length > 0 ? $": {q}" : " what they asked about it") + ". Be specific about what you see, and suggest the next step.", false);
            }
            case "pc_status":
            {
                var health = await Task.Run(PcControl.Health);
                var top = await Task.Run(() => PcControl.TopApps());
                return (health + " Using the most memory: " + string.Join(", ", top.Select(t => $"{t.name} {t.mb} MB")) + ".", false);
            }
            case "force_close_app":
            {
                var want = Arg(args, "name");
                var closed = await Task.Run(() => PcControl.ForceClose(want));
                return (closed.Count == 0 ? $"No running app called \"{want}\" (Windows' own processes are never closed)." : "Force-closed " + string.Join(", ", closed) + ".", false);
            }
            case "power_action":
            {
                var a = Arg(args, "action").ToLowerInvariant();
                if (a == "cancel")
                {
                    if (_countdown <= 0) return ("Nothing to cancel.", false);
                    CancelCountdown();
                    return ("Cancelled.", false);
                }
                if (a == "sleep")
                {
                    Collapse();
                    _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(2500); PowerService.Sleep(); });
                    return ("Putting the PC to sleep in a moment.", true);
                }
                var (verb, now, act) = a switch
                {
                    "restart" => ("Restarting", "Restart now", (Action)PowerService.Restart),
                    "sign_out" => ("Signing out", "Sign out now", () => PcControl.Shell("shutdown", "/l")),
                    _ => ("Shutting down", "Shut down now", (Action)PowerService.Shutdown),
                };
                if (_countdown > 0) return ("A countdown is already running.", false);
                StartCountdown(verb, now, CancelHint("Asked by voice"), act);
                return ($"{verb} after the countdown on the notch ({Math.Max(1, _settings.PowerCountdownSec)} seconds). Say \"cancel\" or click Cancel to stop it.", false);
            }
            case "empty_recycle_bin":
            {
                var (items, bytes) = PcControl.RecycleBin();
                if (items == 0) return ("The Recycle Bin is already empty.", false);
                var size = bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:0.0} GB" : $"{Math.Max(1, bytes / 1_048_576)} MB";
                if (!ArgBool(args, "confirmed", false))
                    return ($"The Recycle Bin has {items} item{(items == 1 ? "" : "s")} ({size}). Emptying it deletes them for good: ask the user to confirm first.", false);
                return (PcControl.EmptyRecycleBin() ? $"Emptied the Recycle Bin ({size} freed)." : "Couldn't empty the Recycle Bin.", false);
            }
            case "install_app":
            {
                var app = Arg(args, "name").Replace("\"", "");
                if (app.Length < 2) return ("Which app?", false);
                if (System.IO.File.Exists(Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe")) == false)
                {
                    PcControl.Shell("ms-windows-store://search/?query=" + Uri.EscapeDataString(app));
                    return ($"Windows' installer (winget) isn't available, so I opened the Microsoft Store search for {app}.", false);
                }
                PcControl.Shell("cmd.exe", $"/k title Installing {app} & winget install \"{app}\" --source winget");
                return ($"Started installing {app} in a window on the screen; it may ask the user to confirm or pick the exact app.", false);
            }
        }
        return null;
    }
}
