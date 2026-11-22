using System.Globalization;
using System.Speech.Recognition;

namespace WindowsNotch.Services;

/// <summary>
/// "Hey &lt;name&gt;" starts a conversation. Uses Windows' own offline speech recognizer with a tiny grammar
/// (only "hey / hi / okay / hello" + the notch's name), so nothing leaves the PC and ordinary talk isn't transcribed.
/// </summary>
public sealed class WakeWord : IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private string _name = "Notch";
    private DateTime _cooldown;

    /// <summary>The wake phrase was heard (on a background thread).</summary>
    public event Action<string>? Heard;
    /// <summary>Stricter while music plays, so a song can't wake it.</summary>
    public Func<bool>? Strict { get; set; }
    public string Status { get; private set; } = "off";

    public bool Start(string? name)
    {
        if (_engine != null) return true;
        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            var info = installed.FirstOrDefault(r => r.Culture.Equals(CultureInfo.CurrentUICulture))
                       ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
                       ?? installed.FirstOrDefault();
            if (info == null) { Status = "no speech recognizer installed (Settings › Time & language › Speech)"; return false; }
            _engine = new SpeechRecognitionEngine(info);
            _engine.SetInputToDefaultAudioDevice();
            _engine.SpeechRecognized += (_, e) =>
            {
                var need = Strict?.Invoke() == true ? 0.88 : 0.72;
                if (e.Result.Confidence < need || DateTime.UtcNow < _cooldown) return;
                _cooldown = DateTime.UtcNow.AddSeconds(4);
                App.Log($"wake word: \"{e.Result.Text}\" ({e.Result.Confidence:0.00})");
                Heard?.Invoke(e.Result.Text);
            };
            LoadGrammar(name);
            _engine.RecognizeAsync(RecognizeMode.Multiple);
            Status = $"listening for \"Hey {_name}\"";
            App.Log($"wake word: {Status} ({info.Culture})");
            return true;
        }
        catch (Exception ex)
        {
            App.Log("wake word: " + ex.Message);
            Status = "microphone or speech recognizer unavailable";
            _engine?.Dispose(); _engine = null;
            return false;
        }
    }

    /// <summary>The user gave the notch a (new) name: listen for that from now on.</summary>
    public void SetName(string? name)
    {
        if (_engine == null) { _name = Clean(name); return; }
        try
        {
            _engine.RecognizeAsyncCancel();
            _engine.UnloadAllGrammars();
            LoadGrammar(name);
            _engine.RecognizeAsync(RecognizeMode.Multiple);
            Status = $"listening for \"Hey {_name}\"";
        }
        catch (InvalidOperationException)
        {
            // still stopping: try again a moment later
            Task.Delay(400).ContinueWith(_ => { try { LoadGrammar(name); _engine?.RecognizeAsync(RecognizeMode.Multiple); } catch { } });
        }
    }

    private void LoadGrammar(string? name)
    {
        _name = Clean(name);
        var names = new List<string> { _name };
        if (!_name.Equals("Notch", StringComparison.OrdinalIgnoreCase)) names.Add("Notch");
        var phrases = new Choices();
        foreach (var n in names)
            foreach (var hey in new[] { "hey", "hi", "okay", "hello" })
                phrases.Add($"{hey} {n}");
        _engine!.LoadGrammar(new Grammar(new GrammarBuilder(phrases) { Culture = _engine.RecognizerInfo.Culture }) { Name = "wake" });
    }

    private static string Clean(string? n)
    {
        var s = new string((n ?? "").Where(c => char.IsLetter(c) || c == ' ' || c == '-' || c == '\'').ToArray()).Trim();
        return s.Length == 0 ? "Notch" : s;
    }

    public void Stop()
    {
        try { _engine?.RecognizeAsyncCancel(); } catch { }
        _engine?.Dispose();
        _engine = null;
        Status = "off";
    }

    public void Dispose() => Stop();
}
