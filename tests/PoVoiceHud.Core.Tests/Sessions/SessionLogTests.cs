using PoVoiceHud.Core.Sessions;
using PoVoiceHud.Core.ViewModels;

namespace PoVoiceHud.Core.Tests.Sessions;

public class SessionLogTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 14, 0, 0, TimeSpan.FromHours(2));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PoVoiceHud.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Lines_and_verdicts_are_appended_to_a_readable_file_per_day()
    {
        var log = new SessionLog(_dir);
        log.Append(new SessionEntry { At = T0, Source = "Mic", Heard = "Python was created by Dennis Ritchie." });
        log.Append(new SessionEntry { At = T0.AddMinutes(1), Source = "Mic", Claim = "Python was created by Dennis Ritchie",
            Answer = "❌ Incorrect — Guido van Rossum created it.", Verdict = "Contradicted" });
        log.Append(new SessionEntry { At = T0.AddDays(1), Heard = "Next day." });

        var lines = File.ReadAllLines(Path.Combine(_dir, "2026-10-19.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Equal("""{"at":"2026-10-19T14:00:00+02:00","source":"Mic","heard":"Python was created by Dennis Ritchie."}""", lines[0]);
        Assert.Contains("❌ Incorrect", lines[1]);   // readable, not \u-escaped; empty fields are left out
        Assert.True(File.Exists(Path.Combine(_dir, "2026-10-20.jsonl")));
    }

    private sealed class FakeModel(string reply) : Core.Providers.FactCheck.ILanguageModel
    {
        public string? User { get; private set; }

        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            User = user;
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task A_day_is_read_back_searched_summed_up_and_exported()
    {
        var log = new SessionLog(_dir);
        log.Append(new SessionEntry { At = T0, Source = "Teams", Heard = "The Moon is bigger than Earth." });
        log.Append(new SessionEntry { At = T0, Source = "Teams", Claim = "The Moon is bigger than Earth",
            Answer = "❌ Incorrect — The Moon is about a quarter of Earth's width.", Verdict = "Contradicted", Link = new Uri("https://en.wikipedia.org/wiki/Moon") });
        log.Append(new SessionEntry { At = T0.AddMinutes(1), Source = "Mic", Heard = "Let's ship on Friday." });
        File.AppendAllText(Path.Combine(_dir, "2026-10-19.jsonl"), "{\"at\":\"2026-10-19T14:0");   // cut short by a crash
        log.Append(new SessionEntry { At = T0.AddDays(-3), Heard = "Earlier." });

        var day = new DateOnly(2026, 10, 19);
        Assert.Equal([day, day.AddDays(-3)], log.Days());
        Assert.Equal(3, log.Read(day).Count);   // the broken line is skipped

        var model = new FakeModel("""
            {"overview":"A planning call.","points":["Shipping Friday"],"decisions":["Ship on Friday"],
             "corrections":["The Moon is smaller than Earth"],"open_questions":[],"code":[]}
            """);
        string? exported = null;
        var history = new HistoryViewModel(log, new SessionSummarizer(model).SummarizeAsync, (_, markdown) => { exported = markdown; return Task.CompletedTask; });
        history.Reload();
        Assert.Equal(0, history.DayIndex);   // the newest day
        Assert.Equal(3, history.Rows.Count);
        Assert.Equal((LineState.Incorrect, ""), (history.Rows[1].Kind, history.Rows[1].Speaker));
        history.Search = "friday";
        Assert.Equal("You", Assert.Single(history.Rows).Speaker);

        await history.SummarizeCommand.ExecuteAsync(null);
        Assert.Contains("14:00 Them: The Moon is bigger than Earth.", model.User);   // the transcript goes as data
        Assert.StartsWith("## Summary\n\nA planning call.", history.Summary);
        Assert.Contains("### Decisions\n\n- Ship on Friday", history.Summary);
        Assert.Equal(history.Summary, log.ReadSummary(day));   // kept beside the day's history

        await history.ExportCommand.ExecuteAsync(null);
        Assert.StartsWith("# Meeting notes — ", exported);
        Assert.Contains("2 lines heard · 0 correct · 1 incorrect · 0 answers from the code", exported);
        Assert.Contains("- **The Moon is bigger than Earth** — The Moon is about a quarter of Earth's width. ([source](https://en.wikipedia.org/wiki/Moon))", exported);
        Assert.Contains("## Summary", exported);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
