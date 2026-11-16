using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsNotch.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed class LyricsResult
{
    public List<LyricLine> Lines { get; } = new();
    public bool Synced => Lines.Count > 0;
    public string Match { get; set; } = "";

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

    /// <param name="fromMusicApp">true for Spotify-style metadata (clean title + real artist);
    /// false for browsers, where the "title" is usually a video title like "Artist - Song (Official Video)".</param>
    public async Task<LyricsResult?> GetAsync(string rawTitle, string rawArtist, TimeSpan duration, bool fromMusicApp = true)
    {
        if (string.IsNullOrWhiteSpace(rawTitle)) return null;
        var (title, artist) = Normalize(rawTitle, rawArtist, fromMusicApp);
        if (title.Length == 0) return null;
        var key = $"{title}|{artist}".ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        LyricsResult? result = null;
        try
        {
            // 1) exact lookup (only trustworthy with a real artist and an exact track length)
            if (artist.Length > 0)
            {
                var q = $"track_name={E(title)}&artist_name={E(artist)}";
                if (fromMusicApp && duration > TimeSpan.FromSeconds(5)) q += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
                result = await TryGet($"https://lrclib.net/api/get?{q}", title, artist, duration, fromMusicApp);
            }
            // 2) searches — every candidate is checked against title / artist / length
            if (result == null && artist.Length > 0)
                result = await TrySearch($"https://lrclib.net/api/search?track_name={E(title)}&artist_name={E(artist)}", title, artist, duration, fromMusicApp);
            if (result == null)
                result = await TrySearch($"https://lrclib.net/api/search?q={E((title + " " + artist).Trim())}", title, artist, duration, fromMusicApp);
            // 3) browsers: the "artist" is often just the uploader's channel → title only, gated by track length
            if (result == null && !fromMusicApp && artist.Length > 0 && duration > TimeSpan.FromSeconds(30))
                result = await TrySearch($"https://lrclib.net/api/search?track_name={E(title)}", title, "", duration, fromMusicApp);
        }
        catch (Exception ex) { App.Log("lyrics: " + ex.Message); }

        App.Log($"lyrics: \"{rawTitle}\" / \"{rawArtist}\" → searched \"{title}\" by \"{artist}\" → {(result == null ? "no match" : result.Match)}");
        _cache[key] = result;
        return result;
    }

    private static string E(string s) => Uri.EscapeDataString(s ?? "");

    // ------------------------------------------------------------------ title clean-up
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Turns "Indila - Love Story (Official Video) [HD]" + "IndilaVEVO" into ("Love Story", "Indila").</summary>
    public static (string title, string artist) Normalize(string title, string artist, bool fromMusicApp)
    {
        var a = Regex.Replace(artist ?? "", @"\s*(VEVO|-\s*Topic|Official|TV)\s*$", "", I).Trim();
        if (Regex.IsMatch(a, @"^(YouTube|Google Chrome|Chrome|Microsoft Edge|Edge|Firefox|Unknown.*)$", I)) a = "";
        var t = title ?? "";

        if (fromMusicApp)
        {
            // Spotify: "Song - Remastered 2011", "Song - Live at …", "Song (feat. X)"
            t = Regex.Replace(t, @"\s+-\s+(.*(remaster|live|version|edit|mix|mono|stereo|demo|bonus).*)$", "", I);
            t = Regex.Replace(t, @"\s*[\(\[](feat|ft|with)\.?\s[^\)\]]*[\)\]]", "", I);
            return (t.Trim(), a);
        }

        t = Regex.Replace(t, @"\s+-\s+YouTube( Music)?$", "", I);                    // tab-title suffix
        t = Regex.Replace(t, @"\s*[\(\[\{【][^\)\]\}】]*[\)\]\}】]", "");                   // (Official Video) [HD] 【MV】
        t = Regex.Replace(t, @"\s*(\||//|#).*$", "");                                   // "Song | Channel", hashtags
        t = Regex.Replace(t, @"\b(official\s+)?(music\s+|lyrics?\s+|visuali[sz]er\s+)?(video|audio|clip)\b", "", I);
        t = Regex.Replace(t, @"\b(lyrics?|paroles|letra|hd|hq|4k|mv|m/v|clip officiel)\b", "", I);
        t = Regex.Replace(t, @"\s+(ft\.?|feat\.?|featuring|x)\s+[^-–—]*", " ", I);   // "Song ft. X"
        var dash = Regex.Match(t, @"^\s*(.+?)\s+[-–—~]\s+(.+?)\s*$");
        if (dash.Success)
        {
            a = dash.Groups[1].Value.Trim();
            t = dash.Groups[2].Value.Trim();
        }
        t = Regex.Replace(t, "[\"“”«»]", "");
        t = Regex.Replace(t, @"\s{2,}", " ").Trim(' ', '-', '–', '—', ':', '.');
        return (t, a);
    }

    private static string Norm(string s)
    {
        s = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static bool Similar(string a, string b)
    {
        a = Norm(a); b = Norm(b);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b || a.Contains(b) || b.Contains(a)) return true;
        var ta = a.Split(' ').ToHashSet();
        var tb = b.Split(' ').ToHashSet();
        var common = ta.Intersect(tb).Count();
        return common >= Math.Max(1, Math.Min(ta.Count, tb.Count) * 0.6);
    }

    /// <summary>Rejects lyrics that belong to another song (the cause of "same lyrics for every video").</summary>
    private static bool Accept(JsonElement el, string title, string artist, TimeSpan duration, bool fromMusicApp)
    {
        var track = el.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "";
        var by = el.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "";
        if (!Similar(track, title)) return false;

        var artistOk = artist.Length == 0 || Similar(by, artist);
        var durOk = true;
        if (duration > TimeSpan.FromSeconds(30) && el.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
        {
            var diff = d.GetDouble() - duration.TotalSeconds;
            // music videos often run longer than the song (intro / outro)
            durOk = fromMusicApp ? Math.Abs(diff) <= 4 : diff <= 6 && diff >= -60;
        }
        return artist.Length == 0 ? durOk : artistOk && (durOk || fromMusicApp);
    }

    private static async Task<LyricsResult?> TryGet(string url, string title, string artist, TimeSpan duration, bool fromMusicApp)
    {
        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return Accept(doc.RootElement, title, artist, duration, fromMusicApp) ? Parse(doc.RootElement) : null;
    }

    private static async Task<LyricsResult?> TrySearch(string url, string title, string artist, TimeSpan duration, bool fromMusicApp)
    {
        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!Accept(item, title, artist, duration, fromMusicApp)) continue;
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
            var text = Stamp.Replace(raw, "");
            text = Regex.Replace(text, @"<\d+:\d+(?:\.\d+)?>", "");   // enhanced-LRC word timings
            text = Regex.Replace(text, @"\s{2,}", " ").Trim();
            foreach (Match m in matches)
            {
                var t = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value))
                      + TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                result.Lines.Add(new LyricLine(t, text.Length == 0 ? "♪" : text));
            }
        }
        result.Lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        var tn = el.TryGetProperty("trackName", out var tnEl) ? tnEl.GetString() : "?";
        var an = el.TryGetProperty("artistName", out var anEl) ? anEl.GetString() : "?";
        result.Match = $"\"{tn}\" by \"{an}\"";
        return result.Synced ? result : null;
    }
}
