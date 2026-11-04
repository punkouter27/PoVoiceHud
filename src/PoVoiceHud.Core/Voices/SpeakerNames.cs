using CommunityToolkit.Mvvm.ComponentModel;
using PoVoiceHud.Core.Audio;


namespace PoVoiceHud.Core.Voices;

/// <summary>
/// What the two sides are called on the pages: "You" and "Them" until renamed for a call ("Alex"). With the call's voices
/// told apart, the call side takes a list ("Alex, Sam"): the first voice heard is Alex, the second Sam, and any after that
/// "Them 3". Kept while the app runs; the history file keeps the plain sides and the voice numbers.
/// </summary>
public sealed partial class SpeakerNames : ObservableObject
{
    [ObservableProperty] private string _you = "You";
    [ObservableProperty] private string _them = "Them";

    /// <summary>The name for <paramref name="source"/> and, on the call, <paramref name="voice"/>; a blank name falls back to "You" or "Them".</summary>
    public string Of(SourceHint source, int voice = 0) => source switch
    {
        SourceHint.Mic => string.IsNullOrWhiteSpace(You) ? source.Speaker() : You.Trim(),
        SourceHint.Teams => OfCall(Them, voice),
        _ => source.Speaker(),
    };

    /// <summary>"Them" (or the name given) for one voice; for the others the name in its place in the list, else "Them 2".</summary>
    public static string OfCall(string? names, int voice)
    {
        var listed = (names ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (listed.Length == 0) listed = [SourceHint.Teams.Speaker()];
        if (voice <= 0) return string.Join(", ", listed);
        if (voice <= listed.Length) return listed[voice - 1];
        return $"{SourceHint.Teams.Speaker()} {voice}";
    }
}
