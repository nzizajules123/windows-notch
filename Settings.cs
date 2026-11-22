using System.IO;
using System.Text.Json;

namespace WindowsNotch;

public sealed class Settings
{
    /// <summary>Camera hand gestures: closed fist = lock, open palm = restart (with countdown).</summary>
    public bool GesturesEnabled { get; set; } = true;

    /// <summary>How big the hand must be in the camera frame (0..1). Higher = hand must be closer.</summary>
    public double MinHandSize { get; set; } = 0.30;
    public bool ShowCameraPreview { get; set; } = true;
    /// <summary>Clap control through the microphone (for PCs whose camera can't see hands well): 👏👏 = lock, 👏👏👏 = restart.</summary>
    public bool ClapsEnabled { get; set; } = false;
    /// <summary>The eyes say hi, react with little speech bubbles and give the odd gentle nudge.</summary>
    public bool EyesTalk { get; set; } = true;
    /// <summary>The eyes speak their words out loud (ElevenLabs voice with a key, else the Windows voice).</summary>
    public bool VoiceEnabled { get; set; } = false;
    /// <summary>ElevenLabs API key, encrypted for this Windows account (DPAPI). Never stored in plain text.</summary>
    public string? VoiceKey { get; set; }
    public string VoiceId { get; set; } = WindowsNotch.Services.VoiceService.DefaultVoiceId;
    public double VoiceVolume { get; set; } = 0.85;
    /// <summary>Gemini API key for live voice talk, encrypted for this Windows account (DPAPI).</summary>
    public string? GeminiKey { get; set; }
    /// <summary>Gemini's voice for live talk (Kore, Puck, Aoede, Charon, Fenrir, Leda, Orus, Zephyr…).</summary>
    public string GeminiVoice { get; set; } = "Kore";
    /// <summary>When a to-do is due, the notch starts a live chat by itself, tells you, and offers ideas (needs a Gemini key).</summary>
    public bool TalkOnReminder { get; set; } = true;
    /// <summary>The notch starts a chat by itself now and then (hobby news, a bit of history); at most 3 a day, 9:00–21:00.</summary>
    public bool ProactiveChats { get; set; } = true;
    /// <summary>Say "Hey <name>" to start a conversation (offline Windows speech recognizer).</summary>
    public bool WakeWord { get; set; } = true;
    /// <summary>Healthy breaks: after BreakEveryMinutes of using the PC, the screen is covered for BreakMinutes (can't be skipped).</summary>
    public bool BreaksEnabled { get; set; } = false;
    public int BreakEveryMinutes { get; set; } = 120;
    public int BreakMinutes { get; set; } = 5;
    /// <summary>A break you asked for at a set time (can't be cancelled once planned), and its length.</summary>
    public DateTime? ScheduledBreakAt { get; set; }
    public int ScheduledBreakMinutes { get; set; } = 5;
    /// <summary>Show the lock-screen-style standby clock after this many idle minutes (0 = never).</summary>
    public int StandbyAfterMinutes { get; set; } = 5;
    /// <summary>Show "Claude is working…" style live activity in the small pill (off: only in the Activity tab).</summary>
    public bool LiveInPill { get; set; } = false;
    /// <summary>Experimental Windows blur behind the island (off: some Windows builds draw it as a square box).</summary>
    public bool SystemBlur { get; set; } = false;
    /// <summary>"auto" (Python if installed, else built-in), "python" or "web".</summary>
    public string GestureEngine { get; set; } = "auto";
    /// <summary>Optional path to a python.exe that has opencv-python + mediapipe.</summary>
    public string? PythonPath { get; set; }

    /// <summary>How long a gesture must be held before it fires.</summary>
    public int HoldMs { get; set; } = 900;

    /// <summary>Seconds to cancel a restart / shutdown before it happens.</summary>
    public int PowerCountdownSec { get; set; } = 5;

    /// <summary>Seconds after the mouse leaves before the expanded notch closes.</summary>
    public double CollapseDelaySec { get; set; } = 3;

    /// <summary>Download new versions from GitHub by themselves and install them when you're away.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Last release we pointed a non-updatable copy to (so it's only announced once).</summary>
    public string? LastAnnouncedUpdate { get; set; }

    public bool LyricsEnabled { get; set; } = true;
    public bool CaptionsEnabled { get; set; } = false;
    public bool HideInFullscreen { get; set; } = true;

    private static string FilePath => Path.Combine(App.DataDir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) { App.Log(ex); }
        var s = new Settings();
        s.Save();
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); }
        catch (Exception ex) { App.Log(ex); }
    }
}
