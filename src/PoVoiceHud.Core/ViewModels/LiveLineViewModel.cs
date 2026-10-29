using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Providers;

namespace PoVoiceHud.Core.ViewModels;

/// <summary>Where a line of the Live page is: just heard, being checked, judged, or (a question about the code) answered.</summary>
public enum LineState { Heard, Checking, Correct, Incorrect, CantTell, Answered }

/// <summary>One line of the Live page: what was said, highlighted by its verdict once there is one.</summary>
public sealed partial class LiveLineViewModel(TranscriptLine line, SpeakerNames? names = null) : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Note), nameof(HasNote))] private LineState _state;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Note))] private string _reason = "";

    public TranscriptLine Line => line;
    public string Text => line.Text;
    /// <summary>The clock time it was said, in the user's short time format (as on the Facts page).</summary>
    public string Stamp => line.At.ToString("t", CultureInfo.CurrentCulture);
    /// <summary>"You:" (the microphone) or "Them:" (the call), or the names given to them.</summary>
    public string Speaker => $"{names?.Of(line.Source) ?? line.Source.Speaker()}:";

    /// <summary>The sides were renamed.</summary>
    internal void Renamed() => OnPropertyChanged(nameof(Speaker));

    /// <summary>The line under the sentence: "Checking…", the verdict in plain words and its reason, or the answer from the code.</summary>
    public string Note => State switch
    {
        LineState.Checking => "Checking…",
        LineState.Correct => $"{Verdict.Supported.Label()} — {Reason}",
        LineState.Incorrect => $"{Verdict.Contradicted.Label()} — {Reason}",
        LineState.CantTell => $"{Verdict.Unverified.Label()} — {Reason}",
        LineState.Answered => $"Answer — {Reason}",
        _ => "",
    };

    public bool HasNote => State != LineState.Heard;

    internal void Judge(Insight verdict) => Judge(verdict.Verdict, verdict.Reason);

    /// <summary>The code's answer to a question, or its verdict on a statement.</summary>
    internal void Judge(Repos.CodeFact fact)
    {
        if (fact.Verdict is { } verdict) Judge(verdict, fact.Text);
        else (Reason, State) = (fact.Text, LineState.Answered);
    }

    private void Judge(Verdict verdict, string reason)
    {
        Reason = reason;
        State = verdict switch
        {
            Verdict.Supported => LineState.Correct,
            Verdict.Contradicted => LineState.Incorrect,
            _ => LineState.CantTell,
        };
    }
}
