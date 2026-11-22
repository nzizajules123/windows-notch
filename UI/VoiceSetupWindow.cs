using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WindowsNotch.Services;

namespace WindowsNotch.UI;

/// <summary>
/// Small dark dialog to give the eyes a voice: paste an ElevenLabs API key and voice ID, test it, save.
/// Without a key the notch uses the voice built into Windows.
/// </summary>
public sealed class VoiceSetupWindow : Window
{
    private readonly VoiceService _voice;
    private readonly PasswordBox _key = new();
    private readonly TextBox _voiceId = new();
    private readonly PasswordBox _gemKey = new();
    private readonly TextBox _gemVoice = new();
    private readonly TextBlock _status = new();

    public string? ApiKey { get; private set; }
    public string VoiceId { get; private set; } = VoiceService.DefaultVoiceId;
    public string? GeminiKey { get; private set; }
    public string GeminiVoice { get; private set; } = "Kore";
    public bool TalkOnReminder { get; private set; } = true;
    public bool ProactiveChats { get; private set; } = true;
    public bool WakeWord { get; private set; } = true;
    private readonly CheckBox _remind = new(), _proactive = new(), _wake = new();

    private static readonly Brush Fg = Brushes.White;
    private static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x31));

    public VoiceSetupWindow(VoiceService voice, string? key, string? voiceId, string? geminiKey = null, string? geminiVoice = null, bool talkOnReminder = true,
                            bool proactive = true, bool wakeWord = true, string? assistantName = null)
    {
        _voice = voice;
        Title = "Notch voice";
        Width = 440; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = true;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "🔊  Give the eyes a voice", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Fg });
        panel.Children.Add(new TextBlock
        {
            Text = "Paste an ElevenLabs API key (elevenlabs.io → Profile → API keys) and a voice ID. The key is kept encrypted on this PC only. " +
                   "Leave the key empty to use the voice built into Windows.",
            TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 6, 0, 14),
        });

        panel.Children.Add(Label("ElevenLabs API key"));
        StyleField(_key);
        _key.Password = key ?? "";
        panel.Children.Add(_key);

        panel.Children.Add(Label("Voice ID"));
        StyleField(_voiceId);
        _voiceId.Text = string.IsNullOrWhiteSpace(voiceId) ? VoiceService.DefaultVoiceId : voiceId;
        panel.Children.Add(_voiceId);

        panel.Children.Add(new TextBlock
        {
            Text = "🎙  Live talk (Gemini)", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Fg, Margin = new Thickness(0, 18, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "A Gemini API key (aistudio.google.com → Get API key) lets you have a real spoken conversation with the notch: " +
                   "click the mic in the header, press Ctrl+Alt+T, or clap four times.",
            TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 4, 0, 6),
        });
        panel.Children.Add(Label("Gemini API key"));
        StyleField(_gemKey);
        _gemKey.Password = geminiKey ?? "";
        panel.Children.Add(_gemKey);
        panel.Children.Add(Label("Gemini voice  (Kore, Puck, Aoede, Charon, Fenrir, Leda, Orus, Zephyr…)"));
        StyleField(_gemVoice);
        _gemVoice.Text = string.IsNullOrWhiteSpace(geminiVoice) ? "Kore" : geminiVoice;
        panel.Children.Add(_gemVoice);
        _remind.IsChecked = talkOnReminder;
        _remind.Margin = new Thickness(2, 12, 0, 0);
        _remind.Content = new TextBlock { Text = "Talk to me when a to-do is due (tells me the task and gives ideas)", Foreground = Fg, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_remind);
        foreach (var (box, on, text) in new[]
        {
            (_proactive, proactive, "Let the notch start a chat now and then: news about my hobbies, a bit of history (max 3 a day, 9:00–21:00)"),
            (_wake, wakeWord, $"Wake phrase: say \"Hey {(string.IsNullOrWhiteSpace(assistantName) ? "Notch" : assistantName)}\" to start talking (offline, nothing is recorded)"),
        })
        {
            box.IsChecked = on;
            box.Margin = new Thickness(2, 8, 0, 0);
            box.Content = new TextBlock { Text = text, Foreground = Fg, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(box);
        }

        _status.Foreground = Dim; _status.FontSize = 12; _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(Btn("Test", false, async () =>
        {
            if (string.IsNullOrWhiteSpace(_key.Password)) { _status.Text = "Without a key the Windows voice is used. Paste a key to test ElevenLabs."; return; }
            if (!_key.Password.Trim().StartsWith("sk_")) { _status.Text = "⚠ ElevenLabs keys start with sk_. The long code without it is the key's ID — create a new key and copy the key itself (it's shown only once)."; return; }
            _status.Text = "Testing…";
            var err = await _voice.TestAsync(_key.Password, _voiceId.Text.Trim());
            _status.Text = err == null ? "✓ Works! You should hear it now." : "⚠ " + err;
        }));
        buttons.Children.Add(Btn("Cancel", false, () => { DialogResult = false; return Task.CompletedTask; }));
        buttons.Children.Add(Btn("Save", true, () =>
        {
            ApiKey = string.IsNullOrWhiteSpace(_key.Password) ? null : _key.Password.Trim();
            VoiceId = _voiceId.Text.Trim();
            GeminiKey = string.IsNullOrWhiteSpace(_gemKey.Password) ? null : _gemKey.Password.Trim();
            GeminiVoice = string.IsNullOrWhiteSpace(_gemVoice.Text) ? "Kore" : _gemVoice.Text.Trim();
            TalkOnReminder = _remind.IsChecked == true;
            ProactiveChats = _proactive.IsChecked == true;
            WakeWord = _wake.IsChecked == true;
            DialogResult = true;
            return Task.CompletedTask;
        }));
        panel.Children.Add(buttons);

        Content = new Border
        {
            CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Child = panel,
        };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border or StackPanel) DragMove(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
        Loaded += (_, _) => _key.Focus();
    }

    private static TextBlock Label(string t) =>
        new() { Text = t, Foreground = Dim, FontSize = 11.5, Margin = new Thickness(2, 6, 0, 4) };

    private static void StyleField(Control c)
    {
        c.Background = Field; c.Foreground = Fg; c.BorderThickness = new Thickness(0);
        c.Padding = new Thickness(10, 8, 10, 8); c.FontSize = 13;
        if (c is TextBox tb) tb.CaretBrush = Fg;
        if (c is PasswordBox pb) pb.CaretBrush = Fg;
    }

    private static Button Btn(string text, bool primary, Func<Task> click)
    {
        var b = new Button
        {
            Content = text, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7), FontSize = 13,
            Foreground = primary ? Brushes.Black : Fg, Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
            Background = primary ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
        };
        b.Template = MakeTemplate();
        b.Click += async (_, _) => await click();
        return b;
    }

    private static ControlTemplate MakeTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        return t;
    }
}
