using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PoVoiceHud.Core.Asr;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Config;
using PoVoiceHud.Core.Providers;
using PoVoiceHud.Core.Providers.FactCheck;
using PoVoiceHud.Core.Repos;
using PoVoiceHud.Core.Sessions;
using PoVoiceHud.Core.Screen;
using PoVoiceHud.Core.Vad;

namespace PoVoiceHud.Core;

/// <summary>The connected code repositories, disposed together with the container.</summary>
public sealed class CodeRepositories(IReadOnlyList<CodeRepository> all) : IDisposable
{
    public IReadOnlyList<CodeRepository> All => all;

    public void Dispose()
    {
        foreach (var repository in all) repository.Dispose();
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>Everything below the UI. Register IAudioSource/ITranscriber/HttpClient/ModelStats first to substitute your own.</summary>
    /// <param name="repos">
    /// Each repository of <see cref="Settings.Repository"/> with its local copy (<see cref="RepoSource.Open"/>); none for none.
    /// </param>
    public static IServiceCollection AddPoVoiceHudCore(this IServiceCollection services, Settings settings, ModelPaths models,
        IReadOnlyList<(string Location, string Root)>? repos = null)
    {
        services.AddLogging();
        services.TryAddSingleton<IAudioSource>(_ => new WasapiDualCapture(AudioInput.Both));   // you and the call, always
        services.TryAddSingleton(_ => new SileroVad(models.Silero));
        services.TryAddSingleton(_ => new ModelStats());
        services.AddSingleton(_ => new CodeRepositories([.. (repos ?? []).Select(r =>
            new CodeRepository(r.Root, RepoSource.NameOf(r.Location), path => RepoSource.Link(r.Location, r.Root, path)))]));
        // Whisper is told the words it should expect: the ones in settings.json, then the repositories' names and names, as many
        // as fit in PromptFor's short budget. A repository's files change while it runs (a folder being worked on), and so does the list.
        services.TryAddSingleton<ITranscriber>(sp =>
        {
            var code = sp.GetRequiredService<CodeRepositories>().All;
            var transcriber = new WhisperTranscriber(models.Whisper, settings.WhisperThreads, Vocabulary(settings, code));
            foreach (var repository in code) repository.Changed += () => transcriber.Prompt = Vocabulary(settings, code);
            return transcriber;
        });
        services.TryAddSingleton(_ => new HttpClient());
        services.AddSingleton(sp => CreateChecker(settings, sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ModelStats>()));
        services.AddSingleton(sp => new HudPipeline(sp.GetRequiredService<IAudioSource>(), sp.GetRequiredService<SileroVad>(), sp.GetRequiredService<ITranscriber>(),
            sp.GetRequiredService<IFactChecker>(), sp.GetRequiredService<ILogger<HudPipeline>>())
        {
            Code = CreateCodeAnswerer(settings, sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<CodeRepositories>().All, sp.GetRequiredService<ModelStats>()),
            Screen = CreateScreenWatcher(settings, sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ILogger<ScreenWatcher>>(), sp.GetRequiredService<ModelStats>()),
        });
        return services;
    }

    /// <summary>
    /// The fact checker for these settings and the stored key. Also used on its own when only the checker's settings change,
    /// so Whisper and the audio keep running. Without an endpoint and key every check reports that setup is needed.
    /// </summary>
    public static IFactChecker CreateChecker(Settings settings, HttpClient http, ModelStats? stats = null) =>
        new FactChecker(Model(settings, http, stats), settings.LookUpClaims ? EvidenceSources.For(http) : null);

    /// <summary>What answers and checks sentences about the code, or null without a repository. Rebuilt with the checker.</summary>
    public static CodeAnswerer? CreateCodeAnswerer(Settings settings, HttpClient http, IReadOnlyList<CodeRepository> code, ModelStats? stats = null) =>
        code.Count == 0 ? null : new CodeAnswerer(Model(settings, http, stats, CodeAnswerer.ReplyFormat), code);

    /// <summary>What watches the screen, or null when <see cref="Settings.WatchScreen"/> is off or there is no model to read it. Rebuilt with the checker.</summary>
    public static ScreenWatcher? CreateScreenWatcher(Settings settings, HttpClient http, ILogger log, ModelStats? stats = null) =>
        settings.WatchScreen && Model(settings, http, stats, ScreenWatcher.ReplyFormat, timeout: ScreenTimeout) is { } model ? new ScreenWatcher(ScreenCapture.Capture, model, log) : null;

    // A picture takes the model longer than a sentence.
    private static readonly TimeSpan ScreenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A model client for these settings and the stored key, held to <paramref name="replyFormat"/>; null without both.</summary>
    public static FoundryClient? Model(Settings settings, HttpClient http, ModelStats? stats = null, string? replyFormat = null, int maxTokens = 300,
        TimeSpan? timeout = null)
    {
        var key = CredentialStore.Read(CredentialStore.FoundryKey);
        return FoundryClient.IsReady(settings.FoundryEndpoint, key is not null)
            ? new FoundryClient(http, new Uri(settings.FoundryEndpoint!), key, timeout: timeout, responseFormat: replyFormat, stats: stats, maxTokens: maxTokens)
            : null;
    }

    /// <summary>What sums up a meeting: a longer reply and more time than a verdict. Null without an endpoint and key.</summary>
    public static SessionSummarizer? CreateSummarizer(Settings settings, HttpClient http, ModelStats? stats = null) =>
        Model(settings, http, stats, SessionSummarizer.ReplyFormat, maxTokens: 1200, timeout: TimeSpan.FromSeconds(45)) is { } model ? new SessionSummarizer(model) : null;

    private static string? Vocabulary(Settings settings, IReadOnlyList<CodeRepository> code) =>
        WhisperTranscriber.PromptFor((settings.Vocabulary ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Concat(code.SelectMany(c => SymbolMatcher.Tokens(c.Name).Where(t => t.Length >= 3).Select(Capitalised)))
            .Concat(code.SelectMany(c => c.Index.Vocabulary())));

    private static string Capitalised(string word) => char.ToUpperInvariant(word[0]) + word[1..];
}
