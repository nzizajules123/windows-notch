using System.IO;
using System.Text.Json;

namespace WindowsNotch.Services;

/// <summary>
/// Who you are to the notch: your name, the name you gave it, and what you like doing. Learned in conversation
/// ("what's your name?", "what should I call you?", "what are your hobbies?") and kept only on this PC,
/// in %APPDATA%\WindowsNotch\profile.json.
/// </summary>
public sealed class UserProfile
{
    public string? UserName { get; set; }
    public string? AssistantName { get; set; }
    public List<string> Interests { get; set; } = new();
    /// <summary>When the notch last started a chat by itself, and how many it started that day.</summary>
    public DateTime LastProactive { get; set; }
    public int ProactiveToday { get; set; }
    public DateTime ProactiveDay { get; set; }

    public bool Introduced => !string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(AssistantName);

    private static string FilePath => Path.Combine(App.DataDir, "profile.json");

    public static UserProfile Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<UserProfile>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { App.Log("profile: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { App.Log("profile: " + ex.Message); }
    }

    /// <summary>What Gemini should know about you at the start of every chat.</summary>
    public string ForPrompt()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(AssistantName))
            parts.Add($"Your name is {AssistantName} — the user chose it for you, so answer to it happily.");
        if (!string.IsNullOrWhiteSpace(UserName))
            parts.Add($"The user's name is {UserName}. Call them by name now and then (naturally, not in every sentence).");
        if (Interests.Count > 0)
            parts.Add("They like: " + string.Join(", ", Interests) + ". Bring these up when it fits.");
        if (!Introduced || Interests.Count == 0)
            parts.Add("You don't know everything about them yet. Early in this chat, warmly " +
                      (string.IsNullOrWhiteSpace(UserName) ? "ask their name, " : "") +
                      (string.IsNullOrWhiteSpace(AssistantName) ? "ask them to give you a name (you'd love one), " : "") +
                      (Interests.Count == 0 ? "and ask what they like doing — their hobbies, " : "") +
                      "one question at a time, and save each answer with remember_profile straight away.");
        parts.Add("Whenever they mention a new hobby or interest, or want to change a name, save it with remember_profile.");
        return string.Join(" ", parts);
    }
}
