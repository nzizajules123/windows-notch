namespace WindowsNotch.Services;

/// <summary>A hand-gesture source: the Python/OpenCV engine or the built-in WebView2 one.</summary>
public interface IGestureEngine : IDisposable
{
    event Action<string>? StatusChanged;            // loading | ready | watching | off | error: …
    event Action<string, double, double>? Frame;   // gesture, score, handSize (0..1)
    event Action? PreviewClosed;

    double MinHandSize { get; set; }
    Task StartAsync();
    void Stop();
    void SetPreview(bool on);
}
