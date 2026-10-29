using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Providers;
using PoVoiceHud.Core.Screen;

namespace PoVoiceHud.Core.ViewModels;

/// <summary>The page the switch at the top has chosen.</summary>
public enum HudPage { Live, Facts, Code, Screen, History }

/// <summary>
/// The window: a header and one of five pages, Live (every line heard, highlighted by its verdict or answer), Facts (the
/// claims found correct or incorrect), Code facts (questions about the code repository answered, and statements about it
/// judged), Screen facts (what is on the screen now, and the claims judged against it), History (past days) or Settings.
/// Until a working endpoint and key are set (<see cref="NeedsSetup"/>) only Settings is shown. The header's headline shows
/// whichever happened last: a verdict, an answer or a status change.
/// </summary>
public sealed partial class HudViewModel : ObservableObject
{
    public const int LiveSize = 60;
    public const int FactsSize = 200;
    /// <summary>Verdicts the sparkline in the header spans.</summary>
    public const int TrendSize = 40;

    /// <summary>What the Facts and Code facts lists can be narrowed to, besides their search text.</summary>
    public static IReadOnlyList<string> FilterKinds { get; } = ["Everything", "Correct", "Incorrect", "Answers", "Said by you", "Said by them"];

    private readonly Func<Uri, Task> _openUri;
    private readonly HashSet<TranscriptLine> _inFlight = [];
    private readonly List<double> _trend = [];
    private int _trendCorrect;
    private int _trendJudged;
    private string _lastStatus = "Starting…";

    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _headline = "Starting…";
    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private double _micLevel;
    /// <summary>Who the meter is hearing: the microphone (you) or the call.</summary>
    [ObservableProperty] private SourceHint _levelSource = SourceHint.Mic;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowsLive), nameof(ShowsFacts), nameof(ShowsCode), nameof(ShowsScreen), nameof(ShowsHistory), nameof(ShowsList), nameof(CanCopy))]
    private HudPage _page;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowsSettings), nameof(ShowsPages), nameof(ShowsLive), nameof(ShowsFacts), nameof(ShowsCode), nameof(ShowsScreen), nameof(ShowsHistory), nameof(ShowsList))]
    private bool _isSettingsOpen;
    /// <summary>No working endpoint and key yet: the app shows Settings and listens to nothing until there is.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowsSettings), nameof(ShowsPages), nameof(ShowsLive), nameof(ShowsFacts), nameof(ShowsCode), nameof(ShowsScreen), nameof(ShowsHistory), nameof(ShowsList))]
    private bool _needsSetup;
    /// <summary>A code repository is connected, so sentences about it are looked up (the Code facts page says how, otherwise).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NoCodeHint))] private bool _hasRepository;
    /// <summary>More than one repository is connected, so each code fact says which it came from.</summary>
    [ObservableProperty] private bool _hasRepositories;
    /// <summary>The screen is watched (<see cref="Settings.WatchScreen"/>), so claims can be judged against it.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NoScreenHint), nameof(HasScreenNow))] private bool _watchesScreen;
    /// <summary>The latest description of the screen, shown at the top of Screen facts; "" before the first.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasScreenNow))] private string _screenNow = "";
    /// <summary>"On screen since 15:04".</summary>
    [ObservableProperty] private string _screenSince = "";
    // This session's verdicts, for the share bar along the bottom of the header.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TallySummary), nameof(HeaderTip))] private int _supported;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TallySummary), nameof(HeaderTip))] private int _contradicted;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TallySummary), nameof(HeaderTip))] private int _unverified;
    /// <summary>How the model has done this session (<see cref="ModelStats.Summary"/>), for the header's tooltip.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HeaderTip))] private string _modelSummary = "No model calls yet";
    /// <summary>Only the header shows: a strip to keep on screen through a call.</summary>
    [ObservableProperty] private bool _isCompact;
    /// <summary>Text the Facts and Code facts lists are narrowed to.</summary>
    [ObservableProperty] private string _filterText = "";
    /// <summary>Which of <see cref="FilterKinds"/> the lists are narrowed to.</summary>
    [ObservableProperty] private int _filterKind;

    public HudViewModel(Func<Uri, Task> openUri)
    {
        _openUri = openUri;
        // The names and the rows live as long as the window does, so the rows are told rather than subscribed.
        Names.PropertyChanged += (_, _) =>
        {
            foreach (var line in Live) line.Renamed();
            foreach (var fact in Facts) fact.Renamed();
            foreach (var fact in CodeFacts) fact.Renamed();
            foreach (var fact in ScreenFacts) fact.Renamed();
        };
    }

    public bool ShowsSettings => IsSettingsOpen || NeedsSetup;
    /// <summary>The pages and the switch between them.</summary>
    public bool ShowsPages => !ShowsSettings;
    public bool ShowsLive => ShowsPages && Page == HudPage.Live;
    public bool ShowsFacts => ShowsPages && Page == HudPage.Facts;
    public bool ShowsCode => ShowsPages && Page == HudPage.Code;
    public bool ShowsScreen => ShowsPages && Page == HudPage.Screen;
    public bool ShowsHistory => ShowsPages && Page == HudPage.History;
    /// <summary>Facts, Code facts or Screen facts: a list with a count, a filter and Copy.</summary>
    public bool ShowsList => ShowsFacts || ShowsCode || ShowsScreen;
    public bool HasLive => Live.Count > 0;
    public bool NoLive => Live.Count == 0;
    public bool HasFacts => Facts.Count > 0;
    public bool NoFacts => ShownFacts.Count == 0;
    public bool HasCodeFacts => CodeFacts.Count > 0;
    public bool NoCodeFacts => ShownCodeFacts.Count == 0;
    public bool NoScreenFacts => ShownScreenFacts.Count == 0;
    public bool HasScreenNow => WatchesScreen && ScreenNow.Length > 0;
    /// <summary>The lists are narrowed, so an empty one says so rather than "nothing yet".</summary>
    public bool IsFiltered => FilterText.Trim().Length > 0 || FilterKind != 0;

    /// <summary>What the empty Code facts page says: how to ask, or that a repository has to be connected first.</summary>
    public string NoCodeHint => IsFiltered && HasCodeFacts ? "Nothing matches the filter."
        : HasRepository
        ? "No code facts yet. Say the repository's name, a file or class in it, or words like \"our code\", \"the repo\" or \"our website\": questions are answered from the code and statements checked against it."
        : "Connect a code repository in Settings (a GitHub or Azure DevOps URL, or a folder), and questions and statements about it are answered and checked here.";

    /// <summary>What the empty Facts page says.</summary>
    public string NoFactsHint => IsFiltered && HasFacts ? "Nothing matches the filter."
        : "No facts yet. Claims the model finds correct or incorrect are listed here, newest first.";

    /// <summary>What Screen facts says under its list while it is empty: that the screen is being read, or how to turn that on.</summary>
    public string NoScreenHint => IsFiltered && ScreenFacts.Count > 0 ? "Nothing matches the filter."
        : WatchesScreen
        ? "No screen facts yet. Say something about what is on the screen (a date, a name, a number on a form) and it is checked against it here."
        : "Turn on Watch the screen in Settings, and what you say about the screen (a date or a name on a form, say) is checked against it here.";

    public bool CanCopy => Page switch { HudPage.Code => ShownCodeFacts.Count > 0, HudPage.Screen => ShownScreenFacts.Count > 0, _ => ShownFacts.Count > 0 };

    /// <summary>"2 answers · 1 checked": the Code facts page heading.</summary>
    public string CodeSummary => $"{CodeFacts.Count(f => f.IsAnswer)} {(CodeFacts.Count(f => f.IsAnswer) == 1 ? "answer" : "answers")} · {CodeFacts.Count(f => !f.IsAnswer)} checked";

    /// <summary>"3 correct · 1 incorrect"; the Facts page heading and the headline's tooltip.</summary>
    public string FactsSummary => Summary(Facts);

    /// <summary>"1 correct · 2 incorrect": the Screen facts page heading.</summary>
    public string ScreenSummary => Summary(ScreenFacts);

    private static string Summary(IEnumerable<FactViewModel> facts) => $"{facts.Count(f => f.IsCorrect)} correct · {facts.Count(f => !f.IsCorrect)} incorrect";

    /// <summary>What the share bar along the header's bottom edge shows: "3 correct · 1 incorrect · 2 can't tell" this session.</summary>
    public string TallySummary => Supported + Contradicted + Unverified == 0
        ? "No verdicts yet"
        : $"{Supported} {Verdict.Supported.Label().ToLowerInvariant()} · {Contradicted} {Verdict.Contradicted.Label().ToLowerInvariant()} · " +
          $"{Unverified} {Verdict.Unverified.Label().ToLowerInvariant()}";

    /// <summary>The header's tooltip: this session's verdicts and how the model has done.</summary>
    public string HeaderTip => $"{TallySummary}\n{ModelSummary}";

    /// <summary>
    /// The share of claims found correct so far, after each of the last <see cref="TrendSize"/> correct or incorrect
    /// verdicts: the sparkline in the header.
    /// </summary>
    public IReadOnlyList<double> Trend => _trend;

    /// <summary>What the two sides are called.</summary>
    public SpeakerNames Names { get; } = new();

    /// <summary>Past days; set by the app once it knows where the history is.</summary>
    [ObservableProperty] private HistoryViewModel? _history;

    /// <summary>Every line heard, oldest first.</summary>
    public ObservableCollection<LiveLineViewModel> Live { get; } = [];

    /// <summary>Claims found correct or incorrect, newest first. "Can't tell" stays on the Live page only.</summary>
    public ObservableCollection<FactViewModel> Facts { get; } = [];

    /// <summary>Questions about the code answered and statements about it judged, newest first.</summary>
    public ObservableCollection<CodeFactViewModel> CodeFacts { get; } = [];

    /// <summary>Claims judged against the screen and found correct or incorrect, newest first. They are not on Facts.</summary>
    public ObservableCollection<FactViewModel> ScreenFacts { get; } = [];

    /// <summary><see cref="Facts"/> narrowed by the filter: what the Facts page lists and copies.</summary>
    public ObservableCollection<FactViewModel> ShownFacts { get; } = [];

    /// <summary><see cref="CodeFacts"/> narrowed by the filter.</summary>
    public ObservableCollection<CodeFactViewModel> ShownCodeFacts { get; } = [];

    /// <summary><see cref="ScreenFacts"/> narrowed by the filter.</summary>
    public ObservableCollection<FactViewModel> ShownScreenFacts { get; } = [];

    /// <summary>Something came back that has a sound: a verdict, an answer from the code.</summary>
    public event Action<Cue>? Arrived;

    public void AddTranscript(TranscriptLine line)
    {
        Live.Add(new LiveLineViewModel(line, Names));
        while (Live.Count > LiveSize) Live.RemoveAt(0);
        OnPropertyChanged(nameof(HasLive));
        OnPropertyChanged(nameof(NoLive));
    }

    /// <summary>A line went to the fact checker (busy) or came back from it.</summary>
    public void MarkChecking(TranscriptLine line, bool busy)
    {
        if (busy) _inFlight.Add(line);
        else _inFlight.Remove(line);
        IsChecking = _inFlight.Count > 0;
        if (Find(line) is not { } live) return;
        if (busy && live.State == LineState.Heard) live.State = LineState.Checking;
        else if (!busy && live.State == LineState.Checking) live.State = LineState.Heard;   // not a claim after all
    }

    /// <summary>The pipeline was replaced (settings changed): nothing is with the old one's fact checker any more.</summary>
    public void ResetChecking()
    {
        _inFlight.Clear();
        IsChecking = false;
        foreach (var line in Live.Where(l => l.State == LineState.Checking)) line.State = LineState.Heard;
    }

    public void Push(Checked result)
    {
        var (line, verdict) = result;
        Find(line)?.Judge(verdict);
        Tally(verdict.Verdict);
        if (verdict.Verdict != Verdict.Unverified)
        {
            if (verdict.OnScreen) AddFact(ScreenFacts, verdict, line.At, line.Source);
            else AddFact(Facts, verdict, line.At, line.Source);
            Headline = $"{verdict.Verdict.Emoji()} {verdict.Claim}";   // "can't tell" stays a note on its line
        }
        Arrived?.Invoke(CueFor(verdict.Verdict));
    }

    /// <summary>
    /// What the code says about a line: the line takes the answer or verdict, and the Code facts page gets a row, except for
    /// "can't tell", which stays a quiet note on its line as on Facts.
    /// </summary>
    public void PushCode(CodeChecked result)
    {
        var (line, fact) = result;
        Find(line)?.Judge(fact);
        if (fact.Verdict is { } verdict) Tally(verdict);
        if (fact.Verdict == Verdict.Unverified)
        {
            Arrived?.Invoke(Cue.Unverified);
            return;
        }
        CodeFacts.Insert(0, new CodeFactViewModel(fact, line.At, line.Source, _openUri, Names));
        while (CodeFacts.Count > FactsSize) CodeFacts.RemoveAt(CodeFacts.Count - 1);
        Headline = $"{(fact.Verdict is { } v ? v.Emoji() : "💬")} {fact.Topic}";
        OnPropertyChanged(nameof(HasCodeFacts));
        OnPropertyChanged(nameof(CodeSummary));
        Refilter();
        Arrived?.Invoke(fact.Verdict is { } said ? CueFor(said) : Cue.Answer);
    }

    private static Cue CueFor(Verdict verdict) => verdict switch
    {
        Verdict.Supported => Cue.Supported,
        Verdict.Contradicted => Cue.Contradicted,
        _ => Cue.Unverified,
    };

    /// <summary>A new description of the screen: the top of Screen facts.</summary>
    public void ShowScreen(ScreenSeen seen)
    {
        ScreenNow = seen.Summary;
        ScreenSince = $"On screen since {seen.At.ToString("t", System.Globalization.CultureInfo.CurrentCulture)}";
    }

    /// <summary>Copy: one line per fact on the page on show, as filtered, newest first.</summary>
    public string CopyText => string.Join(Environment.NewLine, Page switch
    {
        HudPage.Code => ShownCodeFacts.Select(f => f.CopyLine),
        HudPage.Screen => ShownScreenFacts.Select(f => f.CopyLine),
        _ => ShownFacts.Select(f => f.CopyLine),
    });

    private void AddFact(ObservableCollection<FactViewModel> facts, Insight verdict, DateTimeOffset at, SourceHint source)
    {
        // The same claim said again is one row with a count, moved to the top.
        if (facts.FirstOrDefault(f => f.IsSameAs(verdict) && f.Verdict == verdict.Verdict) is { } earlier)
        {
            facts.Remove(earlier);
            earlier.Repeat(at);
            facts.Insert(0, earlier);
        }
        else
        {
            facts.Insert(0, new FactViewModel(verdict, at, source, _openUri, Names));
            while (facts.Count > FactsSize) facts.RemoveAt(facts.Count - 1);
        }
        OnPropertyChanged(nameof(HasFacts));
        OnPropertyChanged(nameof(FactsSummary));
        OnPropertyChanged(nameof(ScreenSummary));
        Refilter();
    }

    private void Tally(Verdict verdict)
    {
        if (verdict == Verdict.Supported) Supported++;
        else if (verdict == Verdict.Contradicted) Contradicted++;
        else Unverified++;
        if (verdict == Verdict.Unverified) return;
        _trendJudged++;
        if (verdict == Verdict.Supported) _trendCorrect++;
        _trend.Add((double)_trendCorrect / _trendJudged);
        if (_trend.Count > TrendSize) _trend.RemoveAt(0);
        OnPropertyChanged(nameof(Trend));
    }

    partial void OnFilterTextChanged(string value) => Refilter();

    partial void OnFilterKindChanged(int value) => Refilter();

    /// <summary>Brings the shown lists in line with the facts and the filter, moving rows rather than rebuilding the lists.</summary>
    private void Refilter()
    {
        Mirror(ShownFacts, [.. Facts.Where(f => Matches(f.Title + " " + f.Reason, f.Verdict, f.Source))]);
        Mirror(ShownCodeFacts, [.. CodeFacts.Where(f => Matches(f.Topic + " " + f.Text + " " + f.Repository, f.Verdict, f.Source))]);
        Mirror(ShownScreenFacts, [.. ScreenFacts.Where(f => Matches(f.Title + " " + f.Reason, f.Verdict, f.Source))]);
        OnPropertyChanged(nameof(NoFacts));
        OnPropertyChanged(nameof(NoCodeFacts));
        OnPropertyChanged(nameof(NoScreenFacts));
        OnPropertyChanged(nameof(NoScreenHint));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(NoFactsHint));
        OnPropertyChanged(nameof(NoCodeHint));
        OnPropertyChanged(nameof(CanCopy));
    }

    private bool Matches(string text, Verdict? verdict, SourceHint source) =>
        (FilterText.Trim() is var search && (search.Length == 0 || text.Contains(search, StringComparison.OrdinalIgnoreCase)
            || Names.Of(source).Contains(search, StringComparison.OrdinalIgnoreCase)))
        && FilterKind switch
        {
            1 => verdict == Verdict.Supported,
            2 => verdict == Verdict.Contradicted,
            3 => verdict is null,
            4 => source == SourceHint.Mic,
            5 => source == SourceHint.Teams,
            _ => true,
        };

    /// <summary>Makes <paramref name="shown"/> hold <paramref name="target"/>, in order, with as few moves as it takes.</summary>
    internal static void Mirror<T>(ObservableCollection<T> shown, List<T> target) where T : class
    {
        for (var i = 0; i < target.Count; i++)
        {
            if (i < shown.Count && ReferenceEquals(shown[i], target[i])) continue;
            var at = shown.IndexOf(target[i]);
            if (at > i) shown.Move(at, i);
            else shown.Insert(i, target[i]);
        }
        while (shown.Count > target.Count) shown.RemoveAt(shown.Count - 1);
    }

    // Lines are records, so a verdict finds its line even if it was copied on the way; the newest match wins.
    private LiveLineViewModel? Find(TranscriptLine line) => Live.LastOrDefault(l => l.Line == line);

    public void SetStatus(string status)
    {
        _lastStatus = status;
        ShowStatus();
    }

    private void ShowStatus() => Headline = StatusText = IsMuted ? "Muted" : _lastStatus;

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        ShowStatus();
    }

    /// <summary>The ⚙ button: opens Settings, or leaves them (discarding what was not applied). Not while setup is needed.</summary>
    [RelayCommand]
    private void ToggleSettings()
    {
        if (!NeedsSetup) IsSettingsOpen = !IsSettingsOpen;
    }

    /// <summary>The header alone, or the whole window again.</summary>
    [RelayCommand]
    private void ToggleCompact() => IsCompact = !IsCompact;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // The History page reads the log again when it is opened, so today's newest lines are in it.
        if (e.PropertyName == nameof(ShowsHistory) && ShowsHistory) History?.Reload();
    }
}
