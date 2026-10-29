using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoVoiceHud.Core.Providers;
using PoVoiceHud.Core.Providers.FactCheck;
using PoVoiceHud.Core.Sessions;

namespace PoVoiceHud.Core.ViewModels;

/// <summary>One line of a past day: something said, or the verdict or answer it got (<see cref="Kind"/> says which).</summary>
public sealed record HistoryRow(string Time, string Speaker, string Text, LineState Kind)
{
    public bool IsHeard => Kind == LineState.Heard;
}

/// <summary>
/// The History page: any day's transcript and verdicts from the session log, searchable, with its meeting summary (made on
/// request, or when the microphone is muted after a meeting) and an export to a Markdown document.
/// </summary>
/// <param name="summarize">Sums up a day's entries with the model; null until the app is connected.</param>
/// <param name="export">Writes the Markdown for a day somewhere the user can open it, and opens it.</param>
public sealed partial class HistoryViewModel(SessionLog log, Func<IReadOnlyList<SessionEntry>, CancellationToken, Task<SessionSummary?>>? summarize,
    Func<DateOnly, string, Task> export) : ObservableObject
{
    private List<DateOnly> _days = [];
    private IReadOnlyList<SessionEntry> _entries = [];

    [ObservableProperty] private int _dayIndex = -1;
    [ObservableProperty] private string _search = "";
    /// <summary>The day's summary as Markdown, or "" when it has none yet.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSummary))] private string _summary = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SummarizeCommand))] private bool _isSummarizing;

    /// <summary>The days with a history, newest first, as people read dates.</summary>
    public ObservableCollection<string> Days { get; } = [];

    /// <summary>The chosen day's lines that match the search.</summary>
    public ObservableCollection<HistoryRow> Rows { get; } = [];

    public bool HasSummary => Summary.Length > 0;

    public Func<IReadOnlyList<SessionEntry>, CancellationToken, Task<SessionSummary?>>? Summarizer { get; set; } = summarize;

    public DateOnly? Day => DayIndex >= 0 && DayIndex < _days.Count ? _days[DayIndex] : null;

    /// <summary>Reads the list of days again (today's file may be new), keeping the day on show or else choosing the newest.</summary>
    public void Reload()
    {
        var shown = Day;
        _days = [.. log.Days()];
        Days.Clear();
        foreach (var day in _days) Days.Add(day.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture));
        var at = shown is { } d ? _days.IndexOf(d) : -1;
        if (at < 0) at = _days.Count > 0 ? 0 : -1;
        if (at == DayIndex) Load();
        else DayIndex = at;
    }

    partial void OnDayIndexChanged(int value) => Load();

    partial void OnSearchChanged(string value) => Show();

    private void Load()
    {
        _entries = Day is { } day ? log.Read(day) : [];
        Summary = Day is { } d ? log.ReadSummary(d) ?? "" : "";
        Message = "";
        Show();
    }

    private void Show()
    {
        Rows.Clear();
        var search = Search.Trim();
        foreach (var entry in _entries)
        {
            var row = Row(entry);
            if (search.Length == 0 || row.Text.Contains(search, StringComparison.OrdinalIgnoreCase) || row.Speaker.Contains(search, StringComparison.OrdinalIgnoreCase))
                Rows.Add(row);
        }
    }

    private static HistoryRow Row(SessionEntry entry) => entry.Heard is { } heard
        ? new HistoryRow(entry.At.ToString("t", CultureInfo.CurrentCulture), SessionSummarizer.Speaker(entry.Source), heard, LineState.Heard)
        : new HistoryRow("", "", WithoutEmoji(entry.Answer ?? entry.Claim ?? ""), entry.Verdict switch
        {
            nameof(Verdict.Supported) => LineState.Correct,
            nameof(Verdict.Contradicted) => LineState.Incorrect,
            "Answer" => LineState.Answered,
            _ => LineState.CantTell,
        });

    /// <summary>"✅ Correct — …" as "Correct — …": the row has its own icon in its own colour.</summary>
    private static string WithoutEmoji(string answer) =>
        answer.Length > 0 && !char.IsLetterOrDigit(answer[0]) && answer.IndexOf(' ', StringComparison.Ordinal) is var at and > 0 ? answer[(at + 1)..] : answer;

    private bool CanSummarize() => !IsSummarizing;

    /// <summary>Sums up the day on show with the model and saves it beside the day's history.</summary>
    [RelayCommand(CanExecute = nameof(CanSummarize))]
    private async Task Summarize()
    {
        if (Day is not { } day) return;
        Message = await SummarizeDayAsync(day, CancellationToken.None) ?? "";
        if (Day == day) Summary = log.ReadSummary(day) ?? "";
    }

    /// <summary>
    /// Sums up <paramref name="day"/> and saves the summary; returns why not when it could not (null when it could). Also used
    /// when the microphone is muted after a meeting.
    /// </summary>
    public async Task<string?> SummarizeDayAsync(DateOnly day, CancellationToken ct)
    {
        if (Summarizer is not { } summarizer) return "Connect the model in Settings to sum up a meeting.";
        IsSummarizing = true;
        try
        {
            var summary = await summarizer(log.Read(day), ct);
            if (summary is null) return "Nothing to sum up yet.";
            log.SaveSummary(day, summary.ToMarkdown());
            return null;
        }
        catch (FactCheckUnavailableException e)
        {
            return $"No summary: {e.Message}";
        }
        finally
        {
            IsSummarizing = false;
        }
    }

    /// <summary>The day on show, with its summary, as a Markdown document that opens when written.</summary>
    [RelayCommand]
    private async Task Export()
    {
        if (Day is not { } day) return;
        await export(day, SessionExport.ToMarkdown(day, _entries, Summary));
    }
}
