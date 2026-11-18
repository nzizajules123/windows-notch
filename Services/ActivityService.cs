using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace WindowsNotch.Services;

public enum ActivityState { Idle, Working, Done }

/// <summary>One AI / dev app the notch keeps an eye on (Claude Code session, Claude, ChatGPT, VS Code).</summary>
public sealed class ActivityItem : INotifyPropertyChanged
{
    public string Key { get; init; } = "";
    public string App { get; init; } = "";
    public string Kind { get; init; } = "";
    public ImageSource? Icon { get; set; }
    public IntPtr Hwnd { get; set; }

    private string _title = "", _detail = "";
    private ActivityState _state;
    private DateTime _since = DateTime.Now;
    public string Title { get => _title; set { if (_title != value) { _title = value; Changed(); } } }
    public string Detail { get => _detail; set { if (_detail != value) { _detail = value; Changed(); } } }
    public DateTime Since => _since;
    public ActivityState State
    {
        get => _state;
        set { if (_state == value) return; _state = value; _since = DateTime.Now; Changed(); Changed(nameof(StateText)); Changed(nameof(StateBrush)); Changed(nameof(WorkingVisibility)); }
    }
    public string StateText => _state switch
    {
        ActivityState.Working => "Working",
        ActivityState.Done => "Done",
        _ => "Idle",
    };
    public Brush StateBrush => _state switch
    {
        ActivityState.Working => new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
        ActivityState.Done => new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
        _ => new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
    };
    public Visibility WorkingVisibility => _state == ActivityState.Working ? Visibility.Visible : Visibility.Collapsed;
    public void Touch() => Changed(nameof(When));
    public string When
    {
        get
        {
            var d = DateTime.Now - _since;
            return d.TotalSeconds < 60 ? "now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalHours} h";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// Watches Claude Code sessions (their local transcripts), the Claude and ChatGPT desktop apps and VS Code:
/// working / done status plus a short summary of the latest reply or what's open.
/// </summary>
public sealed class ActivityService
{
    public ObservableCollection<ActivityItem> Items { get; } = new();
    /// <summary>(item, previous state) — e.g. Working → Done means "finished".</summary>
    public event Action<ActivityItem, ActivityState>? StateChanged;

    private readonly Dictionary<string, (TimeSpan cpu, DateTime at)> _cpu = new();
    private readonly Dictionary<string, int> _busyStreak = new(), _idleStreak = new();
    private static readonly string ClaudeProjects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private static readonly (string proc, string app, double busyCpu)[] Apps =
    {
        ("claude", "Claude", 6), ("ChatGPT", "ChatGPT", 6), ("Code", "VS Code", 14),
    };

    public void Refresh()
    {
        try { RefreshClaudeCode(); } catch (Exception ex) { App.Log("activity claude code: " + ex.Message); }
        foreach (var (proc, app, busy) in Apps)
        {
            try { RefreshApp(proc, app, busy); } catch (Exception ex) { App.Log($"activity {app}: {ex.Message}"); }
        }
        foreach (var i in Items) i.Touch();
    }

    // ------------------------------------------------------------------ Claude Code (transcripts)
    private void RefreshClaudeCode()
    {
        if (!Directory.Exists(ClaudeProjects)) return;
        var recent = Directory.EnumerateFiles(ClaudeProjects, "*.jsonl", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Where(f => DateTime.Now - f.LastWriteTime < TimeSpan.FromHours(8) && !f.FullName.Contains("subagents", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTime)
            .Take(3)
            .ToList();

        foreach (var f in recent)
        {
            var key = "cc:" + f.FullName;
            var item = Items.FirstOrDefault(i => i.Key == key);
            if (item == null)
            {
                item = new ActivityItem { Key = key, App = "Claude Code", Kind = "session", Icon = IconFor("claude") };
                Items.Add(item);
            }
            var (project, summary, lastIsUser) = ReadTranscript(f.FullName);
            item.Title = project ?? Path.GetFileName(Path.GetDirectoryName(f.FullName) ?? "") ?? "Session";
            var age = DateTime.Now - f.LastWriteTime;
            var state = age < TimeSpan.FromSeconds(20) || (lastIsUser && age < TimeSpan.FromMinutes(3)) ? ActivityState.Working
                      : age < TimeSpan.FromMinutes(30) ? ActivityState.Done : ActivityState.Idle;
            item.Detail = state == ActivityState.Working && string.IsNullOrEmpty(summary) ? "Thinking…" : summary ?? "";
            SetState(item, state);
        }
        // forget sessions that dropped out of the recent list
        foreach (var gone in Items.Where(i => i.Kind == "session" && !recent.Any(f => "cc:" + f.FullName == i.Key)).ToList()) Items.Remove(gone);
    }

    /// <summary>Project folder + the latest assistant text (first sentence or two) from the end of a transcript.</summary>
    private static (string? project, string? summary, bool lastIsUser) ReadTranscript(string path)
    {
        const int tail = 192 * 1024;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = fs.Length;
        fs.Seek(Math.Max(0, len - tail), SeekOrigin.Begin);
        var buf = new byte[Math.Min(tail, len)];
        var read = fs.Read(buf, 0, buf.Length);
        var lines = Encoding.UTF8.GetString(buf, 0, read).Split('\n');

        string? project = null, summary = null;
        bool? lastIsUser = null;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length < 2 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (project == null && r.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } c)
                    project = Path.GetFileName(c.TrimEnd('\\', '/'));
                var type = r.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (lastIsUser == null && type is "user" or "assistant")
                {
                    // a user entry that is only a tool result still means the assistant is working
                    lastIsUser = type == "user";
                }
                if (summary == null && type == "assistant" && r.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text" && part.TryGetProperty("text", out var tx) && tx.GetString() is { Length: > 0 } text)
                        { summary = Summarize(text); break; }
                }
                if (summary != null && project != null && lastIsUser != null) break;
            }
            catch { /* partial line at the start of the tail */ }
        }
        return (project, summary, lastIsUser == true);
    }

    /// <summary>Plain-text first sentence(s) of a markdown reply, ~150 characters.</summary>
    public static string Summarize(string md)
    {
        var s = Regex.Replace(md, @"```[\s\S]*?```", " ");
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        s = Regex.Replace(s, @"[*_`#>|]+", "");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length <= 150) return s;
        var cut = s.LastIndexOfAny(new[] { '.', '!', '?' }, 150);
        return cut > 60 ? s[..(cut + 1)] : s[..147] + "…";
    }

    // ------------------------------------------------------------------ desktop apps (window + CPU)
    private void RefreshApp(string procName, string app, double busyCpu)
    {
        var procs = Process.GetProcessesByName(procName);
        try
        {
            var item = Items.FirstOrDefault(i => i.Key == "app:" + app);
            if (procs.Length == 0)
            {
                if (item != null) Items.Remove(item);
                return;
            }
            var main = procs.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle));
            if (main == null && item == null) return;   // only a background helper is running
            if (item == null)
            {
                item = new ActivityItem { Key = "app:" + app, App = app, Kind = "app", Icon = IconFor(procName, procs) };
                Items.Add(item);
            }
            if (main != null)
            {
                item.Hwnd = main.MainWindowHandle;
                item.Title = CleanTitle(app, main.MainWindowTitle);
            }

            // CPU of all the app's processes since the last check
            var total = TimeSpan.Zero;
            foreach (var p in procs) { try { total += p.TotalProcessorTime; } catch { } }
            var now = DateTime.Now;
            double pct = 0;
            if (_cpu.TryGetValue(app, out var prev) && now > prev.at)
                pct = (total - prev.cpu).TotalMilliseconds / ((now - prev.at).TotalMilliseconds * Environment.ProcessorCount) * 100;
            _cpu[app] = (total, now);

            // a few busy samples in a row = working; a few calm ones after that = done
            var busy = pct > busyCpu;
            _busyStreak[app] = busy ? _busyStreak.GetValueOrDefault(app) + 1 : 0;
            _idleStreak[app] = busy ? 0 : _idleStreak.GetValueOrDefault(app) + 1;
            if (_busyStreak[app] >= 2) SetState(item, ActivityState.Working);
            else if (item.State == ActivityState.Working && _idleStreak[app] >= 3) SetState(item, ActivityState.Done);
            else if (item.State == ActivityState.Done && DateTime.Now - item.Since > TimeSpan.FromMinutes(10)) SetState(item, ActivityState.Idle);

            if (item.State == ActivityState.Working)
                item.Detail = app == "VS Code" ? $"Busy — building, indexing or running something ({pct:0}% CPU)" : "Responding…";
            else if (item.Detail.Length == 0 || item.Detail.StartsWith("Busy") || item.Detail == "Responding…")
                item.Detail = item.State == ActivityState.Done ? (app == "VS Code" ? "Finished working" : "Reply finished") : "Open";
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    private static string CleanTitle(string app, string title) => app switch
    {
        "VS Code" => Regex.Replace(title, @"\s*-\s*Visual Studio Code.*$", "").Replace(" - ", "  ·  "),
        _ => title is "Claude" or "ChatGPT" ? "Open" : title,
    };

    /// <summary>A notification from one of these apps (e.g. "Claude finished responding") updates its card.</summary>
    public void OnToast(string app, string title, string body)
    {
        var name = app.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? "ChatGPT"
                 : app.Contains("Claude", StringComparison.OrdinalIgnoreCase) ? "Claude"
                 : app.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase) || app == "VS Code" ? "VS Code" : null;
        if (name == null) return;
        var item = Items.FirstOrDefault(i => i.Key == "app:" + name);
        if (item == null) { item = new ActivityItem { Key = "app:" + name, App = name, Kind = "app" }; Items.Add(item); }
        item.Detail = Summarize(string.Join(" — ", new[] { title, body }.Where(s => !string.IsNullOrWhiteSpace(s))));
        SetState(item, ActivityState.Done);
    }

    private void SetState(ActivityItem item, ActivityState s)
    {
        if (item.State == s) return;
        var prev = item.State;
        item.State = s;
        StateChanged?.Invoke(item, prev);
    }

    private static readonly Dictionary<string, ImageSource?> Icons = new();
    private static ImageSource? IconFor(string procName, Process[]? procs = null)
    {
        if (Icons.TryGetValue(procName, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            var p = procs?.FirstOrDefault() ?? Process.GetProcessesByName(procName).FirstOrDefault();
            var path = p?.MainModule?.FileName;
            if (path != null)
            {
                var large = new IntPtr[1];
                if (Native.ExtractIconEx(path, 0, large, null, 1) > 0) icon = Native.IconToBitmap(large[0], true);
            }
        }
        catch { }
        return Icons[procName] = icon;
    }
}
