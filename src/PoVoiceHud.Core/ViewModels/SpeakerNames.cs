using CommunityToolkit.Mvvm.ComponentModel;
using PoVoiceHud.Core.Audio;

namespace PoVoiceHud.Core.ViewModels;

/// <summary>
/// What the two sides are called on the pages: "You" and "Them" until renamed for a call ("Alex"). Kept while the app
/// runs; the history file keeps the plain sides.
/// </summary>
public sealed partial class SpeakerNames : ObservableObject
{
    [ObservableProperty] private string _you = "You";
    [ObservableProperty] private string _them = "Them";

    /// <summary>The name for <paramref name="source"/>; a blank name falls back to "You" or "Them".</summary>
    public string Of(SourceHint source) => source switch
    {
        SourceHint.Mic => string.IsNullOrWhiteSpace(You) ? source.Speaker() : You.Trim(),
        SourceHint.Teams => string.IsNullOrWhiteSpace(Them) ? source.Speaker() : Them.Trim(),
        _ => source.Speaker(),
    };
}
