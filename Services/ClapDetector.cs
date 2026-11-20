using System.Runtime.InteropServices;

namespace WindowsNotch.Services;

/// <summary>
/// Finds hand claps in a stream of 16 kHz mono samples. A clap is a sudden, loud, broadband burst that dies away
/// within ~0.1 s. Speech, music and humming last too long; most knocks and thumps are too dull. Claps that follow
/// each other in a steady rhythm (0.12–0.75 s apart) form a sequence. The sequence is reported once no clap has
/// come for <see cref="EndGapMs"/>.
/// Pure maths with no audio device, so it can be tested with recorded or synthetic sound.
/// </summary>
public sealed class ClapAnalyzer
{
    public const int Rate = 16000;
    private const int Frame = Rate / 100;          // 10 ms frames
    public const int MinGapMs = 120, MaxGapMs = 750, EndGapMs = 800;

    /// <summary>A clap was heard; the argument is how many claps the sequence has so far.</summary>
    public event Action<int>? Clap;
    /// <summary>A sequence ended: (number of claps, milliseconds from first to last clap).</summary>
    public event Action<int, int>? Sequence;
    /// <summary>The level of every frame (0..1). Handy for a "listening" meter or for debugging.</summary>
    public event Action<double>? Level;

    /// <summary>Lower = needs a louder clap. 1 is the default; 0.6 = sensitive; 1.6 = only loud claps.</summary>
    public double Strictness { get; set; } = 1.0;

    private readonly short[] _buf = new short[Frame];
    private int _fill;
    private long _frameNo;                 // 10 ms ticks since start
    private double _floor = 0.003;         // background loudness (RMS)
    private double _prevRms;

    // a burst being checked: is it short enough to be a clap?
    private long _burstStart = -1;
    private double _burstPeakRms, _burstZcr, _burstBase;
    private bool _burstDecayed;

    // the running sequence
    private readonly List<long> _claps = new();

    public void Feed(ReadOnlySpan<short> samples)
    {
        foreach (var s in samples)
        {
            _buf[_fill++] = s;
            if (_fill == Frame) { Analyze(_buf); _fill = 0; }
        }
    }

    private void Analyze(short[] f)
    {
        double sum = 0, peak = 0; int crossings = 0;
        for (int i = 0; i < f.Length; i++)
        {
            var v = f[i] / 32768.0;
            sum += v * v;
            peak = Math.Max(peak, Math.Abs(v));
            if (i > 0 && (f[i] >= 0) != (f[i - 1] >= 0)) crossings++;
        }
        var rms = Math.Sqrt(sum / f.Length);
        var zcr = crossings / (double)f.Length;
        var now = _frameNo++;
        Level?.Invoke(Math.Min(1, rms * 4));

        if (_burstStart >= 0)
        {
            var age = now - _burstStart;
            if (age <= 2)                                   // the burst's first 20 ms: its loudest part
            {
                _burstPeakRms = Math.Max(_burstPeakRms, rms);
                _burstZcr = Math.Max(_burstZcr, zcr);
            }
            // faded: back down near what was there before (talking, music) or far below its own peak
            else if (rms < Math.Max(_burstPeakRms * 0.22, _burstBase * 1.5)) _burstDecayed = true;

            if (_burstDecayed || age >= 14)
            {
                // a clap fades fast (within ~0.12 s) and is bright (lots of zero crossings), unlike a thud or a vowel
                if (_burstDecayed && age <= 13 && _burstZcr >= 0.08) OnClap(_burstStart);
                _burstStart = -1;
            }
        }
        else
        {
            var need = Math.Max(Math.Min(_floor, 0.04) * 6, 0.025) * Strictness;
            if (rms > need && peak > 0.12 * Strictness && rms > _prevRms * 2.5)
            {
                _burstStart = now;
                _burstPeakRms = rms;
                _burstZcr = zcr;
                _burstBase = _prevRms;
                _burstDecayed = false;
            }
            else
            {
                // learn the room's background noise (quickly downwards, slowly upwards)
                var k = rms < _floor ? 0.08 : 0.02;
                _floor = Math.Clamp(_floor + (rms - _floor) * k, 0.0005, 0.2);
            }
        }
        _prevRms = rms;

        if (_claps.Count > 0 && (now - _claps[^1]) * 10 >= EndGapMs) EndSequence();
    }

    private void OnClap(long at)
    {
        if (_claps.Count > 0)
        {
            var gap = (at - _claps[^1]) * 10;
            if (gap < MinGapMs) return;                   // the echo of the same clap
            if (gap > MaxGapMs) _claps.Clear();
            else if (_claps.Count >= 2)
            {
                // claps in a sequence keep a steady beat; a random rattle doesn't
                var prevGap = (_claps[^1] - _claps[^2]) * 10;
                if (gap > prevGap * 2.2 || gap * 2.2 < prevGap) { _claps.Clear(); }
            }
        }
        _claps.Add(at);
        Clap?.Invoke(_claps.Count);
    }

    private void EndSequence()
    {
        var n = _claps.Count;
        var span = (int)((_claps[^1] - _claps[0]) * 10);
        _claps.Clear();
        Sequence?.Invoke(n, span);
    }
}

/// <summary>
/// Listens to the default microphone (16 kHz mono via the classic waveIn API, which needs no extra packages)
/// and feeds the sound to a <see cref="ClapAnalyzer"/>. Nothing is recorded or stored: each 50 ms block is
/// analysed and then reused.
/// </summary>
public sealed class ClapListener : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr h, uint dev, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();

    private const uint WAVE_MAPPER = 0xFFFFFFFF, CALLBACK_EVENT = 0x50000, WHDR_DONE = 1;
    private const int Buffers = 4, BlockSamples = ClapAnalyzer.Rate / 20;   // 50 ms blocks

    public ClapAnalyzer Analyzer { get; } = new();
    /// <summary>"listening", "off", or "error: …".</summary>
    public event Action<string>? StatusChanged;
    public bool Running => _thread != null;

    private Thread? _thread;
    private volatile bool _stop;

    public void Start()
    {
        if (_thread != null) return;
        if (waveInGetNumDevs() == 0) { StatusChanged?.Invoke("error: no microphone found"); return; }
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "clap listener", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        var t = _thread;
        if (t == null) return;
        _stop = true;
        t.Join(1500);
        _thread = null;
        StatusChanged?.Invoke("off");
    }

    private void Run()
    {
        using var ready = new AutoResetEvent(false);
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = ClapAnalyzer.Rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = ClapAnalyzer.Rate * 2 };
        var err = waveInOpen(out var h, WAVE_MAPPER, ref fmt, ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
        if (err != 0)
        {
            _thread = null;
            StatusChanged?.Invoke(err == 4 ? "error: the microphone is busy or blocked" : $"error: couldn't open the microphone ({err})");
            return;
        }

        var hdrSize = Marshal.SizeOf<WAVEHDR>();
        var hdrs = new IntPtr[Buffers];
        var samples = new short[BlockSamples];
        try
        {
            for (int i = 0; i < Buffers; i++)
            {
                hdrs[i] = Marshal.AllocHGlobal(hdrSize);
                var hdr = new WAVEHDR { lpData = Marshal.AllocHGlobal(BlockSamples * 2), dwBufferLength = BlockSamples * 2 };
                Marshal.StructureToPtr(hdr, hdrs[i], false);
                waveInPrepareHeader(h, hdrs[i], hdrSize);
                waveInAddBuffer(h, hdrs[i], hdrSize);
            }
            waveInStart(h);
            StatusChanged?.Invoke("listening");

            while (!_stop)
            {
                ready.WaitOne(200);
                foreach (var p in hdrs)
                {
                    var hdr = Marshal.PtrToStructure<WAVEHDR>(p);
                    if ((hdr.dwFlags & WHDR_DONE) == 0) continue;
                    var n = (int)hdr.dwBytesRecorded / 2;
                    if (n > 0)
                    {
                        Marshal.Copy(hdr.lpData, samples, 0, n);
                        try { Analyzer.Feed(samples.AsSpan(0, n)); } catch (Exception ex) { App.Log(ex); }
                    }
                    if (_stop) break;
                    // hand the block back to the driver
                    waveInUnprepareHeader(h, p, hdrSize);
                    hdr.dwFlags = 0; hdr.dwBytesRecorded = 0;
                    Marshal.StructureToPtr(hdr, p, false);
                    waveInPrepareHeader(h, p, hdrSize);
                    waveInAddBuffer(h, p, hdrSize);
                }
            }
        }
        finally
        {
            waveInReset(h);
            foreach (var p in hdrs)
            {
                if (p == IntPtr.Zero) continue;
                waveInUnprepareHeader(h, p, hdrSize);
                Marshal.FreeHGlobal(Marshal.PtrToStructure<WAVEHDR>(p).lpData);
                Marshal.FreeHGlobal(p);
            }
            waveInClose(h);
        }
    }

    public void Dispose() => Stop();
}
