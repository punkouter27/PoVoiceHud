namespace PoVoiceHud.Core.Providers;

/// <summary>
/// The words that make a spoken sentence a question, in one place for the fact checker (which skips questions) and the code
/// answerer (which answers them). Regex alternations, so they can go straight into a pattern.
/// </summary>
public static class SpokenText
{
    /// <summary>What, which, where, who, why, how. "When" is left out: it starts statements as often ("When the app starts, …").</summary>
    public const string QuestionWords = "what|which|where|who|why|how";

    /// <summary>The verbs a question puts first, or right after its question word: "Is it…", "Which model does…".</summary>
    public const string AskingVerbs = "is|are|was|were|do|does|did|can|could|should|will|would|has|have";

    /// <summary>Both lists as words, for a lookup rather than a pattern.</summary>
    public static IEnumerable<string> Words(string alternation) => alternation.Split('|');
}
