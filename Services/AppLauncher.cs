using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WindowsNotch.Services;

/// <summary>
/// Opens apps by name, the way the Start menu finds them. It reads every app in "shell:AppsFolder" (desktop apps
/// like VS Code and Chrome, and Store apps like Spotify, WhatsApp or Settings), then picks the best match for what
/// you said ("vs code", "word", "spotify").
/// </summary>
public static class AppLauncher
{
    private sealed record AppEntry(string Name, string Id, string Key);
    private static List<AppEntry>? _apps;
    private static DateTime _loadedAt;

    /// <summary>Spoken names that differ from the Start menu name.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vs code"] = "visual studio code", ["vscode"] = "visual studio code", ["code"] = "visual studio code",
        ["browser"] = "google chrome", ["chrome"] = "google chrome", ["edge"] = "microsoft edge",
        ["word"] = "word", ["excel"] = "excel", ["powerpoint"] = "powerpoint",
        ["file explorer"] = "file explorer", ["explorer"] = "file explorer", ["files"] = "file explorer", ["my files"] = "file explorer",
        ["terminal"] = "terminal", ["command prompt"] = "command prompt", ["cmd"] = "command prompt",
        ["calculator"] = "calculator", ["calc"] = "calculator", ["settings"] = "settings",
        ["notepad"] = "notepad", ["paint"] = "paint", ["camera"] = "camera", ["photos"] = "photos",
        ["task manager"] = "task manager", ["store"] = "microsoft store", ["mail"] = "outlook",
    };

    /// <summary>Opens the best match; returns its name, or null when nothing fits.</summary>
    public static string? Open(string spoken)
    {
        var want = Normalize(spoken);
        if (want.Length == 0) return null;
        if (Aliases.TryGetValue(want, out var alias)) want = Normalize(alias);

        var apps = Apps();
        AppEntry? best = null; var bestScore = 0;
        foreach (var a in apps)
        {
            var s = Score(a.Key, want);
            if (s > bestScore || (s == bestScore && best != null && a.Name.Length < best.Name.Length)) { best = a; bestScore = s; }
        }
        if (best != null && bestScore >= 50)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{best.Id}") { UseShellExecute = true });
                App.Log($"voice: opened app \"{best.Name}\"");
                return best.Name;
            }
            catch (Exception ex) { App.Log("open app: " + ex.Message); }
        }
        // last try: things Windows knows by command name (notepad, mspaint, calc…)
        try
        {
            Process.Start(new ProcessStartInfo(spoken.Trim()) { UseShellExecute = true });
            return spoken.Trim();
        }
        catch { return null; }
    }

    private static int Score(string name, string want)
    {
        if (name == want) return 100;
        if (name.StartsWith(want + " ") || name.EndsWith(" " + want)) return 90;
        if (name.StartsWith(want)) return 80;
        if (Regex.IsMatch(name, $@"\b{Regex.Escape(want)}\b")) return 75;
        var words = want.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => name.Contains(w))) return 70;
        if (want.Length >= 4 && name.Contains(want)) return 60;
        return 0;
    }

    private static string Normalize(string s) =>
        Regex.Replace(Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N} ]", " "), @"\s+", " ").Trim()
             .Replace("the app ", "") is var t ? Regex.Replace(t, @"\bapp\b", "").Trim() : "";

    /// <summary>Everything in shell:AppsFolder (cached for 10 minutes; read through the Windows shell, on the UI thread).</summary>
    private static List<AppEntry> Apps()
    {
        if (_apps != null && DateTime.Now - _loadedAt < TimeSpan.FromMinutes(10)) return _apps;
        var list = new List<AppEntry>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(shellType!)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                // skip uninstallers, readmes and help files
                var key = Normalize(name);
                if (key.Contains("uninstall") || key.Contains("readme") || key.EndsWith(" help") || key.Contains("documentation")) continue;
                list.Add(new AppEntry(name, id, key));
            }
        }
        catch (Exception ex) { App.Log("apps list: " + ex.Message); }
        _apps = list;
        _loadedAt = DateTime.Now;
        return list;
    }
}
