using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WindowsNotch.Services;

/// <summary>
/// Live voice conversation with Gemini (the Live API, speech to speech): the microphone streams to Gemini, which
/// answers out loud through the speakers, with live captions for both sides. Talk normally; Gemini notices when you
/// stop. While it speaks the microphone is muted (half-duplex), so it doesn't hear and answer itself through the
/// speakers. The session ends by itself after a while of silence.
/// </summary>
public sealed class LiveTalkService
{
    public enum TalkState { Off, Connecting, Listening, Speaking }

    private static readonly string[] PreferredModels =
        { "gemini-3.1-flash-live-preview", "gemini-2.5-flash-native-audio-latest", "gemini-2.5-flash-native-audio-preview-12-2025" };
    private static string? _model;
    private const int MicRate = 16000, SpeakerRate = 24000;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(75);

    public string? ApiKey { get; set; }
    public string VoiceName { get; set; } = "Kore";
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);
    public TalkState State { get; private set; } = TalkState.Off;
    public bool Active => State != TalkState.Off;

    /// <summary>State changes, from any thread. The text is an error message when a session fails.</summary>
    public event Action<TalkState, string?>? StateChanged;
    /// <summary>What Gemini is saying (caption, grows as it speaks).</summary>
    public event Action<string>? ModelText;
    /// <summary>What you said (caption).</summary>
    public event Action<string>? UserText;

    /// <summary>Actions Gemini may call while you talk (Gemini "functionDeclarations"). Null = talk only.</summary>
    public object[]? Tools { get; set; }
    /// <summary>Runs an action Gemini asked for: (name, args) → (what happened, end the conversation after the reply).</summary>
    public Func<string, JsonElement, Task<(string result, bool endAfterReply)>>? ToolHandler { get; set; }
    private bool _endAfterReply;
    /// <summary>QA: a typed first message instead of the greeting (tests actions without speaking).</summary>
    public string? FirstMessage { get; set; }
    private Queue<string>? _scripted;
    /// <summary>True while music plays on the PC: the conversation then stays open longer and only clear speech gets through.</summary>
    public Func<bool>? MusicPlaying { get; set; }
    /// <summary>Keep the chat open however quiet it gets (e.g. during a break, so the notch can keep you company).</summary>
    public bool KeepAlive { get; set; }
    /// <summary>Let Gemini search Google (fresh news about your hobbies). Dropped automatically if the key's plan doesn't allow it.</summary>
    public bool UseSearch { get; set; } = true;
    private static bool _searchBlocked;
    private bool _searchThisTime, _heardModel;
    /// <summary>One finished exchange (what you said, what the notch answered), for its memory of earlier chats.</summary>
    public event Action<string, string>? Exchange;

    private string _prompt = "";
    private string? _opening;
    private string? _resumeHandle;
    private bool _resuming;
    private string _turnUser = "";
    // music gate: background level of the mic while music plays, and how long the gate stays open after you speak
    private double _musicFloor = 800;
    private DateTime _gateOpenUntil;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private MicStream? _mic;
    private WaveOutStream? _speaker;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private DateTime _lastActivity, _mutedUntil;
    private readonly StringBuilder _modelLine = new(), _userLine = new();

    private void Set(TalkState s, string? msg = null)
    {
        if (State == s && msg == null) return;
        State = s;
        StateChanged?.Invoke(s, msg);
    }

    /// <param name="opening">what the notch should start with (e.g. a to-do that is due), instead of a greeting</param>
    public async Task StartAsync(string systemPrompt, string? opening = null)
    {
        if (Active || !HasKey) return;
        Set(TalkState.Connecting);
        _prompt = systemPrompt;
        _opening = opening;
        _scripted = null;
        _resumeHandle = null;
        await ConnectAsync(null);
    }

    /// <summary>Gemini ends each connection after ~10 minutes: carry on in a new one, with the conversation remembered.</summary>
    private async Task ResumeAsync()
    {
        var handle = _resumeHandle;
        if (handle == null) { Stop("session time limit"); return; }
        App.Log("live talk: reconnecting (session continues)");
        Cleanup();
        Set(TalkState.Connecting);
        await ConnectAsync(handle);
    }

    private async Task ConnectAsync(string? resumeHandle)
    {
        _resuming = resumeHandle != null;
        _heardModel = false;
        _cts = new CancellationTokenSource();
        try
        {
            var model = _model ??= await PickModelAsync(ApiKey!);
            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("x-goog-api-key", ApiKey!.Trim());
            await _ws.ConnectAsync(new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"), _cts.Token);
            await SendAsync(new
            {
                setup = new
                {
                    model = "models/" + model,
                    generationConfig = new
                    {
                        responseModalities = new[] { "AUDIO" },
                        speechConfig = new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = VoiceName } } },
                    },
                    systemInstruction = new { parts = new[] { new { text = _prompt } } },
                    // long chats: older turns are summarised away instead of hitting the length limit
                    contextWindowCompression = new { slidingWindow = new { } },
                    sessionResumption = resumeHandle == null ? (object)new { } : new { handle = resumeHandle },
                    inputAudioTranscription = new { },
                    outputAudioTranscription = new { },
                    tools = BuildTools(),
                },
            }, ignoreNulls: true);
            _endAfterReply = false;
            App.Log($"live talk: connected ({model})");
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            App.Log("live talk: " + ex.Message);
            Cleanup();
            Set(TalkState.Off, ex is WebSocketException ? "Couldn't reach Gemini — check the key and internet" : "Couldn't start: " + ex.Message);
        }
    }

    public void Stop(string? why = null)
    {
        if (!Active) return;
        App.Log("live talk: stopped" + (why != null ? $" ({why})" : ""));
        Cleanup();
        Set(TalkState.Off, null);
    }

    private void Cleanup()
    {
        try { _cts?.Cancel(); } catch { }
        _mic?.Dispose(); _mic = null;
        _speaker?.Dispose(); _speaker = null;
        var ws = _ws; _ws = null;
        if (ws != null)
        {
            _ = Task.Run(async () =>
            {
                try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
                ws.Dispose();
            });
        }
    }

    private static async Task<string> PickModelAsync(string key)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Add("x-goog-api-key", key.Trim());
            using var doc = JsonDocument.Parse(await http.GetStringAsync("https://generativelanguage.googleapis.com/v1beta/models?pageSize=200"));
            var live = doc.RootElement.GetProperty("models").EnumerateArray()
                .Where(m => m.TryGetProperty("supportedGenerationMethods", out var g) && g.EnumerateArray().Any(x => x.GetString() == "bidiGenerateContent"))
                .Select(m => m.GetProperty("name").GetString()!.Replace("models/", ""))
                .ToHashSet();
            foreach (var p in PreferredModels) if (live.Contains(p)) return p;
            return live.FirstOrDefault(n => n.Contains("native-audio")) ?? live.FirstOrDefault(n => n.Contains("live") && !n.Contains("translate") && !n.Contains("transcribe")) ?? PreferredModels[0];
        }
        catch { return PreferredModels[0]; }
    }

    private object[]? BuildTools()
    {
        var list = new List<object>();
        _searchThisTime = UseSearch && !_searchBlocked;
        if (_searchThisTime) list.Add(new { googleSearch = new { } });
        if (Tools != null) list.Add(new { functionDeclarations = Tools });
        return list.Count == 0 ? null : list.ToArray();
    }

    private static readonly JsonSerializerOptions SkipNulls = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private async Task SendAsync(object msg, bool ignoreNulls = false)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(ignoreNulls ? JsonSerializer.Serialize(msg, SkipNulls) : JsonSerializer.Serialize(msg));
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? default); }
        catch { }
        finally { _sendLock.Release(); }
    }

    private void StartAudio()
    {
        _speaker = new WaveOutStream(SpeakerRate);
        _mic = new MicStream(MicRate, 1600);   // 100 ms blocks
        var silence = new short[1600];
        _mic.Block += block =>
        {
            // half-duplex: while the notch talks (and a moment after), send silence so it doesn't hear itself
            var talking = _speaker?.Playing == true || DateTime.UtcNow < _mutedUntil;
            var music = MusicPlaying?.Invoke() == true;
            var src = talking ? silence : block;
            if (!talking && music)
            {
                // music from the speakers reaches the mic too: only let clearly louder sound (you, talking) through,
                // and keep the gate open a moment so whole sentences pass
                var rms = Rms(block);
                if (rms > Math.Max(1400, _musicFloor * 2.2)) _gateOpenUntil = DateTime.UtcNow.AddMilliseconds(1300);
                else _musicFloor += (rms - _musicFloor) * (rms < _musicFloor ? 0.2 : 0.05);
                if (DateTime.UtcNow >= _gateOpenUntil) src = silence;
            }
            var bytes = new byte[src.Length * 2];
            Buffer.BlockCopy(src, 0, bytes, 0, bytes.Length);
            _ = SendAsync(new { realtimeInput = new { audio = new { data = Convert.ToBase64String(bytes), mimeType = "audio/pcm;rate=16000" } } });
            if (!talking && src != silence && Loud(block)) _lastActivity = DateTime.UtcNow;
            // stays open while music plays, so you can say "stop it" or carry on talking
            var limit = music ? TimeSpan.FromMinutes(15) : IdleLimit;
            if (!KeepAlive && DateTime.UtcNow - _lastActivity > limit) Stop("quiet for a while");
        };
        _speaker.Drained += () =>
        {
            _mutedUntil = DateTime.UtcNow.AddMilliseconds(350);
            if (State == TalkState.Speaking) Set(TalkState.Listening);
        };
        var err = _mic.Start();
        if (err != null) { Cleanup(); Set(TalkState.Off, err); }
    }

    /// <summary>Runs each action Gemini asked for and sends back what happened, so it can tell you.</summary>
    private async Task RunToolsAsync(JsonElement calls)
    {
        var responses = new List<object>();
        foreach (var fc in calls.EnumerateArray())
        {
            var name = fc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var id = fc.TryGetProperty("id", out var i) ? i.GetString() : null;
            var args = fc.TryGetProperty("args", out var a) ? a : default;
            string result;
            try
            {
                if (ToolHandler == null) result = "This action isn't available.";
                else
                {
                    var (r, end) = await ToolHandler(name, args);
                    result = r;
                    if (end) _endAfterReply = true;
                }
            }
            catch (Exception ex) { result = "That didn't work: " + ex.Message; }
            App.Log($"live talk: action {name} → {result}");
            responses.Add(new { id, name, response = new { result } });
        }
        _lastActivity = DateTime.UtcNow;
        await SendAsync(new { toolResponse = new { functionResponses = responses } }, ignoreNulls: true);
    }

    /// <summary>QA: "first || second || third" → the first now, the rest one per reply.</summary>
    private string? NextScripted()
    {
        if (FirstMessage == null) return null;
        _scripted ??= new Queue<string>(FirstMessage.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (_scripted.Count == 0) return null;
        var first = _scripted.Dequeue();
        _turnUser = first;
        return first;
    }

    /// <summary>Tells Gemini something mid-conversation (e.g. a to-do just came due); it answers out loud.</summary>
    public Task SayToModelAsync(string text) =>
        Active ? SendAsync(new { clientContent = new { turns = new[] { new { role = "user", parts = new[] { new { text } } } }, turnComplete = true } }) : Task.CompletedTask;

    private static double Rms(short[] b)
    {
        double sum = 0;
        foreach (var s in b) sum += (double)s * s;
        return Math.Sqrt(sum / b.Length);
    }

    private static bool Loud(short[] b) => Rms(b) > 600;   // someone speaking near the mic

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[1 << 16];
        try
        {
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open } ws)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do { r = await ws.ReceiveAsync(buf, ct); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    var why = ws.CloseStatusDescription;
                    // Google Search isn't allowed on this key (quota / plan): carry on without it
                    if (_searchThisTime && !_heardModel && why != null &&
                        (why.Contains("quota", StringComparison.OrdinalIgnoreCase) || why.Contains("search", StringComparison.OrdinalIgnoreCase) || why.Contains("ground", StringComparison.OrdinalIgnoreCase)))
                    {
                        App.Log("live talk: Google Search not available on this key — continuing without it");
                        _searchBlocked = true;
                        Cleanup();
                        Set(TalkState.Connecting);
                        _ = Task.Run(() => ConnectAsync(null));
                        return;
                    }
                    App.Log($"live talk: closed by Gemini ({ws.CloseStatus} {why})");
                    Cleanup();
                    Set(TalkState.Off, string.IsNullOrWhiteSpace(why) || ws.CloseStatus == WebSocketCloseStatus.NormalClosure ? null : "Gemini: " + why);
                    return;
                }
                Handle(ms.ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Active) { App.Log("live talk: " + ex.Message); Cleanup(); Set(TalkState.Off, "Connection lost"); }
        }
    }

    private void Handle(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.TryGetProperty("setupComplete", out _))
        {
            _lastActivity = DateTime.UtcNow;
            StartAudio();
            if (Active) Set(TalkState.Listening);
            if (_resuming) return;   // carrying on an existing conversation: no new greeting
            // say hi first, so you know it's listening
            _ = SendAsync(new { clientContent = new { turns = new[] { new { role = "user", parts = new[] { new { text = _opening ?? NextScripted() ?? "(The user just opened a live voice chat with you. Greet them in a few words.)" } } } }, turnComplete = true } });
            return;
        }
        if (root.TryGetProperty("sessionResumptionUpdate", out var sru))
        {
            if (sru.TryGetProperty("newHandle", out var nh) && nh.ValueKind == JsonValueKind.String &&
                (!sru.TryGetProperty("resumable", out var rs) || rs.ValueKind != JsonValueKind.False))
                _resumeHandle = nh.GetString();
            return;
        }
        if (root.TryGetProperty("goAway", out _)) { _ = ResumeAsync(); return; }
        if (root.TryGetProperty("toolCall", out var call) && call.TryGetProperty("functionCalls", out var calls))
        {
            _ = RunToolsAsync(calls.Clone());
            return;
        }
        if (!root.TryGetProperty("serverContent", out var sc)) return;

        if (sc.TryGetProperty("interrupted", out var intr) && intr.GetBoolean())
        {
            _speaker?.Clear();
            _modelLine.Clear();
            Set(TalkState.Listening);
        }
        if (sc.TryGetProperty("inputTranscription", out var it) && it.TryGetProperty("text", out var ut))
        {
            if (_modelLine.Length > 0) { _modelLine.Clear(); _userLine.Clear(); }
            _userLine.Append(ut.GetString());
            _lastActivity = DateTime.UtcNow;
            UserText?.Invoke(_userLine.ToString().Trim());
        }
        if (sc.TryGetProperty("modelTurn", out var mt) && mt.TryGetProperty("parts", out var parts))
        {
            foreach (var p in parts.EnumerateArray())
            {
                if (!p.TryGetProperty("inlineData", out var inl) || !inl.TryGetProperty("data", out var d)) continue;
                _speaker?.Write(Convert.FromBase64String(d.GetString()!));
                _heardModel = true;
                _lastActivity = DateTime.UtcNow;
                if (State == TalkState.Listening) Set(TalkState.Speaking);
            }
        }
        if (sc.TryGetProperty("outputTranscription", out var ot) && ot.TryGetProperty("text", out var mtx))
        {
            if (_modelLine.Length == 0) { _turnUser = _userLine.ToString().Trim(); _userLine.Clear(); }
            _modelLine.Append(mtx.GetString());
            ModelText?.Invoke(_modelLine.ToString().Trim());
        }
        if (sc.TryGetProperty("turnComplete", out var tc) && tc.GetBoolean())
        {
            // QA: the next typed request, a few seconds after the reply
            if (_scripted is { Count: > 0 })
            {
                var next = _scripted.Dequeue();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(6000);
                    if (!Active) return;
                    _turnUser = next;
                    await SendAsync(new { clientContent = new { turns = new[] { new { role = "user", parts = new[] { new { text = next } } } }, turnComplete = true } });
                });
            }
            var said = _modelLine.ToString().Trim();
            if (said.Length > 0 && _turnUser.Length > 0) Exchange?.Invoke(_turnUser, said);
            _turnUser = "";
            _modelLine.Clear();
            // e.g. music just started: finish the sentence, then hang up so Gemini doesn't listen to the song
            if (_endAfterReply)
            {
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 100 && _speaker?.Playing == true; i++) await Task.Delay(100);
                    Stop("action done");
                });
            }
        }
    }
}

/// <summary>Default microphone as 16-bit mono blocks (classic waveIn, no extra packages).</summary>
public sealed class MicStream : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)] private struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr h, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr h);

    private readonly int _rate, _block;
    private Thread? _thread;
    private volatile bool _stop;
    private string? _error;
    private readonly ManualResetEventSlim _opened = new();
    /// <summary>Each recorded block (on the audio thread).</summary>
    public event Action<short[]>? Block;

    public MicStream(int rate, int blockSamples) { _rate = rate; _block = blockSamples; }

    /// <summary>Starts recording; returns an error text or null.</summary>
    public string? Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "live mic" };
        _thread.Start();
        _opened.Wait(3000);
        return _error;
    }

    private void Run()
    {
        using var ready = new AutoResetEvent(false);
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)_rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = (uint)_rate * 2 };
        if (waveInOpen(out var h, 0xFFFFFFFF, ref fmt, ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000) != 0)
        {
            _error = "Couldn't open the microphone";
            _opened.Set();
            return;
        }
        var size = Marshal.SizeOf<WAVEHDR>();
        var hdrs = new IntPtr[4];
        try
        {
            for (int i = 0; i < hdrs.Length; i++)
            {
                hdrs[i] = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(new WAVEHDR { lpData = Marshal.AllocHGlobal(_block * 2), dwBufferLength = (uint)_block * 2 }, hdrs[i], false);
                waveInPrepareHeader(h, hdrs[i], size);
                waveInAddBuffer(h, hdrs[i], size);
            }
            waveInStart(h);
            _opened.Set();
            while (!_stop)
            {
                ready.WaitOne(200);
                foreach (var p in hdrs)
                {
                    var hdr = Marshal.PtrToStructure<WAVEHDR>(p);
                    if ((hdr.dwFlags & 1) == 0) continue;
                    var n = (int)hdr.dwBytesRecorded / 2;
                    if (n > 0 && !_stop)
                    {
                        var s = new short[n];
                        Marshal.Copy(hdr.lpData, s, 0, n);
                        try { Block?.Invoke(s); } catch (Exception ex) { App.Log(ex); }
                    }
                    if (_stop) break;
                    waveInUnprepareHeader(h, p, size);
                    hdr.dwFlags = 0; hdr.dwBytesRecorded = 0;
                    Marshal.StructureToPtr(hdr, p, false);
                    waveInPrepareHeader(h, p, size);
                    waveInAddBuffer(h, p, size);
                }
            }
        }
        finally
        {
            waveInReset(h);
            foreach (var p in hdrs)
            {
                if (p == IntPtr.Zero) continue;
                waveInUnprepareHeader(h, p, size);
                Marshal.FreeHGlobal(Marshal.PtrToStructure<WAVEHDR>(p).lpData);
                Marshal.FreeHGlobal(p);
            }
            waveInClose(h);
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread != null && Thread.CurrentThread != _thread) _thread.Join(1000);
        _thread = null;
    }
}

/// <summary>Plays a stream of 16-bit mono PCM chunks as they arrive (classic waveOut). <see cref="Clear"/> cuts it off.</summary>
public sealed class WaveOutStream : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)] private struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr h, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr h);

    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly List<IntPtr> _inFlight = new();
    private readonly AutoResetEvent _ready = new(false);
    private readonly Thread _thread;
    private volatile bool _stop, _clear;
    private IntPtr _h;
    private static readonly int HdrSize = Marshal.SizeOf<WAVEHDR>();

    /// <summary>Raised when everything queued has been played (audio thread).</summary>
    public event Action? Drained;
    public bool Playing => !_queue.IsEmpty || _inFlightCount > 0;
    private volatile int _inFlightCount;

    public WaveOutStream(int rate)
    {
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = (uint)rate * 2 };
        if (waveOutOpen(out _h, 0xFFFFFFFF, ref fmt, _ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000) != 0)
            throw new InvalidOperationException("Couldn't open the speakers");
        _thread = new Thread(Run) { IsBackground = true, Name = "live speaker", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Write(byte[] pcm) { if (pcm.Length > 0) { _queue.Enqueue(pcm); _ready.Set(); } }

    /// <summary>Stops what's playing and drops what's queued (you interrupted).</summary>
    public void Clear() { _clear = true; _ready.Set(); }

    private void Run()
    {
        while (!_stop)
        {
            _ready.WaitOne(100);
            if (_clear)
            {
                _clear = false;
                while (_queue.TryDequeue(out _)) { }
                waveOutReset(_h);
            }
            // hand back finished blocks
            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                var p = _inFlight[i];
                var hdr = Marshal.PtrToStructure<WAVEHDR>(p);
                if ((hdr.dwFlags & 1) == 0) continue;   // WHDR_DONE
                waveOutUnprepareHeader(_h, p, HdrSize);
                Marshal.FreeHGlobal(hdr.lpData);
                Marshal.FreeHGlobal(p);
                _inFlight.RemoveAt(i);
                _inFlightCount = _inFlight.Count;
                if (_inFlight.Count == 0 && _queue.IsEmpty) Drained?.Invoke();
            }
            // queue new ones
            while (_queue.TryDequeue(out var pcm))
            {
                var data = Marshal.AllocHGlobal(pcm.Length);
                Marshal.Copy(pcm, 0, data, pcm.Length);
                var p = Marshal.AllocHGlobal(HdrSize);
                Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = (uint)pcm.Length }, p, false);
                waveOutPrepareHeader(_h, p, HdrSize);
                waveOutWrite(_h, p, HdrSize);
                _inFlight.Add(p);
                _inFlightCount = _inFlight.Count;
            }
        }
        waveOutReset(_h);
        foreach (var p in _inFlight)
        {
            waveOutUnprepareHeader(_h, p, HdrSize);
            Marshal.FreeHGlobal(Marshal.PtrToStructure<WAVEHDR>(p).lpData);
            Marshal.FreeHGlobal(p);
        }
        waveOutClose(_h);
    }

    public void Dispose()
    {
        _stop = true;
        _ready.Set();
        if (Thread.CurrentThread != _thread) _thread.Join(1000);
    }
}
