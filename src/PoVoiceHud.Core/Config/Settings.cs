namespace PoVoiceHud.Core.Config;

/// <summary>Where claims, answers and summaries are sent. Written by name in settings.json.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ModelMode>))]
public enum ModelMode
{
    /// <summary>The Azure AI Foundry resource only.</summary>
    Azure,
    /// <summary>Azure, and a local model whenever Azure cannot be reached or is too slow.</summary>
    AzureThenLocal,
    /// <summary>A local model only (Ollama, Foundry Local, LM Studio): nothing leaves the PC except what the evidence switch sends.</summary>
    Local,
}

/// <summary>
/// Everything in settings.json. The Settings panel shows all of it except <see cref="WhisperThreads"/>,
/// <see cref="WhisperLanguage"/>, <see cref="Vocabulary"/> and <see cref="UpdateFeed"/>, which are for the file only. Changed with <c>with</c>, never in
/// place; the setters are plain <c>set</c> rather than <c>init</c> because the generated JSON reader (<see cref="CoreJson"/>)
/// fills every init property, so a key missing from the file would lose its default instead of keeping it.
/// </summary>
public sealed record Settings
{
    /// <summary>The Azure AI Foundry resource root, https://&lt;resource&gt;.services.ai.azure.com. The key is in Credential Manager.</summary>
    public string? FoundryEndpoint { get; set; }
    /// <summary>Azure, a local model, or Azure with the local one as a fallback.</summary>
    public ModelMode ModelMode { get; set; }
    /// <summary>An OpenAI-compatible endpoint on this PC or network, such as http://localhost:11434/v1 (Ollama).</summary>
    public string? LocalEndpoint { get; set; }
    /// <summary>The local model's name as its server knows it, such as qwen2.5:7b.</summary>
    public string? LocalModel { get; set; }
    /// <summary>Which Whisper model transcribes (<see cref="Transcription.WhisperModels"/>): bigger is more accurate and slower.</summary>
    public string WhisperModel { get; set; } = Transcription.WhisperModels.Default;
    /// <summary>Runs Whisper on the graphics card (Vulkan) when there is one that can; the CPU otherwise. Takes effect at the next start.</summary>
    public bool UseGpu { get; set; } = true;
    /// <summary>Tells the voices on the call apart ("Them 1", "Them 2") with a speaker model run on this PC.</summary>
    public bool SeparateSpeakers { get; set; } = true;
    /// <summary>After a meeting is summed up, offer to post the summary to the chat webhook (its URL is in Credential Manager).</summary>
    public bool OfferToShare { get; set; } = true;
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
    /// <summary>What Whisper listens for: "en", another ISO code, or "auto" to detect it. Only a multilingual model (large-v3 turbo) can do more than English.</summary>
    public string WhisperLanguage { get; set; } = "en";
    /// <summary>Comma-separated words Whisper should expect (names, product terms). A string so that settings compare by value.</summary>
    public string? Vocabulary { get; set; }
    /// <summary>Where new versions are published: an https URL or a folder that holds Velopack releases. Empty: no update check.</summary>
    public string? UpdateFeed { get; set; }
}
