namespace PoVoiceHud.Core.Config;

/// <summary>Which devices <see cref="Audio.WasapiDualCapture"/> opens. The app always opens both: you and the call.</summary>
public enum AudioInput { Mic, Remote, Both }

/// <summary>
/// Everything in settings.json. The Settings panel shows all of it except <see cref="WhisperThreads"/>,
/// <see cref="Vocabulary"/> and <see cref="UpdateFeed"/>, which are for the file only. Changed with <c>with</c>, never in
/// place; the setters are plain <c>set</c> rather than <c>init</c> because the generated JSON reader (<see cref="CoreJson"/>)
/// fills every init property, so a key missing from the file would lose its default instead of keeping it.
/// </summary>
public sealed record Settings
{
    /// <summary>The Azure AI Foundry resource root, https://&lt;resource&gt;.services.ai.azure.com. The key is in Credential Manager.</summary>
    public string? FoundryEndpoint { get; set; }
    /// <summary>Looks each claim up on Wikipedia before the model judges it, so the verdict can cite a real article.</summary>
    public bool LookUpClaims { get; set; } = true;
    /// <summary>
    /// The code repository claims about the code are checked against, optional: an https git URL (GitHub or Azure DevOps,
    /// cloned into the app's cache) or a folder holding a git repository. Its access token is in Credential Manager.
    /// </summary>
    public string? Repository { get; set; }
    public bool StartWithWindows { get; set; }
    /// <summary>
    /// Captures the monitor in use every few seconds and, when it has changed, has the model describe it, so what is said can be
    /// checked against what is shown. The pictures go to the Foundry resource and are never saved; the descriptions are logged.
    /// </summary>
    public bool WatchScreen { get; set; }
    /// <summary>Short cues for verdicts and mute (each under the 300 ms the segmenter ignores) and the highlight's fade.</summary>
    public bool Sounds { get; set; } = true;
    /// <summary>How loud the cues are, 0–100.</summary>
    public int CueVolume { get; set; } = 100;
    /// <summary>
    /// Listen only while a call app (Teams, Zoom, Slack, a browser call…) is using the microphone; otherwise the app waits,
    /// hearing nothing.
    /// </summary>
    public bool CallsOnly { get; set; }
    /// <summary>Half the logical cores, 2–8: beyond 8 Whisper gains little, and all cores would starve the call itself.</summary>
    public int WhisperThreads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    /// <summary>Comma-separated words Whisper should expect (names, product terms). A string so that settings compare by value.</summary>
    public string? Vocabulary { get; set; }
    /// <summary>Where new versions are published: an https URL or a folder that holds Velopack releases. Empty: no update check.</summary>
    public string? UpdateFeed { get; set; }
}
