using Microsoft.Extensions.Logging;
using PoVoiceHud.Core;
using PoVoiceHud.Core.Config;
using PoVoiceHud.Core.Meetings;
using PoVoiceHud.Core.Models;

namespace PoVoiceHud.App;

/// <summary>The end of a meeting: its summary, and sharing or exporting it.</summary>
public partial class App
{
    /// <summary>
    /// Muting after a conversation, or a call ending (a dozen lines or more since the last summary), sums the day up in the
    /// background and saves it beside the history, where the History page shows it. With "Offer to share" on, the meeting
    /// alone (from its first line) is summed up too, and the window offers to post it or email it; nothing goes until asked.
    /// </summary>
    private async Task SummarizeMeetingAsync()
    {
        if (Demo || _heardSinceSummary < 12 || _window?.ViewModel.History is not { } history) return;
        var (start, viewModel) = (_meetingStart ?? DateTimeOffset.Now, _window.ViewModel);
        (_heardSinceSummary, _meetingStart) = (0, null);
        var problem = await history.SummarizeDayAsync(DateOnly.FromDateTime(DateTime.Now), CancellationToken.None);
        _log.LogInformation("Meeting summary: {Result}", problem ?? "saved");
        if (problem is not null) return;
        viewModel.SetStatus("Meeting summary saved: see History");
        if (!_settings.OfferToShare || ServiceCollectionExtensions.CreateSummarizer(_settings, Http, Stats) is not { } summarizer) return;
        try
        {
            var entries = Sessions.Read(DateOnly.FromDateTime(start.LocalDateTime)).Where(e => e.At >= start).ToList();
            if (await summarizer.SummarizeAsync(entries, CancellationToken.None) is not { } meeting) return;
            var title = $"Meeting notes — {start.LocalDateTime:ddd d MMM, HH:mm}–{DateTime.Now:HH:mm}";
            var markdown = meeting.ToMarkdown();
            var webhook = Webhook();
            viewModel.OfferShare($"Summary of the meeting from {start.LocalDateTime:t} is ready.",
                webhook is null ? "Email it" : $"Post to {SummaryShare.NameOf(webhook)}", () => ShareAsync(title, markdown));
        }
        catch (FactCheckUnavailableException e)
        {
            _log.LogWarning("Meeting summary to share: {Problem}", e.Message);
        }
    }

    /// <summary>The chat webhook from Credential Manager, or null for none (or one that is not https).</summary>
    private static Uri? Webhook() =>
        CredentialStore.Read(CredentialStore.ShareWebhook) is { } url && SummaryShare.IsValidWebhook(url) ? new Uri(url) : null;

    /// <summary>Posts a summary to the chat webhook, or without one opens an email draft; says how it went.</summary>
    private async Task<string> ShareAsync(string title, string markdown)
    {
        if (Webhook() is not { } webhook)
        {
            await OpenAsync(SummaryShare.MailTo(title, markdown));
            return "Opened an email with the summary";
        }
        var problem = await SummaryShare.PostAsync(Http, webhook, title, markdown, CancellationToken.None);
        _log.LogInformation("Shared a summary to {Target}: {Result}", SummaryShare.NameOf(webhook), problem ?? "posted");
        return problem is null ? $"Posted the summary to {SummaryShare.NameOf(webhook)}" : $"Not posted: {problem}";
    }

    /// <summary>The History page's Share: that day's summary.</summary>
    private Task<string> ShareDayAsync(DateOnly day, string markdown) =>
        ShareAsync($"Meeting notes — {day.ToString("dddd d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture)}", markdown);

    /// <summary>Writes a day's Markdown to Documents\PoVoiceHud and opens it.</summary>
    private async Task ExportAsync(DateOnly day, string markdown)
    {
        try
        {
            Directory.CreateDirectory(ExportDir);
            var path = Path.Combine(ExportDir, $"Meeting {day:yyyy-MM-dd}.md");
            await File.WriteAllTextAsync(path, markdown);
            _log.LogInformation("Exported {Day} to {Path}", day, path);
            await OpenAsync(new Uri(path));
            _window?.ViewModel.SetStatus($"Saved {Path.GetFileName(path)} in Documents\\PoVoiceHud");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _window?.ViewModel.SetStatus($"Export failed: {e.Message}");
        }
    }
}
