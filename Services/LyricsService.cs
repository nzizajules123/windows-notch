using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsNotch.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed class LyricsResult
{
    public List<LyricLine> Lines { get; } = new();
    public bool Synced => Lines.Count > 0;

    /// <summary>Index of the line being sung at <paramref name="pos"/>, or -1 before the first line.</summary>
    public int IndexAt(TimeSpan pos)
    {
        int idx = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Time <= pos + TimeSpan.FromMilliseconds(250)) idx = i;
            else break;
        }
        return idx;
    }

    /// <summary>When the karaoke fill of a line should reach 100 % (not dragged out over instrumental gaps).</summary>
    public TimeSpan LineEnd(int idx)
    {
        var start = Lines[idx].Time;
        var next = idx + 1 < Lines.Count ? Lines[idx + 1].Time : start + TimeSpan.FromSeconds(5);
        var natural = TimeSpan.FromSeconds(Math.Max(1.4, Lines[idx].Text.Length * 0.075) + 0.6);
        return start + (next - start < natural ? next - start : natural);
    }

    /// <summary>Returns (current, next) line for a playback position.</summary>
    public (string current, string next) At(TimeSpan pos)
    {
        int idx = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Time <= pos + TimeSpan.FromMilliseconds(250)) idx = i;
            else break;
        }
        var cur = idx >= 0 ? Lines[idx].Text : "";
        var next = idx + 1 < Lines.Count ? Lines[idx + 1].Text : "";
        return (cur, next);
    }
}

/// <summary>Time-synced lyrics from LRCLIB (free, open lyrics database — no key needed).</summary>
public sealed class LyricsService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly Dictionary<string, LyricsResult?> _cache = new();
    private static readonly Regex Stamp = new(@"\[(\d+):(\d+(?:\.\d+)?)\]", RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsNotch/1.0");
        return c;
    }

    public async Task<LyricsResult?> GetAsync(string title, string artist, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var key = $"{title}|{artist}".ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        LyricsResult? result = null;
        try
        {
            var q = $"track_name={E(title)}&artist_name={E(artist)}";
            if (duration > TimeSpan.FromSeconds(5)) q += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
            result = await TryGet($"https://lrclib.net/api/get?{q}");

            if (result == null)
            {
                // Fuzzy fallback: strip "- Remastered", "(feat. …)", "[Official Video]" etc.
                var clean = Regex.Replace(title, @"\s*[\(\[].*?[\)\]]|\s+-\s+.*$", "").Trim();
                result = await TrySearch($"https://lrclib.net/api/search?track_name={E(clean)}&artist_name={E(artist)}")
                      ?? await TrySearch($"https://lrclib.net/api/search?q={E(clean + " " + artist)}");
            }
        }
        catch (Exception ex) { App.Log("lyrics: " + ex.Message); }

        _cache[key] = result;
        return result;
    }

    private static string E(string s) => Uri.EscapeDataString(s ?? "");

    private static async Task<LyricsResult?> TryGet(string url)
    {
        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return Parse(doc.RootElement);
    }

    private static async Task<LyricsResult?> TrySearch(string url)
    {
        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var r = Parse(item);
            if (r != null) return r;
        }
        return null;
    }

    private static LyricsResult? Parse(JsonElement el)
    {
        if (!el.TryGetProperty("syncedLyrics", out var synced) || synced.ValueKind != JsonValueKind.String) return null;
        var result = new LyricsResult();
        foreach (var raw in synced.GetString()!.Split('\n'))
        {
            var matches = Stamp.Matches(raw);
            if (matches.Count == 0) continue;
            var text = Stamp.Replace(raw, "").Trim();
            foreach (Match m in matches)
            {
                var t = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value))
                      + TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                result.Lines.Add(new LyricLine(t, text.Length == 0 ? "♪" : text));
            }
        }
        result.Lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result.Synced ? result : null;
    }
}
