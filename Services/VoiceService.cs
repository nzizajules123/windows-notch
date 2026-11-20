using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;

namespace WindowsNotch.Services;

/// <summary>
/// Gives the eyes a voice. With an ElevenLabs key it speaks in the chosen ElevenLabs voice; without one (or if
/// ElevenLabs fails) it falls back to the voice built into Windows.
/// Every phrase is synthesised once and kept in %APPDATA%\WindowsNotch\voice, so the usual greetings cost
/// ElevenLabs credits only the first time. The API key is stored encrypted for this Windows account (DPAPI)
/// and never leaves this PC except to ElevenLabs.
/// </summary>
public sealed class VoiceService
{
    public const string DefaultVoiceId = "7jynqzOvnVP89nA7TB0m";
    private const string Model = "eleven_flash_v2_5";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static string CacheDir => Path.Combine(App.DataDir, "voice");

    private readonly MediaPlayer _player = new();   // created on the UI thread
    private int _token;

    public bool Speaking { get; private set; }
    /// <summary>true when speech starts, false when it ends (UI thread).</summary>
    public event Action<bool>? SpeakingChanged;
    /// <summary>Last ElevenLabs problem ("Key not accepted", "Out of credits"…), cleared on success.</summary>
    public string? LastError { get; private set; }

    public string? ApiKey { get; set; }
    public string VoiceId { get; set; } = DefaultVoiceId;
    public double Volume { get => _player.Volume; set => _player.Volume = Math.Clamp(value, 0, 1); }
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

    public VoiceService()
    {
        _player.Volume = 0.85;
        _player.MediaEnded += (_, _) => Done();
        _player.MediaFailed += (_, e) => { App.Log("voice: playback failed: " + e.ErrorException?.Message); Done(); };
    }

    // ------------------------------------------------------------------ key storage (encrypted for this Windows user)
    public static string? Protect(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) { App.Log("voice: couldn't read the saved key: " + ex.Message); return null; }
    }

    // ------------------------------------------------------------------ speaking
    /// <summary>Says <paramref name="text"/> out loud (emoji are dropped). A newer phrase interrupts an older one.</summary>
    public async Task SpeakAsync(string text)
    {
        var clean = Clean(text);
        if (clean.Length == 0) return;
        var token = ++_token;
        string? file = null;
        try { file = await AudioForAsync(clean); }
        catch (Exception ex) { App.Log("voice: " + ex.Message); }
        if (file == null || token != _token) return;   // a newer phrase came in meanwhile
        _player.Open(new Uri(file));
        _player.Play();
        if (!Speaking) { Speaking = true; SpeakingChanged?.Invoke(true); }
    }

    public void Stop()
    {
        _token++;
        _player.Stop();
        Done();
    }

    private void Done()
    {
        if (!Speaking) return;
        Speaking = false;
        SpeakingChanged?.Invoke(false);
    }

    /// <summary>Checks a key + voice with a tiny phrase. Returns null when it works, else what's wrong.</summary>
    public async Task<string?> TestAsync(string key, string voiceId)
    {
        try
        {
            var bytes = await ElevenLabsAsync(key, voiceId, "Hi! I can talk now.");
            if (bytes == null) return LastError ?? "ElevenLabs didn't answer";
            var f = Path.Combine(CacheDir, "test.mp3");
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllBytesAsync(f, bytes);
            _player.Open(new Uri(f));
            _player.Play();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private async Task<string?> AudioForAsync(string text)
    {
        Directory.CreateDirectory(CacheDir);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
        if (HasKey)
        {
            var path = Path.Combine(CacheDir, $"el-{VoiceId}-{hash}.mp3");
            if (File.Exists(path)) return path;
            var bytes = await ElevenLabsAsync(ApiKey!, VoiceId, text);
            if (bytes != null)
            {
                await File.WriteAllBytesAsync(path, bytes);
                return path;
            }
            // no credits / bad key / offline: the Windows voice still answers
        }
        var wav = Path.Combine(CacheDir, $"win-{hash}.wav");
        if (File.Exists(wav)) return wav;
        return await WindowsVoiceAsync(text, wav) ? wav : null;
    }

    private async Task<byte[]?> ElevenLabsAsync(string key, string voiceId, string text)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=mp3_44100_128");
        req.Headers.Add("xi-api-key", key.Trim());
        req.Headers.Accept.ParseAdd("audio/mpeg");
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            text,
            model_id = Model,
            voice_settings = new { stability = 0.45, similarity_boost = 0.8, style = 0.3, use_speaker_boost = true },
        }), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        if (resp.IsSuccessStatusCode)
        {
            LastError = null;
            return await resp.Content.ReadAsByteArrayAsync();
        }
        var body = await resp.Content.ReadAsStringAsync();
        LastError = (int)resp.StatusCode switch
        {
            400 when body.Contains("api_key_id") => "That's the key ID — use the key that starts with sk_",
            400 or 401 when body.Contains("invalid_api_key") => "ElevenLabs didn't accept the key",
            401 => body.Contains("quota", StringComparison.OrdinalIgnoreCase) ? "Out of ElevenLabs credits" : "ElevenLabs didn't accept the key",
            402 => "Out of ElevenLabs credits",
            404 => "Voice not found",
            429 => "ElevenLabs is busy, try again soon",
            _ => $"ElevenLabs error {(int)resp.StatusCode}",
        };
        App.Log($"voice: ElevenLabs {(int)resp.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
        return null;
    }

    /// <summary>The voice built into Windows (no account needed). Prefers a natural female English voice.</summary>
    private static async Task<bool> WindowsVoiceAsync(string text, string path)
    {
        try
        {
            using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
            var voices = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices;
            var pick = voices.FirstOrDefault(v => v.Language.StartsWith("en") && v.Gender == Windows.Media.SpeechSynthesis.VoiceGender.Female)
                       ?? voices.FirstOrDefault(v => v.Language.StartsWith("en"));
            if (pick != null) synth.Voice = pick;
            using var stream = await synth.SynthesizeTextToStreamAsync(text);
            var buf = new byte[stream.Size];
            using (var input = stream.GetInputStreamAt(0))
            using (var reader = new Windows.Storage.Streams.DataReader(input))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(buf);
            }
            await File.WriteAllBytesAsync(path, buf);
            return true;
        }
        catch (Exception ex) { App.Log("voice: Windows voice failed: " + ex.Message); return false; }
    }

    /// <summary>Drops emoji and symbols so they aren't read out ("smiling face with…").</summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var el = (string)e.Current;
            var c = el[0];
            if (char.IsSurrogate(c)) continue;                                          // 😊 🎶 …
            var cat = char.GetUnicodeCategory(c);
            if (cat is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol) continue;   // ☀ ✨ ❤ ⚡
            if (c is '‍' or '️') continue;
            sb.Append(c == '…' ? "..." : el);
        }
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return s.Any(char.IsLetter) ? s : "";
    }
}
