using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
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
#pragma warning disable CS0649 // set only by the DEBUG-only --demo switch, so a Release build never assigns it
    private bool _demo;   // a layout check: no audio, and the real window placement is left alone
#pragma warning restore CS0649
    private string? _repoToken;   // the repository token the running pipeline fetched with; a new one means fetching again

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
        Dictionary<string, string>? saved = null;
        try
        {
            if (File.Exists(WindowFile)) saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(WindowFile));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // half-written placement file: start with default placement rather than crash on every launch
        }
        _placement = saved?.ToDictionary(p => p.Key, p => (object)p.Value) ?? [];
        WindowManager.PersistenceStorage = _placement;
    }

    private static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var (settings, warning) = SettingsStore.Load(SettingsStore.DefaultPath);
        _settings = settings;
        var viewModel = new HudViewModel(OpenAsync);
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
            if (_pipeline is { } pipeline) pipeline.IsMuted = viewModel.IsMuted;
            if (_tray is { } tray) tray.Tooltip = viewModel.IsMuted ? "PoVoiceHud — muted" : "PoVoiceHud — listening";
            if (_settings.Sounds) SoundPalette.Play(viewModel.IsMuted ? Cue.Muted : Cue.Unmuted);
        };
        viewModel.Arrived += verdict =>
        {
            if (_settings.Sounds)
                SoundPalette.Play(verdict.Verdict switch { Verdict.Supported => Cue.Supported, Verdict.Contradicted => Cue.Contradicted, _ => Cue.Unverified });
        };
        _meter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _meter.Tick += (_, _) =>
        {
            if (_pipeline is not { } pipeline) return;
            viewModel.MicLevel = pipeline.Level;
            viewModel.LevelSource = pipeline.Speaker;
        };
        _meter.Start();

#if DEBUG
        // Layout checks without a microphone: --demo seeds a sample conversation (--facts opens the Facts page, --code Code facts,
        // --settings the Settings page, --setup the first-run page).
        var flags = Environment.GetCommandLineArgs();
        if (flags.Contains("--demo"))
        {
            _demo = true;
            _settings = _settings with { Sounds = false };   // the demo is for looking at, not listening to
            _window.SeedDemo();
            if (flags.Contains("--facts")) viewModel.Page = HudPage.Facts;
            if (flags.Contains("--code")) viewModel.Page = HudPage.Code;
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

    /// <summary>Shows only the Settings page, saying why, until Apply finds a working endpoint and key. The microphone stays off.</summary>
    private void RequireSetup(string? why)
    {
        _window!.ViewModel.NeedsSetup = true;
        _window.ViewModel.SetStatus("Connect the fact checker to start");
        _settingsPage!.RequireSetup(why);
    }

    /// <summary>"Sounds and animations": the cues check the setting as they play; the highlight's fade also follows Windows' animation switch.</summary>
    private void ApplyFeedback(Settings settings) => _window!.Animations = settings.Sounds && new UISettings().AnimationsEnabled;

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

            // The code repository, if there is one: a URL is cloned (the first time, which can take a while) or refreshed; a folder is read where it is.
            string? repoRoot = null, repoWarning = null;
            var repoToken = _repoToken = CredentialStore.Read(CredentialStore.RepoToken);
            if (settings.Repository is { } location)
            {
                Status("Fetching the code repository…");
                (repoRoot, repoWarning) = await Task.Run(() => RepoSource.Open(location, repoToken));
                if (repoWarning is not null) _log.LogWarning("Repository: {Warning}", repoWarning);
            }

            Status("Loading models…");
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddDebug().AddProvider(_fileLog));
            services.AddSingleton(Http);
            services.AddPoVoiceHudCore(settings, models, repoRoot);
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
            pipeline.IsMuted = viewModel.IsMuted;   // the user may have muted during the download

            _ = Drain(pipeline.Status, viewModel.SetStatus);
            _ = Drain(pipeline.Transcript, line =>
            {
                viewModel.AddTranscript(line);
                Sessions.Append(new SessionEntry { At = DateTimeOffset.Now, Source = line.Source.ToString(), Heard = line.Text });
            });
            _ = Drain(pipeline.Checking, c => viewModel.MarkChecking(c.Line, c.Busy));
            viewModel.HasRepository = container.GetService<CodeRepository>() is not null;
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
            _log.LogInformation("Listening: {Model} at {Host}, Wikipedia {LookUp}, repository {Files}", FoundryClient.Model,
                Uri.TryCreate(settings.FoundryEndpoint, UriKind.Absolute, out var host) ? host.Host : "none", settings.LookUpClaims ? "on" : "off",
                container.GetService<CodeRepository>() is { } code ? $"{code.Index.Symbols.Count} names" : "none");
            // After the audio's own "Listening", so it is not overwritten straight away.
            if (repoWarning is not null) _ = Task.Delay(1500).ContinueWith(_ => Status(repoWarning), TaskScheduler.Default);
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
            } == old)
        {
            pipeline.Checker = ServiceCollectionExtensions.CreateChecker(settings, Http);
            pipeline.Code = ServiceCollectionExtensions.CreateCodeAnswerer(settings, Http, _container?.GetService<CodeRepository>());
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
            connect: (endpoint, key) => new FoundryClient(Http, endpoint, key));
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
        if (!_demo) File.WriteAllText(WindowFile, JsonSerializer.Serialize(_placement.ToDictionary(p => p.Key, p => p.Value.ToString())));
        _log.LogInformation("Closing");
        _meter?.Stop();
        _tray?.Dispose();
        _container?.Dispose();
    }
}
