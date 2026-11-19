using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace WindowsNotch.Services;

public sealed class TodoItem : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Created { get; set; } = DateTime.Now;

    private string _text = "";
    private bool _done;
    private DateTime? _due;
    public string Text { get => _text; set { _text = value; Changed(); } }
    public bool Done { get => _done; set { _done = value; Changed(); Changed(nameof(TextOpacity)); Changed(nameof(Decorations)); Changed(nameof(CheckVisibility)); Changed(nameof(CheckFill)); Changed(nameof(DueText)); Changed(nameof(DueVisibility)); } }
    public DateTime? Due { get => _due; set { _due = value; Changed(); Touch(); } }
    public bool Notified { get; set; }

    // ---- view helpers
    [JsonIgnore] public double TextOpacity => _done ? 0.45 : 1;
    [JsonIgnore] public TextDecorationCollection? Decorations => _done ? TextDecorations.Strikethrough : null;
    [JsonIgnore] public Visibility CheckVisibility => _done ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Brush CheckFill => _done ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) : Brushes.Transparent;
    [JsonIgnore] public Visibility DueVisibility => _due != null && !_done ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public bool Overdue => _due != null && !_done && _due < DateTime.Now;

    [JsonIgnore] public string DueText
    {
        get
        {
            if (_due is not { } d || _done) return "";
            var span = d - DateTime.Now;
            if (span.TotalSeconds < 0)
            {
                var late = -span;
                return late.TotalMinutes < 60 ? $"overdue {Math.Max(1, (int)late.TotalMinutes)} min" : $"overdue · {d:HH:mm}";
            }
            if (span.TotalMinutes < 60) return $"in {Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))} min";
            if (d.Date == DateTime.Today) return $"today {d:HH:mm}";
            if (d.Date == DateTime.Today.AddDays(1)) return $"tomorrow {d:HH:mm}";
            return d.ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture);
        }
    }

    [JsonIgnore] public Brush DueBrush => Overdue ? new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x4F))
        : _due is { } d && d - DateTime.Now < TimeSpan.FromHours(1) ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20))
        : new SolidColorBrush(Color.FromArgb(0xB3, 0xF5, 0xF5, 0xF7));
    [JsonIgnore] public Brush DueBackground => Overdue ? new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0x45, 0x3A))
        : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    public void Touch() { Changed(nameof(DueText)); Changed(nameof(DueBrush)); Changed(nameof(DueBackground)); Changed(nameof(DueVisibility)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>A to-do list with reminders, saved to %APPDATA%\WindowsNotch\todo.json.</summary>
public sealed class TodoService
{
    private static readonly string FilePath = Path.Combine(App.DataDir, "todo.json");
    public ObservableCollection<TodoItem> Items { get; } = new();

    public TodoService()
    {
        try
        {
            if (File.Exists(FilePath))
                foreach (var t in JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(FilePath)) ?? new()) Items.Add(t);
        }
        catch (Exception ex) { App.Log("todo load: " + ex.Message); }
        Sort();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.ToList(), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { App.Log("todo save: " + ex.Message); }
    }

    public TodoItem Add(string text, DateTime? due)
    {
        var item = new TodoItem { Text = text, Due = due };
        Items.Add(item);
        Sort();
        Save();
        return item;
    }

    public void Toggle(TodoItem t) { t.Done = !t.Done; Sort(); Save(); }
    public void Remove(TodoItem t) { Items.Remove(t); Save(); }
    public void Snooze(TodoItem t, TimeSpan by) { t.Due = DateTime.Now + by; t.Notified = false; Sort(); Save(); }

    /// <summary>Open tasks first (soonest reminder first), then the rest, finished ones last.</summary>
    public void Sort()
    {
        var sorted = Items.OrderBy(t => t.Done).ThenBy(t => t.Due ?? DateTime.MaxValue).ThenBy(t => t.Created).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var cur = Items.IndexOf(sorted[i]);
            if (cur != i) Items.Move(cur, i);
        }
    }

    /// <summary>Reminders that just became due (each fires once).</summary>
    public List<TodoItem> DueNow()
    {
        var now = DateTime.Now;
        var due = Items.Where(t => !t.Done && !t.Notified && t.Due is { } d && d <= now).ToList();
        foreach (var t in due) t.Notified = true;
        if (due.Count > 0) Save();
        foreach (var t in Items) t.Touch();
        return due;
    }

    public int OpenCount => Items.Count(t => !t.Done);

    // ------------------------------------------------------------------ "call mom at 5pm", "stretch in 20 min", "report tomorrow"
    public static (string text, DateTime? due) Parse(string input)
    {
        var text = input.Trim();
        var now = DateTime.Now;
        DateTime? due = null;
        DateTime? day = null;
        const RegexOptions I = RegexOptions.IgnoreCase;

        var m = Regex.Match(text, @"\bin\s+(\d+(?:[.,]\d+)?)\s*(m|min|mins|minutes?|h|hr|hrs|hours?)\b", I);
        if (m.Success)
        {
            var n = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            due = m.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? now.AddHours(n) : now.AddMinutes(n);
            text = text.Remove(m.Index, m.Length);
        }

        m = Regex.Match(text, @"\btomorrow\b", I);
        if (m.Success) { day = DateTime.Today.AddDays(1); text = text.Remove(m.Index, m.Length); }
        m = Regex.Match(text, @"\btonight\b", I);
        if (m.Success && due == null) { due = DateTime.Today.AddHours(20); if (due < now) due = now.AddMinutes(30); text = text.Remove(m.Index, m.Length); }

        m = Regex.Match(text, @"\b(?:at|@)\s*(\d{1,2})(?:[:.h](\d{2}))?\s*(am|pm)?\b", I);
        if (!m.Success) m = Regex.Match(text, @"\b(\d{1,2})(?:[:.](\d{2}))?\s*(am|pm)\b", I);
        if (m.Success && due == null)
        {
            var h = int.Parse(m.Groups[1].Value);
            var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            var ap = m.Groups[3].Value.ToLowerInvariant();
            if (ap == "pm" && h < 12) h += 12;
            if (ap == "am" && h == 12) h = 0;
            if (h < 24 && min < 60)
            {
                var d = (day ?? DateTime.Today).AddHours(h).AddMinutes(min);
                if (day == null && d <= now) d = d.AddDays(1);
                due = d;
                text = text.Remove(m.Index, m.Length);
            }
        }
        if (due == null && day != null) due = day.Value.AddHours(9);

        text = Regex.Replace(text, @"\s{2,}", " ").Trim(' ', ',', '.', '-');
        if (text.Length > 0) text = char.ToUpper(text[0]) + text[1..];
        return (text, due);
    }
}
