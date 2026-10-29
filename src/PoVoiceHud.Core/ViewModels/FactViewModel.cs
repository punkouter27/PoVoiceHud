using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Providers;

namespace PoVoiceHud.Core.ViewModels;

/// <summary>One row of the Facts page: a claim the model found correct or incorrect, its reason and its source.</summary>
public sealed partial class FactViewModel : ObservableObject
{
    private readonly Insight _insight;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title))] private int _count = 1;
    /// <summary>When it was last said, as a clock time: a repeat moves the row to the top, so it shows the repeat's time.</summary>
    [ObservableProperty] private string _time;

    private readonly SpeakerNames? _names;

    public FactViewModel(Insight insight, DateTimeOffset at, SourceHint source, Func<Uri, Task> openUri, SpeakerNames? names = null)
    {
        _insight = insight;
        _names = names;
        _time = Clock(at);
        Source = source;
        OpenLinkCommand = new AsyncRelayCommand(() => insight.Link is null ? Task.CompletedTask : openUri(insight.Link));
    }

    public SourceHint Source { get; }

    /// <summary>How sure the model was, 0–1: the thin bar under the reason.</summary>
    public double Confidence => _insight.Confidence;

    /// <summary>"Model confidence: 85%".</summary>
    public string ConfidenceText => $"Model confidence: {Confidence:P0}";

    /// <summary>The sides were renamed.</summary>
    internal void Renamed() => OnPropertyChanged(nameof(Speaker));

    public string Claim => _insight.Claim;
    public Verdict Verdict => _insight.Verdict;
    public bool IsCorrect => Verdict == Verdict.Supported;
    public string Label => Verdict.Label();
    public string Reason => _insight.Reason;
    /// <summary>The claim, and how many times it was said when more than once.</summary>
    public string Title => Claim + (Count > 1 ? $" ×{Count}" : "");
    /// <summary>"You" or "Them", or the names given to them.</summary>
    public string Speaker => _names?.Of(Source) ?? Source.Speaker();
    public bool HasLink => _insight.Link is not null;

    /// <summary>
    /// Shows where the link goes. A page the app looked up itself is a checked source; a link the model wrote can be
    /// invented, so it is labelled as the model's citation.
    /// </summary>
    public string LinkLabel => _insight.Link is not { } link ? "" : $"{Where(link)} ↗ ({(_insight.LinkChecked ? "looked up" : "cited by the model")})";

    /// <summary>A file (on disk, on GitHub or in Azure DevOps) reads as its name; any other page as its site. Code facts use it too.</summary>
    internal static string Where(Uri link)
    {
        var azdoPath = link.Query.IndexOf("path=", StringComparison.Ordinal) is var at and >= 0 ? Uri.UnescapeDataString(link.Query[(at + 5)..].Split('&')[0]) : null;
        if (link.IsFile) return Path.GetFileName(link.LocalPath);
        if (azdoPath is not null) return Path.GetFileName(azdoPath);
        return link.AbsolutePath.Contains("/blob/", StringComparison.Ordinal) ? Uri.UnescapeDataString(Path.GetFileName(link.AbsolutePath)) : link.Host;
    }

    public IAsyncRelayCommand OpenLinkCommand { get; }

    /// <summary>One line for "Copy facts": "✅ Correct: claim — reason (link)".</summary>
    public string CopyLine => $"{Verdict.Emoji()} {Label}: {Title} — {Reason}{(_insight.Link is { } link ? $" ({link})" : "")}";

    /// <summary>The same claim, said again (compared ignoring case).</summary>
    public bool IsSameAs(Insight other) => string.Equals(Claim, other.Claim, StringComparison.OrdinalIgnoreCase);

    /// <summary>Said again at <paramref name="at"/>: one more on the count, and that time.</summary>
    public void Repeat(DateTimeOffset at)
    {
        Count++;
        Time = Clock(at);
    }

    /// <summary>The user's short time format, the same as the Live page's.</summary>
    private static string Clock(DateTimeOffset at) => at.ToString("t", CultureInfo.CurrentCulture);
}
