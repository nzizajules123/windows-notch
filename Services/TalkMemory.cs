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

    private const int Keep = 300;
    public sealed record Note(DateTime T, string Text);
    private readonly List<Note> _notes = new();
    private static string NotesPath => Path.Combine(App.DataDir, "talk-notes.json");
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
        try
        {
            if (File.Exists(NotesPath))
                _notes.AddRange(JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(NotesPath)) ?? new());
        }
        catch (Exception ex) { App.Log("talk notes: " + ex.Message); }
    }

    // ------------------------------------------------------------------ long-term notes: goals, plans, projects
    public int NoteCount { get { lock (_lock) return _notes.Count; } }

    public string AddNote(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return "Nothing to note.";
        lock (_lock)
        {
            if (_notes.Any(n => n.Text.Equals(text, StringComparison.OrdinalIgnoreCase))) return "Already noted.";
            _notes.Add(new Note(DateTime.Now, Trim(text, 300)));
            while (_notes.Count > 80) _notes.RemoveAt(0);
            SaveNotes();
            return "Noted.";
        }
    }

    /// <summary>Forgets notes containing <paramref name="match"/> (or all of them for "all").</summary>
    public int RemoveNotes(string match)
    {
        lock (_lock)
        {
            var n = match.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) ? _notes.RemoveAll(_ => true)
                  : _notes.RemoveAll(x => x.Text.Contains(match.Trim(), StringComparison.OrdinalIgnoreCase));
            if (n > 0) SaveNotes();
            return n;
        }
    }

    public string NotesText()
    {
        lock (_lock) return string.Join("\n", _notes.Select(n => $"- ({n.T:d MMM}) {n.Text}"));
    }

    /// <summary>Older exchanges that mention any of the words (newest first), for "what did we say about…?".</summary>
    public string Search(string query, int max = 8)
    {
        var words = query.ToLowerInvariant().Split(new[] { ' ', ',', '.', '?' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
        if (words.Length == 0) return "";
        lock (_lock)
        {
            var hits = _items.Select(e => (e, score: words.Count(w => e.You.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Notch.Contains(w, StringComparison.OrdinalIgnoreCase))))
                             .Where(h => h.score > 0).OrderByDescending(h => h.score).ThenByDescending(h => h.e.T).Take(max)
                             .OrderBy(h => h.e.T);
            return string.Join("\n", hits.Select(h => $"[{h.e.T:ddd d MMM HH:mm}] User: {h.e.You}\n  Notch: {h.e.Notch}"));
        }
    }

    private void SaveNotes()
    {
        try { File.WriteAllText(NotesPath, JsonSerializer.Serialize(_notes), Encoding.UTF8); }
        catch (Exception ex) { App.Log("talk notes: " + ex.Message); }
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
        lock (_lock) { _items.Clear(); _notes.Clear(); Save(); SaveNotes(); }
    }

    /// <summary>The most recent exchanges as text for Gemini (newest last, at most <paramref name="maxChars"/>).</summary>
    public string Recent(int maxChars = 6000)
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
