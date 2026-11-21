using System.IO;
using System.Text;
using System.Text.Json;

namespace WindowsNotch.Services;

/// <summary>
/// What you and the notch said in earlier live chats, so the next chat can pick up where you left off
/// ("what were we talking about?"). Kept only on this PC (%APPDATA%\WindowsNotch\talk-memory.json), last 60
/// exchanges; "forget our conversations" clears it.
/// </summary>
public sealed class TalkMemory
{
    public sealed record Entry(DateTime T, string You, string Notch);

    private const int Keep = 60;
    private readonly List<Entry> _items = new();
    private readonly object _lock = new();
    private static string FilePath => Path.Combine(App.DataDir, "talk-memory.json");

    public TalkMemory()
    {
        try
        {
            if (File.Exists(FilePath))
                _items.AddRange(JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new());
        }
        catch (Exception ex) { App.Log("talk memory: " + ex.Message); }
    }

    public int Count { get { lock (_lock) return _items.Count; } }

    public void Add(string you, string notch)
    {
        lock (_lock)
        {
            _items.Add(new Entry(DateTime.Now, Trim(you, 400), Trim(notch, 600)));
            while (_items.Count > Keep) _items.RemoveAt(0);
            Save();
        }
    }

    public void Clear()
    {
        lock (_lock) { _items.Clear(); Save(); }
    }

    /// <summary>The most recent exchanges as text for Gemini (newest last, at most <paramref name="maxChars"/>).</summary>
    public string Recent(int maxChars = 4000)
    {
        lock (_lock)
        {
            var lines = new List<string>();
            var total = 0;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var e = _items[i];
                var line = $"[{e.T:ddd d MMM HH:mm}] User: {e.You}\n  Notch: {e.Notch}";
                if (total + line.Length > maxChars) break;
                lines.Add(line);
                total += line.Length;
            }
            lines.Reverse();
            return string.Join("\n", lines);
        }
    }

    private void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_items), Encoding.UTF8); }
        catch (Exception ex) { App.Log("talk memory: " + ex.Message); }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
