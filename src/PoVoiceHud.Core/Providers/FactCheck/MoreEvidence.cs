using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoVoiceHud.Core.Providers.FactCheck;

/// <summary>Where the fact checker looks claims up.</summary>
public static class EvidenceSources
{
    /// <summary>
    /// Wikipedia's article intros, Wikidata's numbers and dates for the thing named, and — for a claim about the present —
    /// recent news headlines: all asked at once, so the slowest sets the wait (each gives up after 2.5 s).
    /// </summary>
    public static IEvidenceSource For(HttpClient http) => new CombinedEvidence(new WikipediaEvidence(http), new WikidataEvidence(http), new NewsEvidence(http));
}

/// <summary>Several sources asked at once; their passages in the order the sources are given.</summary>
public sealed class CombinedEvidence(params IEvidenceSource[] sources) : IEvidenceSource
{
    public async Task<IReadOnlyList<Evidence>> FindAsync(string claim, CancellationToken ct) =>
        [.. (await Task.WhenAll(sources.Select(s => s.FindAsync(claim, ct)))).SelectMany(found => found)];
}

/// <summary>Best-effort HTTP for the evidence sources: JSON or nothing, within the time limit.</summary>
internal static partial class EvidenceHttp
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2.5);

    public static async Task<JsonDocument> GetAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Wikimedia asks every client to name itself, and the others do no harm by it.
        request.Headers.UserAgent.ParseAdd("PoVoiceHud/1.1 (Windows desktop fact-check HUD)");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    /// <summary>Runs a lookup within the time limit; a slow, failed or unreadable one returns nothing.</summary>
    public static async Task<IReadOnlyList<Evidence>> TryAsync(Func<CancellationToken, Task<IReadOnlyList<Evidence>>> lookup, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            return await lookup(limit.Token);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException
            || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return [];
        }
    }

    // Capitalised at the start of a sentence, but not names.
    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "The", "A", "An", "It", "Its", "This", "That", "These", "Those", "He", "She", "They", "We", "I", "Our", "In", "On", "At", "Of",
    };

    /// <summary>The names in a claim ("The Eiffel Tower is 330 m tall" → "Eiffel Tower"): what an encyclopedia entry is found by.</summary>
    public static string Names(string claim) =>
        string.Join(' ', Word().Matches(claim).Select(m => m.Value).Where(w => char.IsUpper(w[0]) && !NotNames.Contains(w)));

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’-]*")]
    public static partial Regex Word();
}

/// <summary>
/// Wikidata (no key, no account): the entity the claim names and its key facts — when it was made, born or founded, how
/// tall, how many people, where, who leads it — as one short passage. Numbers and dates are where a model's memory slips.
/// Only the names in the claim leave the machine.
/// </summary>
public sealed class WikidataEvidence(HttpClient http) : IEvidenceSource
{
    private static readonly Uri Api = new("https://www.wikidata.org/w/api.php");

    // The properties worth quoting, in the order they read best.
    private static readonly (string Id, string Name)[] Properties =
    [
        ("P31", "instance of"), ("P17", "country"), ("P131", "located in"), ("P36", "capital"), ("P1376", "capital of"),
        ("P35", "head of state"), ("P6", "head of government"), ("P1082", "population"), ("P2046", "area"),
        ("P2048", "height"), ("P2044", "elevation above sea level"), ("P2067", "mass"), ("P2386", "diameter"),
        ("P571", "inception"), ("P580", "start time"), ("P582", "end time"), ("P576", "dissolved"), ("P569", "date of birth"),
        ("P570", "date of death"), ("P19", "place of birth"), ("P112", "founded by"), ("P170", "creator"), ("P178", "developer"),
        ("P50", "author"), ("P84", "architect"), ("P577", "publication date"), ("P1449", "nickname"),
    ];

    public Task<IReadOnlyList<Evidence>> FindAsync(string claim, CancellationToken ct) => EvidenceHttp.TryAsync(async token =>
    {
        var names = EvidenceHttp.Names(claim);
        if (names.Length == 0) return [];
        using var search = await EvidenceHttp.GetAsync(http, Query("wbsearchentities", $"search={Uri.EscapeDataString(names)}&language=en&limit=1"), token);
        if (!search.RootElement.TryGetProperty("search", out var hits) || hits.GetArrayLength() == 0) return [];
        var id = hits[0].GetProperty("id").GetString()!;

        using var entity = await EvidenceHttp.GetAsync(http, Query("wbgetentities", $"ids={id}&props=labels%7Cdescriptions%7Cclaims&languages=en"), token);
        var item = entity.RootElement.GetProperty("entities").GetProperty(id);
        var label = Text(item, "labels") ?? id;
        var claims = item.GetProperty("claims");
        var facts = Properties.Where(p => claims.TryGetProperty(p.Id, out _)).Select(p => (p.Name, Value: Best(claims.GetProperty(p.Id)))).Where(f => f.Value.HasValue).ToList();
        if (facts.Count == 0) return [];

        // Things and units are ids ("Q90", "Q11573"): one more call names them.
        var ids = facts.SelectMany(f => new[] { f.Value!.Value.Entity, f.Value.Value.Unit }).OfType<string>().Distinct().Take(50).ToList();
        var labels = new Dictionary<string, string>();
        if (ids.Count > 0)
        {
            using var named = await EvidenceHttp.GetAsync(http, Query("wbgetentities", $"ids={string.Join("%7C", ids)}&props=labels&languages=en"), token);
            foreach (var e in named.RootElement.GetProperty("entities").EnumerateObject())
                if (Text(e.Value, "labels") is { } name) labels[e.Name] = name;
        }
        var lines = facts.Select(f => $"{f.Name}: {Say(f.Value!.Value, labels)}");
        var description = Text(item, "descriptions") is { } d ? $" ({d})" : "";
        return [new Evidence($"{label} (Wikidata)", new Uri($"https://www.wikidata.org/wiki/{id}"), $"{label}{description}. {string.Join("; ", lines)}.")];
    }, ct);

    private readonly record struct Value(string? Text, string? Entity, string? Unit);

    private static Uri Query(string action, string rest) => new($"{Api}?action={action}&format=json&{rest}");

    private static string? Text(JsonElement item, string field) =>
        item.TryGetProperty(field, out var all) && all.TryGetProperty("en", out var en) && en.TryGetProperty("value", out var v) ? v.GetString() : null;

    /// <summary>The statement to quote: the preferred one if marked, else the latest (a population is listed year by year).</summary>
    private static Value? Best(JsonElement statements)
    {
        var all = statements.EnumerateArray().Where(s => s.TryGetProperty("rank", out var r) && r.GetString() != "deprecated").ToList();
        var pick = all.FirstOrDefault(s => s.GetProperty("rank").GetString() == "preferred");
        if (pick.ValueKind == JsonValueKind.Undefined && all.Count > 0) pick = all[^1];
        if (pick.ValueKind == JsonValueKind.Undefined || !pick.TryGetProperty("mainsnak", out var snak) || !snak.TryGetProperty("datavalue", out var data)) return null;
        var value = data.GetProperty("value");
        return data.GetProperty("type").GetString() switch
        {
            "wikibase-entityid" => new Value(null, value.GetProperty("id").GetString(), null),
            "quantity" => new Value(Amount(value.GetProperty("amount").GetString()), null,
                value.GetProperty("unit").GetString() is { } unit && unit.Contains("/entity/", StringComparison.Ordinal) ? unit[(unit.LastIndexOf('/') + 1)..] : null),
            "time" => new Value(Date(value.GetProperty("time").GetString(), value.GetProperty("precision").GetInt32()), null, null),
            "monolingualtext" => new Value(value.GetProperty("text").GetString(), null, null),
            "string" => new Value(value.GetString(), null, null),
            _ => null,
        };
    }

    private static string Say(Value value, Dictionary<string, string> labels) =>
        value.Entity is { } id ? labels.GetValueOrDefault(id, id)
        : value.Unit is { } unit ? $"{value.Text} {labels.GetValueOrDefault(unit, "")}".TrimEnd()
        : value.Text ?? "";

    /// <summary>"+2102650" → "2,102,650"; "+330" → "330".</summary>
    private static string? Amount(string? amount) =>
        decimal.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n.ToString("#,0.###", CultureInfo.InvariantCulture) : amount;

    /// <summary>"+1889-03-31T00:00:00Z" at day, month or year precision (11, 10, 9); BCE years keep their minus.</summary>
    private static string? Date(string? time, int precision)
    {
        if (time is null || time.Length < 11) return time;
        var bce = time[0] == '-';
        var parts = time[1..].Split('T')[0].Split('-');
        var year = parts[0].TrimStart('0') + (bce ? " BCE" : "");
        return precision switch
        {
            >= 11 => $"{year}-{parts[1]}-{parts[2]}",
            10 => $"{year}-{parts[1]}",
            _ => year,
        };
    }
}

/// <summary>
/// For a claim about the present ("the current CEO", "still the tallest"): headlines of recent news that match it, from
/// GDELT's free article search. A model's knowledge stops in mid-2024; a dated headline from this week does not. Only the
/// claim's content words leave the machine.
/// </summary>
public sealed class NewsEvidence(HttpClient http) : IEvidenceSource
{
    private const int Articles = 3;
    private static readonly Uri Api = new("https://api.gdeltproject.org/api/v2/doc/doc");

    public Task<IReadOnlyList<Evidence>> FindAsync(string claim, CancellationToken ct) => EvidenceHttp.TryAsync(async token =>
    {
        if (!FactChecker.AboutNow(claim)) return [];
        var words = EvidenceHttp.Word().Matches(claim).Select(m => m.Value)
            .Where(w => w.Length >= 3 && !FactChecker.IsNowWord(w) && (char.IsUpper(w[0]) || char.IsDigit(w[0]))).Take(5).ToList();
        if (words.Count == 0) return [];
        var query = Uri.EscapeDataString(string.Join(' ', words) + " sourcelang:english");
        using var found = await EvidenceHttp.GetAsync(http, new Uri($"{Api}?query={query}&mode=artlist&maxrecords={Articles}&sort=datedesc&format=json"), token);
        if (!found.RootElement.TryGetProperty("articles", out var articles)) return [];
        return [.. articles.EnumerateArray()
            .Select(a => (Title: a.GetProperty("title").GetString(), Url: a.GetProperty("url").GetString(),
                Seen: a.TryGetProperty("seendate", out var s) ? s.GetString() : null, Site: a.TryGetProperty("domain", out var d) ? d.GetString() : null))
            .Where(a => !string.IsNullOrWhiteSpace(a.Title) && Uri.TryCreate(a.Url, UriKind.Absolute, out _))
            .Select(a => new Evidence(a.Title!, new Uri(a.Url!), $"News headline from {a.Site}, {Seen(a.Seen)}: {a.Title}"))];
    }, ct);

    /// <summary>"20261027T143000Z" → "2026-10-27".</summary>
    private static string Seen(string? seen) =>
        seen is { Length: >= 8 } ? $"{seen[..4]}-{seen[4..6]}-{seen[6..8]}" : "recently";
}
