namespace PoVoiceHud.Core.Providers;

/// <summary>A fact-check outcome; drives the line's highlight, the Facts page and the verdict sound.</summary>
public enum Verdict { Supported, Contradicted, Unverified }

public static class VerdictExtensions
{
    public static string Emoji(this Verdict verdict) => verdict switch
    {
        Verdict.Supported => "✅",
        Verdict.Contradicted => "❌",
        _ => "⚠️",
    };

    /// <summary>The words people see: plain, not the model's vocabulary.</summary>
    public static string Label(this Verdict verdict) => verdict switch
    {
        Verdict.Supported => "Correct",
        Verdict.Contradicted => "Incorrect",
        _ => "Can't tell",
    };
}

/// <summary>
/// The verdict on one spoken claim. <see cref="LinkChecked"/> means <see cref="Link"/> is a page the app fetched itself
/// (a Wikipedia article), not a URL the model wrote. <see cref="OnScreen"/> means it was judged from what is on the screen,
/// so it is listed on Screen facts rather than Facts.
/// </summary>
public sealed record Insight(string Claim, Verdict Verdict, string Reason, double Confidence, Uri? Link = null, bool LinkChecked = false, bool OnScreen = false)
{
    /// <summary>"✅ Correct — Completed in March 1889."</summary>
    public string Answer => $"{Verdict.Emoji()} {Verdict.Label()} — {Reason}";
}
