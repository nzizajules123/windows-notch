using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace WindowsNotch.Services;

public sealed class MediaInfo
{
    public string AppId { get; init; } = "";
    public string AppName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public bool IsPlaying { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset LastUpdated { get; init; }
    public BitmapSource? Thumbnail { get; set; }
    public Color Accent { get; set; } = Color.FromRgb(0x30, 0xD1, 0x58);

    public bool IsSpotify => AppId.Contains("spotify", StringComparison.OrdinalIgnoreCase);
    public string TrackKey => $"{AppId}|{Title}|{Artist}";

    /// <summary>Position extrapolated from the last timeline update while playing.</summary>
    public TimeSpan LivePosition
    {
        get
        {
            if (!IsPlaying || LastUpdated == default) return Position;
            var p = Position + (DateTimeOffset.Now - LastUpdated);
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }
}

/// <summary>
/// Reads every app that publishes to Windows' System Media Transport Controls
/// (Chrome / Edge / YouTube, Spotify, VLC 4, Windows Media Player, …).
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private bool _busy;
    private string? _pinnedAppId;
    private string? _thumbKey;
    private BitmapSource? _thumb;
    private Color _accent = Color.FromRgb(0x30, 0xD1, 0x58);
    private int _thumbRetries;

    public MediaInfo? Current { get; private set; }
    public IReadOnlyList<string> SessionApps { get; private set; } = Array.Empty<string>();
    public event Action? Changed;

    /// <summary>The notch's own YouTube player; shown instead of Windows' sessions while it's loaded.</summary>
    public IExternalPlayer? External
    {
        get => _external;
        set
        {
            _external = value;
            if (value != null) value.Changed += () => _ = PollAsync();
        }
    }
    private IExternalPlayer? _external;
    private bool ExternalActive => Current?.AppId == YouTubePlayer.AppId && _external?.Info != null;

    public async Task InitAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
        await PollAsync();
    }

    private GlobalSystemMediaTransportControlsSession? Pick()
    {
        if (_mgr == null) return null;
        var sessions = _mgr.GetSessions();
        SessionApps = sessions.Select(s => s.SourceAppUserModelId).ToList();
        if (sessions.Count == 0) return null;

        if (_pinnedAppId != null)
        {
            var pinned = sessions.FirstOrDefault(s => s.SourceAppUserModelId == _pinnedAppId);
            if (pinned != null) return pinned;
            _pinnedAppId = null;
        }

        var playing = sessions.FirstOrDefault(s =>
            s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        return playing ?? _mgr.GetCurrentSession() ?? sessions[0];
    }

    private GlobalSystemMediaTransportControlsSession? CurrentSession =>
        _mgr?.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId == Current?.AppId);

    private async Task PollAsync()
    {
        if (_busy || _mgr == null) return;
        _busy = true;
        try
        {
            var s = Pick();
            // the in-notch player wins while it's playing, or while nothing else is playing
            if (_external?.Info is { } ext &&
                (ext.IsPlaying || s == null || s.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing))
            {
                var extChanged = Current == null || Current.TrackKey != ext.TrackKey || Current.IsPlaying != ext.IsPlaying
                                 || Current.Thumbnail != ext.Thumbnail || Math.Abs((Current.LivePosition - ext.LivePosition).TotalSeconds) > 1.5;
                Current = ext;
                if (extChanged) Changed?.Invoke();
                return;
            }
            if (s == null)
            {
                if (Current != null) { Current = null; Changed?.Invoke(); }
                return;
            }

            var props = await s.TryGetMediaPropertiesAsync();
            var pb = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();

            var info = new MediaInfo
            {
                AppId = s.SourceAppUserModelId,
                AppName = FriendlyAppName(s.SourceAppUserModelId),
                Title = props?.Title ?? "",
                Artist = props?.Artist ?? props?.AlbumArtist ?? "",
                Album = props?.AlbumTitle ?? "",
                IsPlaying = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Position = tl.Position,
                Duration = tl.EndTime - tl.StartTime,
                LastUpdated = tl.LastUpdatedTime,
            };

            // Artwork: reload when the track changes; retry a few times because
            // browsers often publish the artwork a moment after the title.
            if (info.TrackKey != _thumbKey)
            {
                _thumbKey = info.TrackKey;
                _thumb = null;
                _thumbRetries = 0;
            }
            if (_thumb == null && props?.Thumbnail != null && _thumbRetries < 6)
            {
                _thumbRetries++;
                _thumb = await LoadThumbAsync(props.Thumbnail);
                if (_thumb != null) _accent = ColorUtil.Dominant(_thumb);
            }
            info.Thumbnail = _thumb;
            info.Accent = _thumb != null ? _accent : Color.FromRgb(0x30, 0xD1, 0x58);

            var changed = Current == null
                || Current.TrackKey != info.TrackKey
                || Current.IsPlaying != info.IsPlaying
                || Current.Thumbnail != info.Thumbnail
                || Math.Abs((Current.LivePosition - info.LivePosition).TotalSeconds) > 1.5;
            Current = info;
            if (changed) Changed?.Invoke();
        }
        catch (Exception ex) { App.Log("media: " + ex.Message); }
        finally { _busy = false; }
    }

    private static async Task<BitmapSource?> LoadThumbAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var src = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await src.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 256;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public static string FriendlyAppName(string aumid)
    {
        var a = aumid.ToLowerInvariant();
        if (a.Contains("spotify")) return "Spotify";
        if (a.Contains("chrome")) return "Chrome";
        if (a.Contains("msedge")) return "Edge";
        if (a.Contains("firefox") || a == "308046b0af4a39cb") return "Firefox";
        if (a.Contains("brave")) return "Brave";
        if (a.Contains("opera")) return "Opera";
        if (a.Contains("vlc")) return "VLC";
        if (a.Contains("zunemusic") || a.Contains("media")) return "Media Player";
        var name = aumid.Split('!').Last().Split('\\').Last();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length > 14 ? name[..14] : name;
    }

    public void CycleSource()
    {
        // source chip on the notch player = stop it and go back to the system's media
        if (ExternalActive) { _external!.Stop(); _ = PollAsync(); return; }
        var apps = SessionApps;
        if (apps.Count < 2 || Current == null) return;
        var i = apps.ToList().IndexOf(Current.AppId);
        _pinnedAppId = apps[(i + 1) % apps.Count];
        _ = PollAsync();
    }

    public async Task PlayPauseAsync() { if (ExternalActive) { await _external!.PlayPauseAsync(); return; } if (CurrentSession is { } s) await s.TryTogglePlayPauseAsync(); await Task.Delay(150); await PollAsync(); }
    public async Task NextAsync() { if (ExternalActive) { await _external!.NextAsync(); return; } if (CurrentSession is { } s) await s.TrySkipNextAsync(); await Task.Delay(300); await PollAsync(); }
    public async Task PreviousAsync() { if (ExternalActive) { await _external!.PreviousAsync(); return; } if (CurrentSession is { } s) await s.TrySkipPreviousAsync(); await Task.Delay(300); await PollAsync(); }

    public async Task SeekAsync(TimeSpan to)
    {
        if (ExternalActive) { await _external!.SeekAsync(to); return; }
        if (CurrentSession is { } s) await s.TryChangePlaybackPositionAsync(to.Ticks);
        await Task.Delay(200);
        await PollAsync();
    }
}

internal static class ColorUtil
{
    /// <summary>Picks a vivid, readable accent from artwork (weighted by saturation).</summary>
    public static Color Dominant(BitmapSource src)
    {
        try
        {
            var scaled = new TransformedBitmap(src, new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double bb = px[i] / 255.0, gg = px[i + 1] / 255.0, rr = px[i + 2] / 255.0;
                double max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
                double sat = max == 0 ? 0 : (max - min) / max;
                double weight = 0.05 + sat * sat * max;
                r += rr * weight; g += gg * weight; b += bb * weight; total += weight;
            }
            r /= total; g /= total; b /= total;

            // Boost to a vivid, bright tone so it pops on black.
            RgbToHsv(r, g, b, out var hue, out var s, out var v);
            if (s < 0.08) return Color.FromRgb(0xE5, 0xE5, 0xEA); // greyscale art → soft white
            s = Math.Clamp(s * 1.25, 0.45, 0.85);
            v = Math.Max(v, 0.88);
            HsvToRgb(hue, s, v, out r, out g, out b);
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
        catch { return Color.FromRgb(0x30, 0xD1, 0x58); }
    }

    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max; s = max == 0 ? 0 : d / max;
        if (d == 0) { h = 0; return; }
        if (max == r) h = (g - b) / d % 6;
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60; if (h < 0) h += 360;
    }

    private static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        r += m; g += m; b += m;
    }
}
