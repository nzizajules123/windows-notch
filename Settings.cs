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
    /// <summary>The eyes say hi, react with little speech bubbles and give the odd gentle nudge.</summary>
    public bool EyesTalk { get; set; } = true;
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
