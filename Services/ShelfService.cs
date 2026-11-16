using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace WindowsNotch.Services;

public sealed class ShelfItem
{
    public string Path { get; set; } = "";
    public string? Text { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;

    [JsonIgnore] public bool IsText => Text != null;
    [JsonIgnore] public ImageSource? Icon { get; set; }
    [JsonIgnore] public bool Exists => IsText || File.Exists(Path) || Directory.Exists(Path);
    [JsonIgnore] public double Dim => Exists ? 1.0 : 0.4;

    [JsonIgnore]
    public string Name
    {
        get
        {
            if (IsText) return Text!.Length > 18 ? Text[..17].Replace('\n', ' ') + "…" : Text.Replace('\n', ' ');
            var n = System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(n) ? Path : n;
        }
    }

    [JsonIgnore] public string Tip => IsText ? Text! : Path;
    [JsonIgnore] public string Initial => IsText ? "Aa" : "?";
    [JsonIgnore] public Visibility InitialVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Drop files / folders / text on the notch to keep them handy. Persisted between runs.</summary>
public sealed class ShelfService
{
    public ObservableCollection<ShelfItem> Items { get; } = new();
    private static string FilePath => System.IO.Path.Combine(App.DataDir, "shelf.json");

    public ShelfService()
    {
        try
        {
            if (File.Exists(FilePath))
                foreach (var item in JsonSerializer.Deserialize<List<ShelfItem>>(File.ReadAllText(FilePath)) ?? new())
                {
                    LoadIcon(item);
                    Items.Add(item);
                }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static void LoadIcon(ShelfItem item)
    {
        if (!item.IsText && item.Exists) item.Icon = Native.FileIcon(item.Path);
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new ShelfItem { Path = p };
            LoadIcon(item);
            Items.Insert(0, item);
        }
        Save();
    }

    public void AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Items.Insert(0, new ShelfItem { Text = text.Trim() });
        Save();
    }

    public void Remove(ShelfItem item) { Items.Remove(item); Save(); }
    public void Clear() { Items.Clear(); Save(); }

    public static void Open(ShelfItem item)
    {
        try
        {
            if (item.IsText) { Clipboard.SetText(item.Text!); return; }
            if (item.Exists) Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public static void Reveal(ShelfItem item)
    {
        if (!item.IsText && item.Exists)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.ToList(), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { App.Log(ex); }
    }
}
