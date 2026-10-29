using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoVoiceHud.App.Interop;
using PoVoiceHud.Core;
using PoVoiceHud.Core.Asr;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Config;
using PoVoiceHud.Core.Diagnostics;
using PoVoiceHud.Core.Providers;
using PoVoiceHud.Core.Providers.FactCheck;
using PoVoiceHud.Core.Repos;
using PoVoiceHud.Core.Sessions;
using PoVoiceHud.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.ViewManagement;
using WinUIEx;

namespace PoVoiceHud.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The application object lives as long as the process; its hotkey, tray icon and container are released in OnClosed.")]
public partial class App : Application
{
    private static readonly string WindowFile = Path.Combine(AppPaths.DataDir, "window.json");
    private static readonly HttpClient Http = new();
    private static readonly SessionLog Sessions = new(SessionLog.DefaultDirectory);
    // How the model has done since the app started: every client built from here on counts into it.
    private static readonly ModelStats Stats = new();
    // Where exported days go: a folder people find, not the app's data folder.
    private static readonly string ExportDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PoVoiceHud");

    // WinUIEx persists window placement through this dictionary (string values only);
    // unpackaged apps have no ApplicationData, so we back it with a JSON file.
    private readonly Dictionary<string, object> _placement;
    // One file log for the whole process; every rebuilt container writes to it too.
    private readonly FileLoggerProvider _fileLog = new(Path.Combine(AppPaths.DataDir, "logs"));
    private readonly ILogger _log;
    private MainWindow? _window;
    private ServiceProvider? _container;
    private HudPipeline? _pipeline;
    private Settings _settings = new();
    private int _run;   // bumped by every (re)start, so a start that was overtaken throws its pipeline away
    private SettingsViewModel? _settingsPage;
    private TrayIcon? _tray;
    private DispatcherTimer? _meter;
    // A layout check (Debug builds, --demo): no audio, no call detection, and the real window placement is left alone.
    private static bool Demo { get; } = IsDemo();
    private string? _repoToken;   // the repository token the running pipeline fetched with; a new one means fetching again
    private DispatcherTimer? _calls;   // looks for a call every few seconds while "Listen only during calls" is on
    private string? _call;   // the call app using the microphone, when there is one
    private bool _callsOnly;   // "Listen only during calls", as last applied
    private int _heardSinceSummary;   // lines heard since the last meeting summary: muting after a real conversation sums it up

    public App()
    {
        InitializeComponent();
        Directory.CreateDirectory(AppPaths.DataDir);
        _log = _fileLog.CreateLogger("App");
        UnhandledException += (_, e) =>
        {
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "crash.log"), $"{DateTimeOffset.Now:O} {e.Exception}\n\n");
            // Only failures known to leave the app usable are survived (a file, the network, a Windows API call).
            // Anything else is a bug: it is logged above and allowed to end the process rather than run on in an unknown state.
            e.Handled = e.Exception is IOException or UnauthorizedAccessException or HttpRequestException or TimeoutException
                or OperationCanceledException or Win32Exception or COMException;
            _log.Log(e.Handled ? LogLevel.Error : LogLevel.Critical, e.Exception, "Unhandled exception (survived: {Handled})", e.Handled);
            if (e.Handled) _window?.ViewModel.SetStatus($"Error: {e.Exception.Message} (details in crash.log)");
        };
        // A half-written placement file means the default placement, rather than a crash on every launch.
        _placement = SettingsStore.LoadPairs(WindowFile)?.ToDictionary(p => p.Key, p => (object)p.Value) ?? [];
        WindowManager.PersistenceStorage = _placement;
    }

    private static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    private static bool IsDemo()
    {
#if DEBUG
        return Environment.GetCommandLineArgs().Contains("--demo");
#else
        return false;
#endif
    }

    /// <summary>"Build 261028 · v1.1.0": when this copy was built (YYMMDD) and its version.</summary>
    private static string BuildStamp =>
        $"Build {Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "?"} · v{Version}";

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var (settings, warning) = SettingsStore.Load(SettingsStore.DefaultPath);
        _settings = settings;
        var viewModel = new HudViewModel(OpenAsync);
        viewModel.History = new HistoryViewModel(Sessions,
            (entries, ct) => ServiceCollectionExtensions.CreateSummarizer(_settings, Http, Stats) is { } summarizer
                ? summarizer.SummarizeAsync(entries, ct)
                : throw new FactCheckUnavailableException(Connection.NotSetUp) { NeedsSetup = true },
            ExportAsync);
        _window = new MainWindow(viewModel);
        _window.Closed += OnClosed;
        _window.Activate();
        AttachSettings(settings);
        ApplyFeedback(settings);

        viewModel.PropertyChanged += (_, e) =>
        {
            // Leaving the settings without Apply (Cancel, ⚙ or Esc) discards what was typed.
            if (e.PropertyName == nameof(HudViewModel.IsSettingsOpen) && !viewModel.IsSettingsOpen) _settingsPage?.Revert();
            if (e.PropertyName != nameof(HudViewModel.IsMuted)) return;
            Listen();
            if (_tray is { } tray) tray.Tooltip = viewModel.IsMuted ? "PoVoiceHud — muted" : "PoVoiceHud — listening";
            if (_settings.Sounds) SoundPalette.Play(viewModel.IsMuted ? Cue.Muted : Cue.Unmuted);
            if (viewModel.IsMuted) _ = SummarizeMeetingAsync();
        };
        viewModel.Arrived += cue =>
        {
            if (_settings.Sounds) SoundPalette.Play(cue);
            if (cue == Cue.Contradicted) _window?.FlashIncorrect();
        };
        _meter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var ticks = 0;
        _meter.Tick += (_, _) =>
        {
            if (++ticks % 10 == 0) viewModel.ModelSummary = Stats.Summary;   // once a second is plenty for a tooltip
            if (_pipeline is not { } pipeline) return;
            viewModel.MicLevel = pipeline.Level;
            viewModel.LevelSource = pipeline.Speaker;
        };
        _meter.Start();

#if DEBUG
        // Layout checks without a microphone: --demo seeds a sample conversation (--facts opens the Facts page, --code Code facts,
        // --history History, --compact the header alone, --settings the Settings page, --setup the first-run page).
        var flags = Environment.GetCommandLineArgs();
        if (Demo)
        {
            _settings = _settings with { Sounds = false };   // the demo is for looking at, not listening to
            if (flags.Contains("--scripted")) _ = ScriptedDemoAsync();
            else _window.SeedDemo();
            if (flags.Contains("--facts")) viewModel.Page = HudPage.Facts;
            if (flags.Contains("--code")) viewModel.Page = HudPage.Code;
            if (flags.Contains("--history")) viewModel.Page = HudPage.History;
            if (flags.Contains("--compact")) viewModel.IsCompact = true;
            if (flags.Contains("--settings")) viewModel.IsSettingsOpen = true;
            if (flags.Contains("--setup")) RequireSetup(null);
            return;
        }
#endif
        _log.LogInformation("PoVoiceHud {Version} starting", Version);
        _tray = new TrayIcon(1, MainWindow.IconPath, "PoVoiceHud — listening") { IsVisible = true };
        _tray.Selected += (_, _) => ToggleVisible();
        _tray.ContextMenu += (_, e) => e.Flyout = TrayMenu();
        if (settings.StartWithWindows) Autostart.Set(true);   // keeps the entry pointing at this exe if it moved
        _ = CheckUpdatesAsync(userAsked: false);
        // Nothing is heard until there is an endpoint and key to check claims with.
        if (!FoundryClient.IsReady(settings.FoundryEndpoint, CredentialStore.Read(CredentialStore.FoundryKey) is not null)) RequireSetup(null);
        else _ = StartAsync(settings);
        if (warning is not null) viewModel.SetStatus(warning);
    }

#if DEBUG
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
            if (answerer?.IsAbout(said, context) == true && await answerer.AskAsync(said, context, CancellationToken.None) is { } fact)
                viewModel.PushCode(new CodeChecked(line, fact));
            else if (FactChecker.IsCandidate(said))
            {
                viewModel.MarkChecking(line, busy: true);
                await Task.Delay(600);
                if (await checker.CheckAsync(said, context, CancellationToken.None) is { } verdict) viewModel.Push(new Checked(line, verdict));
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
                """{"kind":"question","topic":"Which model does the app use for fact checking","evidence":"","text":"gpt-4.1-mini, fixed in FoundryClient.","verdict":"","confidence":0.95,"files":["src/PoVoiceHud.Core/Providers/FactCheck/FoundryClient.cs"]}""",
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
#endif

    /// <summary>Shows only the Settings page, saying why, until Apply finds a working endpoint and key. The microphone stays off.</summary>
    private void RequireSetup(string? why)
    {
        _window!.ViewModel.NeedsSetup = true;
        _window.ViewModel.SetStatus("Connect the fact checker to start");
        _settingsPage!.RequireSetup(why);
    }

    /// <summary>
    /// "Sounds and animations": the cues check the setting as they play, at the volume set; the animations also follow
    /// Windows' animation switch. "Listen only during calls" starts or stops looking for a call.
    /// </summary>
    private void ApplyFeedback(Settings settings)
    {
        _window!.Animations = settings.Sounds && new UISettings().AnimationsEnabled;
        SoundPalette.Volume = Math.Clamp(settings.CueVolume, 0, 100) / 100f;
        if (Demo) return;
        _callsOnly = settings.CallsOnly;
        if (settings.CallsOnly && _calls is null)
        {
            _calls = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _calls.Tick += (_, _) => LookForCall();
            _calls.Start();
            LookForCall();
        }
        else if (!settings.CallsOnly && _calls is not null)
        {
            _calls.Stop();
            _calls = null;
            _call = null;
            Listen();
        }
    }

    /// <summary>A call started or ended: listening follows it.</summary>
    private void LookForCall()
    {
        var call = CallDetector.CurrentCall();
        if (call == _call) return;
        _call = call;
        _log.LogInformation("Call {State}", call is null ? "ended" : $"started on {call}");
        Listen();
    }

    /// <summary>
    /// The pipeline hears only when not muted and, with "Listen only during calls", only while a call is on. Waiting for a
    /// call is not muting: the microphone button stays as the user left it, and the header says what it waits for.
    /// </summary>
    private void Listen()
    {
        var viewModel = _window!.ViewModel;
        var waiting = _callsOnly && _call is null;
        if (_pipeline is { } pipeline) pipeline.IsMuted = viewModel.IsMuted || waiting;
        if (viewModel.IsMuted || !_callsOnly || _pipeline is null) return;
        viewModel.SetStatus(waiting ? "Waiting for a call (Teams, Zoom, Slack, a browser call…)" : $"Listening to the call on {_call}");
    }

    /// <summary>
    /// Muting after a conversation (a dozen lines or more since the last summary) sums the day up in the background and saves
    /// it beside the history, where the History page shows it.
    /// </summary>
    private async Task SummarizeMeetingAsync()
    {
        if (Demo || _heardSinceSummary < 12 || _window?.ViewModel.History is not { } history) return;
        _heardSinceSummary = 0;
        var problem = await history.SummarizeDayAsync(DateOnly.FromDateTime(DateTime.Now), CancellationToken.None);
        _log.LogInformation("Meeting summary: {Result}", problem ?? "saved");
        if (problem is null) _window.ViewModel.SetStatus("Meeting summary saved: see History");
    }

    /// <summary>Writes a day's Markdown to Documents\PoVoiceHud and opens it.</summary>
    private async Task ExportAsync(DateOnly day, string markdown)
    {
        try
        {
            Directory.CreateDirectory(ExportDir);
            var path = Path.Combine(ExportDir, $"Meeting {day:yyyy-MM-dd}.md");
            await File.WriteAllTextAsync(path, markdown);
            _log.LogInformation("Exported {Day} to {Path}", day, path);
            await OpenAsync(new Uri(path));
            _window?.ViewModel.SetStatus($"Saved {Path.GetFileName(path)} in Documents\\PoVoiceHud");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _window?.ViewModel.SetStatus($"Export failed: {e.Message}");
        }
    }

    private async Task StartAsync(Settings settings)
    {
        var run = ++_run;
        var window = _window!;
        var viewModel = window.ViewModel;
        void Status(string text) => window.DispatcherQueue.TryEnqueue(() => viewModel.SetStatus(text));
        try
        {
            // One call to the model while the models load: a key refused or a deployment gone since last time sends the user to Settings.
            // A service that is only unreachable does not: transcription still works, and the header says why checks are paused.
            var key = CredentialStore.Read(CredentialStore.FoundryKey);
            var connection = FoundryClient.IsReady(settings.FoundryEndpoint, key is not null)
                ? Connection.TestAsync(new FoundryClient(Http, new Uri(settings.FoundryEndpoint!), key), CancellationToken.None)
                : Task.FromResult<FactCheckUnavailableException?>(new(Connection.NotSetUp) { NeedsSetup = true });

            Status("Checking models…");
            var models = await ModelStore.EnsureAsync(new Progress<double>(p => Status($"Downloading models… {p:P0}")), CancellationToken.None);

            // The code repositories, if any: a URL is cloned (the first time, which can take a while) or refreshed; a folder is read where it is.
            var repos = new List<(string Location, string Root)>();
            string? repoWarning = null;
            var repoToken = _repoToken = CredentialStore.Read(CredentialStore.RepoToken);
            foreach (var location in RepoSource.Locations(settings.Repository))
            {
                Status($"Fetching {RepoSource.NameOf(location)}…");
                var (root, warning) = await Task.Run(() => RepoSource.Open(location, repoToken));
                if (root is not null) repos.Add((location, root));
                if (warning is null) continue;
                _log.LogWarning("Repository {Location}: {Warning}", location, warning);
                repoWarning = warning;
            }

            Status("Loading models…");
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddDebug().AddProvider(_fileLog));
            services.AddSingleton(Http);
            services.AddSingleton(Stats);
            services.AddPoVoiceHudCore(settings, models, repos);
            var container = services.BuildServiceProvider();

            // Resolving the pipeline loads Whisper: keep it off the UI thread.
            var pipeline = await Task.Run(container.GetRequiredService<HudPipeline>);
            var problem = await connection;
            if (run != _run || problem is { NeedsSetup: true })
            {
                await Task.Run(container.Dispose);   // settings were saved again while this one was loading, or they no longer work
                if (run == _run)
                {
                    _log.LogWarning("Not starting: {Problem}", problem!.Message);
                    window.DispatcherQueue.TryEnqueue(() => RequireSetup(problem.Message));
                }
                return;
            }
            (_container, _pipeline) = (container, pipeline);
            pipeline.IsMuted = viewModel.IsMuted || (_callsOnly && _call is null);   // the user may have muted during the download

            _ = Drain(pipeline.Status, viewModel.SetStatus);
            _ = Drain(pipeline.Transcript, line =>
            {
                viewModel.AddTranscript(line);
                _heardSinceSummary++;
                Sessions.Append(new SessionEntry { At = DateTimeOffset.Now, Source = line.Source.ToString(), Heard = line.Text });
            });
            _ = Drain(pipeline.Checking, c => viewModel.MarkChecking(c.Line, c.Busy));
            var code = container.GetRequiredService<CodeRepositories>().All;
            viewModel.HasRepository = code.Count > 0;
            viewModel.HasRepositories = code.Count > 1;
            _ = Drain(pipeline.CodeFacts, result =>
            {
                viewModel.PushCode(result);
                Sessions.Append(new SessionEntry
                {
                    At = DateTimeOffset.Now, Source = result.Line.Source.ToString(), Claim = result.Fact.Topic,
                    Answer = $"{(result.Fact.Verdict is { } v ? $"{v.Emoji()} {v.Label()}" : "💬 Answer")} — {result.Fact.Text}",
                    Verdict = result.Fact.Verdict?.ToString() ?? "Answer", Link = result.Fact.Sources is [var first, ..] ? first.Link : null,
                });
            });
            _ = Drain(pipeline.Verdicts, result =>
            {
                viewModel.Push(result);
                Sessions.Append(new SessionEntry
                {
                    At = DateTimeOffset.Now, Source = result.Line.Source.ToString(), Claim = result.Verdict.Claim, Answer = result.Verdict.Answer,
                    Verdict = result.Verdict.Verdict.ToString(), Link = result.Verdict.Link,
                });
            });
            pipeline.Start();
            _log.LogInformation("Listening: {Model} at {Host}, Wikipedia {LookUp}, repositories {Files}", FoundryClient.Model,
                Uri.TryCreate(settings.FoundryEndpoint, UriKind.Absolute, out var host) ? host.Host : "none", settings.LookUpClaims ? "on" : "off",
                code.Count == 0 ? "none" : string.Join(", ", code.Select(c => $"{c.Name} ({c.Index.Symbols.Count} names)")));
            // After the audio's own "Listening", so it is not overwritten straight away; waiting for a call says so instead.
            if (repoWarning is not null) _ = Task.Delay(1500).ContinueWith(_ => Status(repoWarning), TaskScheduler.Default);
            else if (_callsOnly) _ = Task.Delay(1500).ContinueWith(_ => window.DispatcherQueue.TryEnqueue(Listen), TaskScheduler.Default);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Start failed");
            Status($"Start failed: {e.Message}");
        }
    }

    /// <summary>
    /// Apply in Settings lands here, after the connection check passed: the first start once setup is done; otherwise the
    /// running pipeline gets a new fact checker, or is rebuilt when what it hears changes.
    /// </summary>
    private async Task ApplyAsync(Settings settings)
    {
        var old = _settings;
        _settings = settings;
        var viewModel = _window!.ViewModel;
        if (viewModel.NeedsSetup)
        {
            viewModel.NeedsSetup = false;
            _log.LogInformation("Set up; starting");
            await StartAsync(settings);
            return;
        }
        // The endpoint, key and Wikipedia switch are the checker's, and sounds and start-up the app's: Whisper and the audio keep running.
        // A new repository or token means fetching it again, and a pipeline that is not running yet (loading, or failed) is built afresh.
        if (_pipeline is { } pipeline && CredentialStore.Read(CredentialStore.RepoToken) == _repoToken && settings with
            {
                FoundryEndpoint = old.FoundryEndpoint, LookUpClaims = old.LookUpClaims, Sounds = old.Sounds, StartWithWindows = old.StartWithWindows,
                CueVolume = old.CueVolume, CallsOnly = old.CallsOnly,
            } == old)
        {
            pipeline.Checker = ServiceCollectionExtensions.CreateChecker(settings, Http, Stats);
            pipeline.Code = ServiceCollectionExtensions.CreateCodeAnswerer(settings, Http, _container?.GetService<CodeRepositories>()?.All ?? [], Stats);
            _log.LogInformation("Settings applied; new fact checker");
            viewModel.SetStatus("Settings applied");
            return;
        }
        await RestartAsync(settings);
    }

    /// <summary>Disposes the whole container (capture, Whisper) and builds a new one from <paramref name="settings"/>.</summary>
    private async Task RestartAsync(Settings settings)
    {
        var viewModel = _window!.ViewModel;
        viewModel.ResetChecking();
        viewModel.MicLevel = 0;

        var old = _container;
        (_container, _pipeline) = (null, null);
        _run++;   // a start still loading must not install its pipeline after this point
        if (old is not null) await Task.Run(old.Dispose);   // stops capture and unloads Whisper; not for the UI thread
        _log.LogInformation("Settings applied; restarting the pipeline");
        await StartAsync(settings);
    }

    private async Task CheckUpdatesAsync(bool userAsked)
    {
        var message = await Updates.CheckAsync(_settings.UpdateFeed, _log, userAsked);
        if (message is not null) _window?.ViewModel.SetStatus(message);
    }

    /// <summary>The tray menu: built each time it opens so its labels match the current state.</summary>
    private MenuFlyout TrayMenu()
    {
        static MenuFlyoutItem Item(string text, Action run)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => run();
            return item;
        }
        var menu = new MenuFlyout();
        var viewModel = _window!.ViewModel;
        menu.Items.Add(Item(_window.AppWindow.IsVisible ? "Hide the HUD" : "Show the HUD", ToggleVisible));
        if (viewModel.ShowsPages) menu.Items.Add(Item(viewModel.IsMuted ? "Unmute" : "Mute", () => viewModel.ToggleMuteCommand.Execute(null)));
        menu.Items.Add(Item("Open the history folder", () => OpenFolder(Sessions.Directory)));
        menu.Items.Add(Item("Copy diagnostics", () => _ = CollectDiagnosticsAsync()));
        menu.Items.Add(Item("Check for updates", () => _ = CheckUpdatesAsync(userAsked: true)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Quit PoVoiceHud", _window.Close));
        return menu;
    }

    private void ToggleVisible()
    {
        if (_window!.AppWindow.IsVisible) _window.AppWindow.Hide();
        else _window.AppWindow.Show();
    }

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Zips logs and settings into %TEMP%, puts the zip on the clipboard and shows it in Explorer.</summary>
    private async Task CollectDiagnosticsAsync()
    {
        var zip = Path.Combine(Path.GetTempPath(), $"PoVoiceHud-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var settings = _settings;
        var environment = string.Join("\n",
            $"PoVoiceHud {Version} ({(IsInstalled() ? "installed" : "not installed: run from a build folder")})",
            $"Windows {Environment.OSVersion.Version} · {RuntimeInformation.FrameworkDescription} · {Environment.ProcessorCount} logical cores",
            $"Audio: microphone and call · {FoundryClient.Model} at {(Uri.TryCreate(settings.FoundryEndpoint, UriKind.Absolute, out var endpoint) ? endpoint.Host : "none")}" +
            $" · key stored: {(CredentialStore.Read(CredentialStore.FoundryKey) is null ? "no" : "yes")}",
            $"Wikipedia: {(settings.LookUpClaims ? "on" : "off")} · repository: {(settings.Repository is null ? "none" : RepoSource.IsRemote(settings.Repository) ? "URL" : "folder")}" +
            $" · repository token stored: {(CredentialStore.Read(CredentialStore.RepoToken) is null ? "no" : "yes")} · start with Windows: {(settings.StartWithWindows ? "on" : "off")}" +
            $" · update feed: {(settings.UpdateFeed is null ? "none" : "set")}",
            "");
        try
        {
            await Task.Run(() => DiagnosticsBundle.Create(AppPaths.DataDir, zip, environment));
            var data = new DataPackage();
            data.SetStorageItems([await Windows.Storage.StorageFile.GetFileFromPathAsync(zip)]);
            Clipboard.SetContent(data);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true })?.Dispose();
            _log.LogInformation("Diagnostics written to {Zip}", zip);
            _window?.ViewModel.SetStatus($"{Path.GetFileName(zip)} is on the clipboard");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _window?.ViewModel.SetStatus($"Diagnostics failed: {e.Message}");
        }
    }

    private static bool IsInstalled()
    {
        try
        {
            return new Velopack.UpdateManager(AppContext.BaseDirectory).IsInstalled;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or Velopack.Exceptions.NotInstalledException)
        {
            return false;
        }
    }

    private void AttachSettings(Settings settings)
    {
        var page = new SettingsViewModel(settings,
            readSecret: CredentialStore.Read,
            writeSecret: CredentialStore.Write,
            save: s =>
            {
                SettingsStore.Save(s, SettingsStore.DefaultPath);
                if (s.StartWithWindows != _settings.StartWithWindows && !Autostart.Set(s.StartWithWindows))
                    _window!.ViewModel.SetStatus("Windows did not allow changing the startup list.");
                ApplyFeedback(s);
                _ = ApplyAsync(s);
            },
            connect: (endpoint, key) => new FoundryClient(Http, endpoint, key)) { BuildStamp = BuildStamp };
        page.Done += () => _window!.ViewModel.IsSettingsOpen = false;
        _settingsPage = page;
        _window!.AttachSettings(page, quit: () => _window!.Close());
    }

    private async Task Drain<T>(ChannelReader<T> reader, Action<T> apply)
    {
        await foreach (var item in reader.ReadAllAsync())
            _window!.DispatcherQueue.TryEnqueue(() => apply(item));
    }

    /// <summary>A fact's source link goes through the shell.</summary>
    private async Task OpenAsync(Uri uri)
    {
        if (!await Windows.System.Launcher.LaunchUriAsync(uri))
            _window?.ViewModel.SetStatus($"Nothing is registered to open {uri.Scheme}:// links");
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (!Demo) SettingsStore.SavePairs(_placement.ToDictionary(p => p.Key, p => p.Value.ToString() ?? ""), WindowFile);
        _log.LogInformation("Closing");
        _meter?.Stop();
        _calls?.Stop();
        _tray?.Dispose();
        _container?.Dispose();
    }
}
