using System.Text.RegularExpressions;
using Raffinert.FuzzySharp;

namespace PoVoiceHud.Core.Code;

public sealed record SymbolMatch(Symbol Symbol, double Score);

/// <summary>
/// Scores each symbol by the fraction of its name tokens heard in the sentence (camel-split, fuzzy ≥ 80 so
/// "inspections"/"tenent" still count). Returns the top five at ≥ 0.5; callers choose their own bar.
/// </summary>
public static partial class SymbolMatcher
{
    /// <summary>Score at which a symbol is taken to be what the speaker meant.</summary>
    public const double Strong = 0.8;

    private const int TokenSimilarity = 80;
    // Below this length a near miss is another word ("we"/"web", "there"/"theme", "it"/"kit"), not a mishearing: only exact counts.
    private const int FuzzyFrom = 6;

    // A one-word name only counts when it is a code file ("Gateway.cs"), not a config or doc ("web.config", "tasks.json").
    private static readonly HashSet<string> CodeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".razor", ".cshtml", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".kt", ".go", ".rs", ".rb", ".php",
        ".swift", ".cpp", ".cc", ".c", ".h", ".hpp", ".sql", ".xaml", ".vue", ".svelte", ".dart", ".scala",
    };
    private static readonly HashSet<string> Ignored = ["md", "cs"];

    public static IReadOnlyList<SymbolMatch> Match(string text, SymbolIndex index)
    {
        var spoken = Tokens(text).ToHashSet();
        if (spoken.Count == 0) return [];

        // Names share tokens ("service", "controller"), so each one is fuzzy-matched against the sentence only once.
        var heardCache = new Dictionary<string, bool>();
        bool Heard(string token)
        {
            if (!heardCache.TryGetValue(token, out var heard))
                heardCache[token] = heard = spoken.Contains(token) || spoken.Any(s => Similar(token, s));
            return heard;
        }

        var matches = new List<SymbolMatch>();
        foreach (var symbol in index.Symbols)
        {
            if (symbol.Tokens.Length == 0) continue;
            var heard = symbol.Tokens.Count(Heard);
            var score = (double)heard / symbol.Tokens.Length;
            if (score >= 0.5) matches.Add(new SymbolMatch(symbol, score));
        }
        return matches.OrderByDescending(m => m.Score).ThenByDescending(m => m.Symbol.Name.Length).Take(5).ToList();
    }

    /// <summary>
    /// The symbols a sentence clearly names. A one-word name counts only for an entity or a code file, and not one every
    /// codebase has ("Program", "Settings"): "the program was written in 1991" is not about Program.cs, nor "we talked" about web.config.
    /// </summary>
    public static IEnumerable<SymbolMatch> Mentions(IEnumerable<SymbolMatch> symbols) =>
        symbols.Where(m => m.Score >= Strong && (m.Symbol.Tokens.Length > 1 || (!SymbolIndex.Generic.Contains(m.Symbol.Name)
            && (m.Symbol.Kind == SymbolKind.Entity || CodeFiles.Contains(System.IO.Path.GetExtension(m.Symbol.Path))))));

    /// <summary>
    /// Every word of <paramref name="name"/> with three letters or more is in the sentence, allowing small mishearings:
    /// "PoPunkouterSoftware" is said as "the Punkouter software site".
    /// </summary>
    public static bool Says(string sentence, string name)
    {
        var wanted = Tokens(name).Where(t => t.Length >= 3).ToList();
        var spoken = Tokens(sentence).ToList();
        return wanted.Count > 0 && wanted.All(w => spoken.Any(s => s == w || Similar(w, s)));
    }

    /// <summary>Camel-case / punctuation split, lower-cased, single characters dropped.</summary>
    public static IEnumerable<string> Tokens(string text) =>
        Token().Matches(text).Select(m => m.Value.ToLowerInvariant()).Where(t => t.Length > 1);

    /// <summary>Tokens of a symbol name worth matching: no file extensions, no bare numbers.</summary>
    internal static string[] NameTokens(string name) =>
        Tokens(name).Where(t => !Ignored.Contains(t) && !t.All(char.IsDigit)).ToArray();

    // Length gate first: a ratio ≥ 80 needs the lengths within ~20 % of each other, which skips most Levenshtein calls.
    private static bool Similar(string a, string b) =>
        a.Length >= FuzzyFrom && b.Length >= FuzzyFrom && Math.Abs(a.Length - b.Length) * 5 <= a.Length + b.Length && Fuzz.Ratio(a, b) >= TokenSimilarity;

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+")]
    private static partial Regex Token();
}
