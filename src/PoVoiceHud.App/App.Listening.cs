using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using PoVoiceHud.App.Interop;
using PoVoiceHud.Core;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Code;
using PoVoiceHud.Core.Config;
using PoVoiceHud.Core.FactCheck;
using PoVoiceHud.Core.Meetings;
using PoVoiceHud.Core.Models;
using PoVoiceHud.Core.Screen;
using PoVoiceHud.Core.Transcription;
using Windows.UI.ViewManagement;
using WinUIEx;

namespace PoVoiceHud.App;

/// <summary>Listening: starting the pipeline, applying settings to it, and following calls and mute.</summary>
public partial class App
{
    /// <summary>
    /// "Sounds and animations": the cues check the setting as they play, at the volume set; the animations also follow
    /// Windows' animation switch. "Listen only during calls" starts or stops looking for a call.
    /// </summary>
    private void ApplyFeedback(Settings settings)
    {
        _window!.Animations = settings.Sounds && new UISettings().AnimationsEnabled;
        SoundPalette.Volume = Math.Clamp(settings.CueVolume, 0, 100) / 100f;
        // While the screen is watched the HUD stays out of the pictures (and out of screen sharing), so it never checks its own verdicts.
        CaptureExclusion.Set(_window.GetWindowHandle(), settings.WatchScreen && !Demo);
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
        var ended = _call is not null && call is null;
        _call = call;
        _log.LogInformation("Call {State}", call is null ? "ended" : $"started on {call}");
        Listen();
        if (ended) _ = SummarizeMeetingAsync();   // the end of a call is the end of a meeting, as muting after one is
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
            // In local mode that is the local model; with Azure first, Azure (the fallback was checked when it was applied).
            var connection = ServiceCollectionExtensions.Primary(settings, Http) is { } primary
                ? Connection.TestAsync(primary, CancellationToken.None)
                : Task.FromResult<FactCheckUnavailableException?>(new(Connection.NotSetUp) { NeedsSetup = true });

            Status("Checking models…");
            var whisper = WhisperModels.Find(settings.WhisperModel);
            var models = await ModelStore.EnsureAsync(whisper.Id, settings.SeparateSpeakers,
                new Progress<double>(p => Status($"Downloading Whisper {whisper.Id}… {p:P0}")), CancellationToken.None);

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
                _meetingStart ??= line.At;
                Sessions.Append(new SessionEntry { At = DateTimeOffset.Now, Source = line.Source.ToString(), Voice = line.Voice > 0 ? line.Voice : null, Heard = line.Text });
            });
            _ = Drain(pipeline.Checking, c => viewModel.MarkChecking(c.Line, c.Busy));
            var code = container.GetRequiredService<CodeRepositories>().All;
            viewModel.HasRepository = code.Count > 0;
            viewModel.HasRepositories = code.Count > 1;
            viewModel.WatchesScreen = pipeline.Screen is not null;
            _ = Drain(pipeline.Screens, seen =>
            {
                viewModel.ShowScreen(seen);
                Sessions.Append(new SessionEntry { At = seen.At, Screen = seen.Summary });
            });
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
            _log.LogInformation("Listening: {Model}, Whisper {Whisper} on the {Runtime}, voices {Voices}, Wikipedia {LookUp}, repositories {Files}, screen {Screen}",
                ServiceCollectionExtensions.Describe(settings), whisper.Id, WhisperTranscriber.Runtime, pipeline.Voices is null ? "not told apart" : "told apart",
                settings.LookUpClaims ? "on" : "off",
                code.Count == 0 ? "none" : string.Join(", ", code.Select(c => $"{c.Name} ({c.Index.Symbols.Count} names)")), pipeline.Screen is null ? "off" : "watched");
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
        // The endpoint, key, Wikipedia and screen switches are the checker's, and sounds and start-up the app's: Whisper and the audio keep running.
        // A new repository or token means fetching it again, and a pipeline that is not running yet (loading, or failed) is built afresh.
        if (_pipeline is { } pipeline && CredentialStore.Read(CredentialStore.RepoToken) == _repoToken && settings with
            {
                FoundryEndpoint = old.FoundryEndpoint, LookUpClaims = old.LookUpClaims, Sounds = old.Sounds, StartWithWindows = old.StartWithWindows,
                CueVolume = old.CueVolume, CallsOnly = old.CallsOnly, WatchScreen = old.WatchScreen, ModelMode = old.ModelMode,
                LocalEndpoint = old.LocalEndpoint, LocalModel = old.LocalModel, OfferToShare = old.OfferToShare, UseGpu = old.UseGpu,
            } == old)
        {
            pipeline.Checker = ServiceCollectionExtensions.CreateChecker(settings, Http, Stats);
            pipeline.Code = ServiceCollectionExtensions.CreateCodeAnswerer(settings, Http, _container?.GetService<CodeRepositories>()?.All ?? [], Stats);
            pipeline.Screen = ServiceCollectionExtensions.CreateScreenWatcher(settings, Http, _fileLog.CreateLogger(nameof(ScreenWatcher)), Stats);
            viewModel.WatchesScreen = pipeline.Screen is not null;
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

    private async Task Drain<T>(ChannelReader<T> reader, Action<T> apply)
    {
        await foreach (var item in reader.ReadAllAsync())
            _window!.DispatcherQueue.TryEnqueue(() => apply(item));
    }
}
