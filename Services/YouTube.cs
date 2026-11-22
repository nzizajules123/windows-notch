using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WindowsNotch.Services;

public sealed class YtResult : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Channel { get; init; } = "";
    public string Length { get; init; } = "";
    public string ThumbUrl { get; init; } = "";
    public string Meta => string.IsNullOrEmpty(Length) ? Channel : $"{Channel}  ·  {Length}";

    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get
        {
            if (_thumb == null && ThumbUrl.Length > 0)
            {
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.UriSource = new Uri(ThumbUrl);
                    b.DecodePixelWidth = 160;
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.EndInit();
                    _thumb = b;
                }
                catch { }
            }
            return _thumb;
        }
    }

    private bool _playing;
    public bool Playing { get => _playing; set { _playing = value; Changed(); Changed(nameof(PlayingVisibility)); } }
    public Visibility PlayingVisibility => _playing ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// YouTube search without an API key: reads the public results page (ytInitialData). Personal use;
/// may need updating if YouTube changes its page.
/// </summary>
public static class YouTubeSearch
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        return c;
    }

    public static async Task<List<YtResult>> SearchAsync(string query, int max = 12)
    {
        query = query.Trim();
        if (query.Length == 0) return new();
        var q = Uri.EscapeDataString(query);
        // 1) the results page, "videos only" first, then the plain page
        foreach (var url in new[] { $"https://www.youtube.com/results?search_query={q}&sp=EgIQAQ%253D%253D", $"https://www.youtube.com/results?search_query={q}" })
        {
            try
            {
                var html = await Http.GetStringAsync(url);
                var list = Parse(html, max);
                if (list.Count > 0) return list;
                App.Log($"youtube search: 0 results from {(url.Contains("&sp=") ? "filtered" : "plain")} page ({Shape(html)})");
            }
            catch (Exception ex) { App.Log("youtube search page: " + ex.Message); }
        }
        // 2) YouTube's own search service (what youtube.com itself calls), in case the page layout can't be read
        try
        {
            var list = await SearchApiAsync(query, max);
            if (list.Count > 0) { App.Log("youtube search: results came from the search service"); return list; }
            App.Log("youtube search: 0 results from the search service too");
        }
        catch (Exception ex) { App.Log("youtube search service: " + ex.Message); }
        return new();
    }

    /// <summary>A short description of a page we couldn't read, for the log (so the next layout change is easy to fix).</summary>
    private static string Shape(string html)
    {
        int Count(string s) { int n = 0, i = 0; while ((i = html.IndexOf(s, i, StringComparison.Ordinal)) >= 0) { n++; i += s.Length; } return n; }
        return $"{html.Length / 1024} KB, ytInitialData={(html.Contains("ytInitialData") ? "yes" : "no")}, videoRenderer={Count("\"videoRenderer\":{")}, " +
               $"lockupViewModel={Count("\"lockupViewModel\":{")}, consent={(html.Contains("consent.youtube.com") ? "yes" : "no")}";
    }

    private static async Task<List<YtResult>> SearchApiAsync(string query, int max)
    {
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB", clientVersion = "2.20241120.01.00", hl = "en", gl = "US" } },
            query,
            @params = "EgIQAQ%3D%3D",   // videos only
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/search?prettyPrint=false")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Origin", "https://www.youtube.com");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var list = new List<YtResult>();
        Walk(doc.RootElement, list, max);
        return list;
    }

    private static List<YtResult> Parse(string html, int max)
    {
        var start = html.IndexOf("var ytInitialData = ", StringComparison.Ordinal);
        if (start < 0) start = html.IndexOf("ytInitialData = ", StringComparison.Ordinal);
        if (start < 0) return new();
        start = html.IndexOf('{', start);
        var end = html.IndexOf(";</script>", start, StringComparison.Ordinal);
        if (end < 0) return new();
        using var doc = JsonDocument.Parse(html[start..end]);
        var list = new List<YtResult>();
        Walk(doc.RootElement, list, max);
        return list;
    }

    /// <summary>Video length: "lengthText", else the time badge on the thumbnail. "LIVE" / none → live stream.</summary>
    private static string LengthOf(JsonElement v)
    {
        if (v.TryGetProperty("lengthText", out var l))
        {
            if (l.TryGetProperty("simpleText", out var s)) return s.GetString() ?? "";
            if (l.TryGetProperty("runs", out var r) && r.GetArrayLength() > 0 && r[0].TryGetProperty("text", out var rt)) return rt.GetString() ?? "";
        }
        if (v.TryGetProperty("thumbnailOverlays", out var ov) && ov.ValueKind == JsonValueKind.Array)
            foreach (var o in ov.EnumerateArray())
                if (o.TryGetProperty("thumbnailOverlayTimeStatusRenderer", out var ts))
                {
                    if (ts.TryGetProperty("style", out var st) && st.GetString() == "LIVE") return "";
                    var txt = Runs(ts, "text");
                    if (txt.Length > 0 && char.IsDigit(txt[0])) return txt;
                }
        return "";
    }

    private static void Walk(JsonElement e, List<YtResult> list, int max)
    {
        if (list.Count >= max) return;
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("videoRenderer", out var v) && v.TryGetProperty("videoId", out var id))
            {
                var r = new YtResult
                {
                    Id = id.GetString() ?? "",
                    Title = Runs(v, "title"),
                    Channel = Runs(v, "ownerText") is { Length: > 0 } o ? o : Runs(v, "longBylineText"),
                    Length = LengthOf(v),
                    ThumbUrl = $"https://i.ytimg.com/vi/{id.GetString()}/mqdefault.jpg",
                };
                if (r.Id.Length > 0 && r.Length.Length > 0 && list.All(x => x.Id != r.Id)) list.Add(r);   // skip live streams
                return;
            }
            // YouTube's newer layout: "lockupViewModel" (contentId + lockupMetadataViewModel + a time badge on the thumbnail)
            if (e.TryGetProperty("lockupViewModel", out var lv) && lv.TryGetProperty("contentId", out var cid))
            {
                var isVideo = !lv.TryGetProperty("contentType", out var ct) || ct.GetString() == "LOCKUP_CONTENT_TYPE_VIDEO";
                var meta = lv.TryGetProperty("metadata", out var m) && m.TryGetProperty("lockupMetadataViewModel", out var lm) ? lm : default;
                var title = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("title", out var t) && t.TryGetProperty("content", out var tc) ? tc.GetString() ?? "" : "";
                var r = new YtResult
                {
                    Id = cid.GetString() ?? "",
                    Title = title,
                    Channel = meta.ValueKind == JsonValueKind.Object ? FirstMetadataText(meta) : "",
                    Length = BadgeTime(lv),
                    ThumbUrl = $"https://i.ytimg.com/vi/{cid.GetString()}/mqdefault.jpg",
                };
                if (isVideo && r.Id.Length == 11 && r.Title.Length > 0 && r.Length.Length > 0 && list.All(x => x.Id != r.Id)) list.Add(r);
                return;
            }
            foreach (var p in e.EnumerateObject()) Walk(p.Value, list, max);
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) Walk(x, list, max);
    }

    /// <summary>The "3:40" time badge somewhere on a lockup's thumbnail ("" for live streams).</summary>
    private static string BadgeTime(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("thumbnailBadgeViewModel", out var b) && b.TryGetProperty("text", out var tx) &&
                tx.GetString() is { Length: > 0 } s && char.IsDigit(s[0]) && s.Contains(':'))
                return s;
            foreach (var p in e.EnumerateObject())
                if (BadgeTime(p.Value) is { Length: > 0 } found) return found;
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray())
                if (BadgeTime(x) is { Length: > 0 } found) return found;
        return "";
    }

    /// <summary>The channel name: the first text in a lockup's metadata rows.</summary>
    private static string FirstMetadataText(JsonElement meta)
    {
        if (meta.TryGetProperty("metadata", out var md) && md.TryGetProperty("contentMetadataViewModel", out var cm) &&
            cm.TryGetProperty("metadataRows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray())
                if (row.TryGetProperty("metadataParts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var part in parts.EnumerateArray())
                        if (part.TryGetProperty("text", out var t) && t.TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } s)
                            return s;
        return "";
    }

    private static string Runs(JsonElement v, string prop)
    {
        if (!v.TryGetProperty(prop, out var t)) return "";
        if (t.TryGetProperty("runs", out var runs) && runs.GetArrayLength() > 0 && runs[0].TryGetProperty("text", out var tx)) return tx.GetString() ?? "";
        if (t.TryGetProperty("simpleText", out var st)) return st.GetString() ?? "";
        return "";
    }
}

/// <summary>Something besides Windows' media sessions that the Media tab can show and control.</summary>
public interface IExternalPlayer
{
    MediaInfo? Info { get; }
    event Action? Changed;
    Task PlayPauseAsync();
    Task NextAsync();
    Task PreviousAsync();
    Task SeekAsync(TimeSpan to);
    void Stop();
}

/// <summary>
/// Plays YouTube audio inside the notch: the official embedded player in a hidden WebView2 window.
/// Keeps a queue (the search results) and skips videos whose owners don't allow embedding.
/// </summary>
public sealed class YouTubePlayer : IExternalPlayer, IDisposable
{
    public const string AppId = "notch.youtube";
    private Window? _win;
    private WebView2? _web;
    private bool _ready, _initializing;
    private List<YtResult> _queue = new();
    private int _index = -1;
    private (string id, double start)? _pending;
    private double _t, _d;
    private int _state = -1;          // YT: -1 unstarted, 0 ended, 1 playing, 2 paused, 3 buffering, 5 cued
    private string _title = "", _author = "";
    private BitmapSource? _art;
    private Color _accent = Color.FromRgb(0x30, 0xD1, 0x58);
    private DateTimeOffset _timeAt;

    public event Action? Changed;
    /// <summary>QA only: play silently.</summary>
    public bool MutedForTest { get; set; }
    public YtResult? Current => _index >= 0 && _index < _queue.Count ? _queue[_index] : null;
    public bool Active => Current != null;

    public MediaInfo? Info => Current is not { } c ? null : new MediaInfo
    {
        AppId = AppId,
        AppName = "YouTube · notch",
        Title = c.Title,
        Artist = c.Channel,
        IsPlaying = _state is 1 or 3,
        Position = TimeSpan.FromSeconds(_t),
        Duration = TimeSpan.FromSeconds(_d),
        LastUpdated = _timeAt,
        Thumbnail = _art,
        Accent = _art != null ? _accent : Color.FromRgb(0xFF, 0x45, 0x3A),
    };

    public async Task PlayAsync(IReadOnlyList<YtResult> queue, int index)
    {
        foreach (var q in _queue) q.Playing = false;
        _queue = queue.ToList();
        await PlayIndexAsync(index);
    }

    private async Task PlayIndexAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        if (Current != null) Current.Playing = false;
        _index = index;
        var c = _queue[index];
        c.Playing = true;
        _t = 0; _d = 0; _state = 3; _timeAt = DateTimeOffset.Now;
        _art = null;
        LoadArt(c);
        Changed?.Invoke();
        if (_web == null) await InitAsync();
        Send(new { cmd = "load", id = c.Id, start = 0 });
    }

    private void LoadArt(YtResult c)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri($"https://i.ytimg.com/vi/{c.Id}/hqdefault.jpg");
            b.DecodePixelWidth = 320;
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.EndInit();
            void Done()
            {
                if (Current?.Id != c.Id) return;
                // the 4:3 thumbnail has black bars — crop to the middle 16:9 → square-ish art
                try
                {
                    var h = b.PixelHeight; var w = b.PixelWidth;
                    var crop = new CroppedBitmap(b, new Int32Rect((w - h * 3 / 4) / 2, h / 8, h * 3 / 4, h * 3 / 4));
                    crop.Freeze();
                    _art = crop;
                }
                catch { _art = b; }
                _accent = ColorUtil.Dominant(_art);
                Changed?.Invoke();
            }
            if (b.IsDownloading) b.DownloadCompleted += (_, _) => Done(); else Done();
        }
        catch { }
    }

    private async Task InitAsync()
    {
        if (_initializing) return;
        _initializing = true;
        try
        {
            _web = new WebView2();
            _win = new Window
            {
                Title = "Notch player", Width = 320, Height = 180, Left = -32000, Top = -32000,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
                Content = _web,
            };
            _win.Show();
            Native.AddExStyle(new WindowInteropHelper(_win).Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsNotch", "WebView2Player");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, new CoreWebView2EnvironmentOptions(
                "--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling --disable-renderer-backgrounding " +
                "--disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion,HardwareMediaKeyHandling"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("notch.local", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnMessage;
            core.Navigate("https://notch.local/player.html");
        }
        catch (Exception ex) { App.Log("player: " + ex.Message); }
        finally { _initializing = false; }
    }

    private void Send(object msg)
    {
        if (_web?.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); } catch { }
    }

    private async void OnMessage(object? s, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var r = doc.RootElement;
            switch (r.GetProperty("type").GetString())
            {
                case "ready":
                    _ready = true;
                    if (MutedForTest) Mute(true);
                    if (Current != null) Send(new { cmd = "load", id = Current.Id, start = _t });
                    break;
                case "state":
                    var st = r.GetProperty("state").GetInt32();
                    if (st == 0) { await NextAsync(); return; }                  // ended → next in queue
                    _state = st;
                    Changed?.Invoke();
                    break;
                case "time":
                    _t = r.GetProperty("t").GetDouble();
                    _d = r.GetProperty("d").GetDouble();
                    var st2 = r.GetProperty("s").GetInt32();
                    var changed = st2 != _state;
                    _state = st2;
                    _timeAt = DateTimeOffset.Now;
                    if (changed) Changed?.Invoke();
                    break;
                case "error":
                    var code = r.GetProperty("code").GetInt32();
                    App.Log($"player: error {code} on {Current?.Id}");
                    // 101/150: owner doesn't allow playback outside YouTube → skip to the next result
                    if (code is 100 or 101 or 150 or 2 or 5) await NextAsync();
                    break;
            }
        }
        catch (Exception ex) { App.Log("player msg: " + ex.Message); }
    }

    public Task PlayPauseAsync()
    {
        if (!Active) return Task.CompletedTask;
        var playing = _state is 1 or 3;
        Send(new { cmd = playing ? "pause" : "play" });
        _state = playing ? 2 : 1;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task NextAsync() => _index + 1 < _queue.Count ? PlayIndexAsync(_index + 1) : Task.CompletedTask;
    public Task PreviousAsync() => _t > 4 ? SeekAsync(TimeSpan.Zero) : _index > 0 ? PlayIndexAsync(_index - 1) : SeekAsync(TimeSpan.Zero);

    public Task SeekAsync(TimeSpan to)
    {
        Send(new { cmd = "seek", t = to.TotalSeconds });
        _t = to.TotalSeconds;
        _timeAt = DateTimeOffset.Now;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public void Mute(bool on) => Send(new { cmd = "mute", on });
    /// <summary>Player volume 0–100 (the notch turns it down while you talk to it).</summary>
    public void SetVolume(int v) => Send(new { cmd = "volume", v = Math.Clamp(v, 0, 100) });

    public void Stop()
    {
        Send(new { cmd = "stop" });
        if (Current != null) Current.Playing = false;
        _index = -1;
        _state = -1;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        try { Send(new { cmd = "stop" }); } catch { }
        _web?.Dispose();
        _win?.Close();
    }
}
