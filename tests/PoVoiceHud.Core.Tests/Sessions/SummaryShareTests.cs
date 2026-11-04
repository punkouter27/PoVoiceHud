using System.Text.Json;
using PoVoiceHud.Core.Sessions;
using PoVoiceHud.Core.Tests.Fixtures;

namespace PoVoiceHud.Core.Tests.Sessions;

public class SummaryShareTests
{
    private const string Title = "Meeting notes — Wed 4 Nov, 10:02–10:47";
    private static readonly string Markdown = new SessionSummary("We went through the roadmap.", ["Launch moves to **March**"], ["Keep the demo"], [], [], []).ToMarkdown();

    [Fact]
    public async Task Each_service_gets_the_summary_in_its_own_shape_and_a_refusal_says_why()
    {
        var slack = new Uri("https://hooks.slack.com/services/T0/B0/x");
        var teams = new Uri("https://prod-12.westus.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke?sig=x");
        var discord = new Uri("https://discord.com/api/webhooks/1/x");
        var other = new Uri("https://hooks.example.com/in");

        using (var json = JsonDocument.Parse(SummaryShare.Payload(slack, Title, Markdown)))
        {
            var text = json.RootElement.GetProperty("text").GetString()!;
            Assert.StartsWith($"*{Title}*", text, StringComparison.Ordinal);
            Assert.Contains("*Main points*", text, StringComparison.Ordinal);   // headings as bold lines
            Assert.Contains("• Launch moves to *March*", text, StringComparison.Ordinal);
            Assert.DoesNotContain("## Summary", text, StringComparison.Ordinal);
        }
        using (var json = JsonDocument.Parse(SummaryShare.Payload(teams, Title, Markdown)))
        {
            var card = json.RootElement.GetProperty("attachments")[0];
            Assert.Equal("application/vnd.microsoft.card.adaptive", card.GetProperty("contentType").GetString());
            Assert.Equal(Title, card.GetProperty("content").GetProperty("body")[0].GetProperty("text").GetString());
            Assert.Contains("**Decisions**", card.GetProperty("content").GetProperty("body")[1].GetProperty("text").GetString(), StringComparison.Ordinal);
        }
        using (var json = JsonDocument.Parse(SummaryShare.Payload(discord, Title, Markdown + new string('x', 3000))))
            Assert.Equal(2000, json.RootElement.GetProperty("content").GetString()!.Length);
        using (var json = JsonDocument.Parse(SummaryShare.Payload(other, Title, Markdown)))
            Assert.Equal(Title, json.RootElement.GetProperty("title").GetString());
        Assert.Equal(("Slack", "Teams", "Discord", "hooks.example.com"),
            (SummaryShare.NameOf(slack), SummaryShare.NameOf(teams), SummaryShare.NameOf(discord), SummaryShare.NameOf(other)));
        Assert.False(SummaryShare.IsValidWebhook("http://hooks.slack.com/services/x"));

        var mail = SummaryShare.MailTo(Title, Markdown + new string('y', 5000));
        Assert.Equal("mailto", mail.Scheme);
        Assert.True(mail.OriginalString.Length < 6000);
        Assert.Contains(Uri.EscapeDataString("We went through the roadmap."), mail.OriginalString, StringComparison.Ordinal);

        var posted = new FakeHttpHandler(_ => FakeHttpHandler.Json("ok"));
        Assert.Null(await SummaryShare.PostAsync(new HttpClient(posted), slack, Title, Markdown, TestContext.Current.CancellationToken));
        Assert.Equal(slack, posted.Requests[0].RequestUri);
        var refused = new FakeHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        Assert.Equal("Slack answered HTTP 403", await SummaryShare.PostAsync(new HttpClient(refused), slack, Title, Markdown, TestContext.Current.CancellationToken));
    }
}
