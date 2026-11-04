using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PoVoiceHud.Core.Models;



namespace PoVoiceHud.Core.Meetings;

/// <summary>Where a webhook URL posts to, which decides the shape of what is sent.</summary>
public enum ShareTarget { Slack, Teams, Discord, Other }

/// <summary>
/// Posts a meeting summary to a chat channel's incoming webhook: Slack ({"text"} in its mrkdwn), Teams (a Workflows
/// "when a webhook request is received" flow, as an Adaptive Card), Discord ({"content"}, 2,000 characters at most), or any
/// other service as {"title", "text"} with the Markdown. Or, with no webhook, an email draft in the default mail app.
/// </summary>
public static partial class SummaryShare
{
    private const int DiscordLimit = 2000;
    private const int MailLimit = 1800;   // mailto: URLs past ~2,000 characters are cut off or refused by some mail apps

    /// <summary>A webhook must be https: it carries what was said in the meeting.</summary>
    public static bool IsValidWebhook(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https";

    public static ShareTarget TargetOf(Uri webhook) => webhook.Host switch
    {
        "hooks.slack.com" => ShareTarget.Slack,
        "discord.com" or "discordapp.com" => ShareTarget.Discord,
        var host when host.EndsWith(".logic.azure.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".powerplatform.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".webhook.office.com", StringComparison.OrdinalIgnoreCase) => ShareTarget.Teams,
        _ => ShareTarget.Other,
    };

    /// <summary>"Slack", "Teams", "Discord", or the webhook's site: what the Post button names.</summary>
    public static string NameOf(Uri webhook) => TargetOf(webhook) is var target and not ShareTarget.Other ? target.ToString() : webhook.Host;

    /// <summary>The JSON body for <paramref name="webhook"/>'s service.</summary>
    public static string Payload(Uri webhook, string title, string markdown)
    {
        var body = Body(markdown);
        JsonObject payload = TargetOf(webhook) switch
        {
            ShareTarget.Slack => new() { ["text"] = $"*{title}*\n\n{Slack(body)}" },
            ShareTarget.Discord => new() { ["content"] = Cut($"**{title}**\n\n{body}", DiscordLimit) },
            ShareTarget.Teams => new()
            {
                ["type"] = "message",
                ["attachments"] = new JsonArray(new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["content"] = new JsonObject
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = new JsonArray(
                            new JsonObject { ["type"] = "TextBlock", ["text"] = title, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
                            new JsonObject { ["type"] = "TextBlock", ["text"] = Teams(body), ["wrap"] = true }),
                    },
                }),
            },
            _ => new() { ["title"] = title, ["text"] = body },
        };
        return payload.ToJsonString();
    }

    /// <summary>Posts the summary; null when the service took it, otherwise why not, in a few words.</summary>
    public static async Task<string?> PostAsync(HttpClient http, Uri webhook, string title, string markdown, CancellationToken ct)
    {
        var payload = Payload(webhook, title, markdown);
        try
        {
            await Resilience.Default.ExecuteAsync(async token =>
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(15));
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(webhook, content, limit.Token);
                response.EnsureSuccessStatusCode();
            }, ct);
            return null;
        }
        catch (HttpRequestException e)
        {
            return e.StatusCode is { } code ? $"{NameOf(webhook)} answered HTTP {(int)code}" : $"could not reach {NameOf(webhook)}: {e.Message}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"{NameOf(webhook)} did not answer within 15 s";
        }
    }

    /// <summary>A mailto: link with the summary as its body, cut to what mail apps take; the recipients are left to fill in.</summary>
    public static Uri MailTo(string title, string markdown) =>
        new($"mailto:?subject={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(Cut(Body(markdown), MailLimit))}");

    /// <summary>The summary without its "## Summary" heading: the post has its own title.</summary>
    private static string Body(string markdown) => TopHeading().Replace(markdown.Trim(), "").Trim();

    /// <summary>Slack's mrkdwn: headings as bold lines, bullets as •, **bold** as *bold*.</summary>
    private static string Slack(string markdown) =>
        Bold().Replace(Bullet().Replace(Heading().Replace(markdown, "*$1*"), "• "), "*$1*");

    /// <summary>Adaptive Cards have no headings: they become bold lines; lists and **bold** carry over.</summary>
    private static string Teams(string markdown) => Heading().Replace(markdown, "**$1**");

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"\A##\s+Summary\s*")]
    private static partial Regex TopHeading();

    [GeneratedRegex(@"^#{2,6}\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^- ", RegexOptions.Multiline)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();
}
