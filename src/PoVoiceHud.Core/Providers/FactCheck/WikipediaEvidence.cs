using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoVoiceHud.Core.Providers.FactCheck;

/// <summary>A passage looked up for a claim: the article it came from and its opening text.</summary>
public sealed record Evidence(string Title, Uri Url, string Text);

public interface IEvidenceSource
{
    /// <summary>Passages that may settle <paramref name="claim"/>, best first; empty when none were found or the lookup failed.</summary>
    Task<IReadOnlyList<Evidence>> FindAsync(string claim, CancellationToken ct);
}

/// <summary>
/// English Wikipedia's search API (no key, no account): the opening paragraphs of the three articles that best match
/// the claim's content words. Best-effort by design: a slow or failed lookup returns nothing and the model judges alone.
/// Only those content words leave the machine, never audio.
/// </summary>
public sealed partial class WikipediaEvidence(HttpClient http) : IEvidenceSource
{
    private const int Articles = 3;
    private const int ExtractChars = 1200;
    private static readonly Uri Api = new("https://en.wikipedia.org/w/api.php");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2.5);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being", "am", "of", "in", "on", "at", "to", "for", "by", "with",
        "from", "and", "or", "but", "not", "no", "that", "this", "these", "those", "it", "its", "as", "has", "have", "had", "do",
        "does", "did", "will", "would", "can", "could", "should", "than", "then", "there", "their", "they", "he", "she", "his",
        "her", "we", "our", "you", "your", "i", "my", "me", "so", "very", "about", "into", "over", "under", "which", "who", "what",
    };

    public async Task<IReadOnlyList<Evidence>> FindAsync(string claim, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            foreach (var terms in Searches(claim))
            {
                var found = await SearchAsync(terms, limit.Token);
                if (found.Count > 0) return found;
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Wikipedia unreachable or too slow: the verdict goes ahead without passages.
        }
        return [];
    }

    /// <summary>
    /// What to search for: the claim's content words, then — because the search wants every word to match — only its
    /// names and numbers. "Python was created by Dennis Ritchie" → "Python created Dennis Ritchie", then "Python Dennis Ritchie".
    /// </summary>
    private static IEnumerable<string> Searches(string claim)
    {
        var content = Word().Matches(claim).Select(m => m.Value).Where(w => !StopWords.Contains(w)).ToList();
        if (content.Count == 0) yield break;
        yield return string.Join(' ', content);
        var names = content.Where(w => char.IsUpper(w[0]) || char.IsDigit(w[0])).ToList();
        if (names.Count > 0 && names.Count < content.Count) yield return string.Join(' ', names);
    }

    private async Task<IReadOnlyList<Evidence>> SearchAsync(string terms, CancellationToken ct)
    {
        var query = string.Join('&',
            "action=query", "format=json", "formatversion=2", "redirects=1",
            $"generator=search&gsrlimit={Articles}&gsrsearch={Uri.EscapeDataString(terms)}",
            $"prop=extracts%7Cinfo&exintro=1&explaintext=1&exlimit={Articles}&exchars={ExtractChars}&inprop=url");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{Api}?{query}"));
        // Wikimedia asks every client to name itself.
        request.Headers.UserAgent.ParseAdd("PoVoiceHud/1.1 (Windows desktop fact-check HUD)");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var body = doc.RootElement;

        if (!body.TryGetProperty("query", out var result) || !result.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return [];
        return pages.EnumerateArray()
            .Select(p => (
                Rank: p.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : int.MaxValue,
                Title: p.TryGetProperty("title", out var t) ? t.GetString() : null,
                Url: p.TryGetProperty("fullurl", out var u) && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) ? uri : null,
                Text: p.TryGetProperty("extract", out var x) ? x.GetString() : null))
            .Where(p => p.Title is not null && p.Url is not null && !string.IsNullOrWhiteSpace(p.Text))
            .OrderBy(p => p.Rank)
            .Select(p => new Evidence(p.Title!, p.Url!, p.Text!.Trim()))
            .ToList();
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’-]*")]
    private static partial Regex Word();
}
