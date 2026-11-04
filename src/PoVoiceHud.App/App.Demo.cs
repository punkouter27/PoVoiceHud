#if DEBUG
using PoVoiceHud.App.Interop;
using PoVoiceHud.Core;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Code;
using PoVoiceHud.Core.FactCheck;
using PoVoiceHud.Core.Hud;
using PoVoiceHud.Core.Models;
using WinUIEx;

namespace PoVoiceHud.App;

/// <summary>Debug builds only: the --demo layout checks and the scripted demo.</summary>
public partial class App
{
    /// <summary>
    /// Layout checks without a microphone: --demo seeds a sample conversation (--facts opens the Facts page, --code Code facts,
    /// --screen Screen facts, --history History, --compact the header alone, --settings the Settings page, --setup the first-run page,
    /// --share the offer to post a meeting's summary).
    /// </summary>
    private void ShowDemo(HudViewModel viewModel)
    {
        var flags = Environment.GetCommandLineArgs();
        _settings = _settings with { Sounds = false };   // the demo is for looking at, not listening to
        CaptureExclusion.Set(_window!.GetWindowHandle(), false);   // the demo watches nothing, and is there to be captured
        if (flags.Contains("--scripted")) _ = ScriptedDemoAsync();
        else _window!.SeedDemo();
        if (flags.Contains("--facts")) viewModel.Page = HudPage.Facts;
        if (flags.Contains("--code")) viewModel.Page = HudPage.Code;
        if (flags.Contains("--screen")) viewModel.Page = HudPage.Screen;
        if (flags.Contains("--history")) viewModel.Page = HudPage.History;
        if (flags.Contains("--compact")) viewModel.IsCompact = true;
        if (flags.Contains("--settings")) viewModel.IsSettingsOpen = true;
        if (flags.Contains("--setup")) RequireSetup(null);
        if (flags.Contains("--share"))
            viewModel.OfferShare($"Summary of the meeting from {DateTime.Now.AddMinutes(-45):t} is ready.", "Post to Slack", () => Task.FromResult("Demo: nothing was posted"));
    }

    /// <summary>
    /// --demo --scripted: sentences go through the real routing, fact checker and code answerer — with a model that gives
    /// canned replies, and this source tree as the code repository — one every second or so. No audio, no network.
    /// </summary>
    private async Task ScriptedDemoAsync()
    {
        var viewModel = _window!.ViewModel;
        viewModel.SetStatus("Scripted demo: a fake model, this repository as the code");
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, ".git"))) root = Path.GetDirectoryName(root);
        using var code = root is null ? null : await Task.Run(() => new CodeRepository(root, "PoVoiceHud", path => RepoSource.Link(null, root, path)));
        viewModel.HasRepository = code is not null;
        var model = new ScriptedModel();
        var checker = new FactChecker(model);
        var answerer = code is null ? null : new CodeAnswerer(model, code);
        (SourceHint Who, string Said)[] script =
        [
            (SourceHint.Teams, "Okay, let's go through the release notes."),
            (SourceHint.Mic, "The Eiffel Tower was completed in 1889."),
            (SourceHint.Teams, "The Moon is bigger than the Earth."),
            (SourceHint.Mic, "Which model does are app use for fact checking."),
            (SourceHint.Teams, "Our app stores the API key in a text file."),
            (SourceHint.Mic, "Octopuses have three hearts."),
        ];
        var context = new List<string>();
        foreach (var (who, said) in script)
        {
            await Task.Delay(900);
            var line = new TranscriptLine(who, DateTimeOffset.Now, said);
            viewModel.AddTranscript(line);
            if (answerer?.IsAbout(said, context) == true && await answerer.AskAsync(said, context, null, CancellationToken.None) is { } fact)
                viewModel.PushCode(new CodeChecked(line, fact));
            else if (FactChecker.IsCandidate(said))
            {
                viewModel.MarkChecking(line, busy: true);
                await Task.Delay(600);
                if (await checker.CheckAsync(said, context, null, CancellationToken.None) is { } verdict) viewModel.Push(new Checked(line, verdict));
                viewModel.MarkChecking(line, busy: false);
            }
            context.Add(said);
        }
    }

    /// <summary>Canned replies in the shapes the real model is held to: by the sentence, a verdict or a code fact.</summary>
    private sealed class ScriptedModel : ILanguageModel
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) => Task.FromResult(Reply(Said(user)));

        private static string Reply(string said) => said switch
        {
            _ when said.Contains("which model", StringComparison.OrdinalIgnoreCase) =>
                """{"kind":"question","topic":"Which model does the app use for fact checking","evidence":"","text":"gpt-4.1-mini, fixed in FoundryClient.","verdict":"","confidence":0.95,"files":["src/PoVoiceHud.Core/Models/FoundryClient.cs"]}""",
            _ when said.Contains("API key", StringComparison.Ordinal) =>
                """{"kind":"statement","topic":"The app stores the API key in a text file","evidence":"src/PoVoiceHud.Core/Config/CredentialStore.cs","text":"It is kept in Windows Credential Manager.","verdict":"contradicted","confidence":0.9,"files":["src/PoVoiceHud.Core/Config/CredentialStore.cs"]}""",
            _ when said.Contains("Moon", StringComparison.Ordinal) =>
                """{"claim":"The Moon is bigger than the Earth","reason":"The Moon is about a quarter of Earth's diameter.","source":"https://en.wikipedia.org/wiki/Moon","verdict":"contradicted","confidence":0.98}""",
            _ when said.Contains("Octopuses", StringComparison.Ordinal) =>
                """{"claim":"Octopuses have three hearts","reason":"Two pump blood through the gills and one through the body.","source":"","verdict":"supported","confidence":0.85}""",
            _ when said.Contains("Eiffel", StringComparison.Ordinal) =>
                """{"claim":"The Eiffel Tower was completed in 1889","reason":"It opened in March 1889.","source":"https://en.wikipedia.org/wiki/Eiffel_Tower","verdict":"supported","confidence":0.97}""",
            _ => """{"claim":"","reason":"","source":"","verdict":"not_a_claim","confidence":1}""",
        };

        /// <summary>The sentence being judged, not its context: "said" for the code, "statement" for the fact checker.</summary>
        private static string Said(string user)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(user);
            return (doc.RootElement.TryGetProperty("said", out var said) ? said : doc.RootElement.GetProperty("statement")).GetString() ?? "";
        }
    }
}
#endif
