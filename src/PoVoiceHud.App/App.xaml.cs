using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using PoVoiceHud.App.Interop;
using PoVoiceHud.Core;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Config;
using PoVoiceHud.Core.Diagnostics;
using PoVoiceHud.Core.Hud;
using PoVoiceHud.Core.Meetings;
using PoVoiceHud.Core.Models;
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
    private DateTimeOffset? _meetingStart;   // when the first of those lines was heard: a shared summary covers this meeting only

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
            ExportAsync, ShareDayAsync);
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
        Stats.Fallback.Switched += OnFallbackSwitched;
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
        if (Demo)
        {
            ShowDemo(viewModel);
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
        if (!Connection.IsReady(settings, CredentialStore.Read(CredentialStore.FoundryKey) is not null)) RequireSetup(null);
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
            connect: (endpoint, key) => new FoundryClient(Http, endpoint, key),
            connectLocal: (endpoint, model) => new FoundryClient(Http, endpoint, null, timeout: TimeSpan.FromSeconds(90), localModel: model)) { BuildStamp = BuildStamp };
        page.Done += () => _window!.ViewModel.IsSettingsOpen = false;
        _settingsPage = page;
        _window!.AttachSettings(page);
    }

    /// <summary>A fact's source link goes through the shell.</summary>
    private async Task OpenAsync(Uri uri)
    {
        if (!await Windows.System.Launcher.LaunchUriAsync(uri))
            _window?.ViewModel.SetStatus($"Nothing is registered to open {uri.Scheme}:// links");
    }

    /// <summary>Azure went down or came back while "Azure, and a local model when Azure is down" is chosen: said once in the header.</summary>
    private void OnFallbackSwitched(bool local) => _window?.DispatcherQueue.TryEnqueue(() => _window.ViewModel.SetStatus(local
        ? $"Azure is not answering: checking with {_settings.LocalModel} on this PC"
        : "Azure is answering again"));

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Stats.Fallback.Switched -= OnFallbackSwitched;
        if (!Demo) SettingsStore.SavePairs(_placement.ToDictionary(p => p.Key, p => p.Value.ToString() ?? ""), WindowFile);
        _log.LogInformation("Closing");
        _meter?.Stop();
        _calls?.Stop();
        _tray?.Dispose();
        _container?.Dispose();
    }
}
