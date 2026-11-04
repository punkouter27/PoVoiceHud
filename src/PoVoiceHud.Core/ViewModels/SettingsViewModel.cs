using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoVoiceHud.Core.Asr;
using PoVoiceHud.Core.Config;
using PoVoiceHud.Core.Providers.FactCheck;
using PoVoiceHud.Core.Repos;
using PoVoiceHud.Core.Sessions;

namespace PoVoiceHud.Core.ViewModels;

/// <summary>
/// Edits <see cref="Settings"/> plus three secrets: where claims are checked (Azure's endpoint and key, a local model, or
/// both), the optional code repository and its access token, the Whisper model, the chat webhook, then the switches. Nothing changes until <see cref="ApplyCommand"/>, which first makes one real call to the model
/// whenever the endpoint or key changed (or setup is needed), and reads a new repository, so a wrong one is caught here.
/// <see cref="CancelCommand"/> puts back what was applied. A secret is written only when typed (blank keeps the stored one).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    // Properties that report on the page rather than hold a setting.
    private static readonly string[] NotSettings = [nameof(Message), nameof(RequiresSetup)];

    private readonly Func<string, string?> _readSecret;
    private readonly Action<string, string> _writeSecret;
    private readonly Action<Settings> _save;
    private readonly Func<Uri, string?, ILanguageModel> _connect;
    private readonly Func<Uri, string, ILanguageModel>? _connectLocal;
    private readonly Func<string, string?, Task<string>> _probeRepository;
    private Settings _original;

    [ObservableProperty] private string? _endpoint;
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private bool _hasKey;
    [ObservableProperty] private string? _repository;
    [ObservableProperty] private string _repositoryToken = "";
    [ObservableProperty] private bool _hasRepositoryToken;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LookUpNote))] private bool _lookUpClaims;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _sounds;
    /// <summary>How loud the cues are, 0–100.</summary>
    [ObservableProperty] private double _cueVolume;
    [ObservableProperty] private bool _callsOnly;
    [ObservableProperty] private bool _watchScreen;
    /// <summary>Which of <see cref="ModelModes"/>: Azure, Azure then the local model, or the local model only.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UsesAzure), nameof(UsesLocal))] private int _modelMode;
    [ObservableProperty] private string? _localEndpoint;
    [ObservableProperty] private string? _localModel;
    /// <summary>Which of <see cref="WhisperModels.All"/>.</summary>
    [ObservableProperty] private int _whisperModel;
    [ObservableProperty] private bool _useGpu;
    [ObservableProperty] private bool _separateSpeakers;
    [ObservableProperty] private bool _offerToShare;
    [ObservableProperty] private string _shareWebhook = "";
    [ObservableProperty] private bool _hasShareWebhook;
    /// <summary>
    /// What the last Apply found: "✓ Connected…", "✗ The key was refused…", "Checking the connection…", or "" for nothing.
    /// Never null: the page binds functions to it, and x:Bind does not call a function whose argument is null, so the dot
    /// would keep its old colour beside an empty line.
    /// </summary>
    [ObservableProperty] private string _message = "";
    /// <summary>No working endpoint and key yet: Apply always checks the connection, and there is nothing to cancel back to.</summary>
    [ObservableProperty] private bool _requiresSetup;

    /// <param name="connect">Builds the model client for the connection check from the endpoint and key.</param>
    /// <param name="probeRepository">Reads a repository with a token and says what it found, starting with ✓ or ✗ (<see cref="RepoSource.Probe"/> by default).</param>
    /// <param name="connectLocal">Builds the local model's client for its check from the server and the model's name.</param>
    public SettingsViewModel(Settings settings, Func<string, string?> readSecret, Action<string, string> writeSecret,
        Action<Settings> save, Func<Uri, string?, ILanguageModel> connect, Func<string, string?, Task<string>>? probeRepository = null,
        Func<Uri, string, ILanguageModel>? connectLocal = null)
    {
        (_original, _readSecret, _writeSecret, _save, _connect, _connectLocal) = (settings, readSecret, writeSecret, save, connect, connectLocal);
        _probeRepository = probeRepository ?? ((location, token) => Task.Run(() => RepoSource.Probe(location, token)));
        _endpoint = settings.FoundryEndpoint;
        _repository = settings.Repository;
        _hasRepositoryToken = readSecret(CredentialStore.RepoToken) is not null;
        _lookUpClaims = settings.LookUpClaims;
        _startWithWindows = settings.StartWithWindows;
        _sounds = settings.Sounds;
        _cueVolume = settings.CueVolume;
        _callsOnly = settings.CallsOnly;
        _watchScreen = settings.WatchScreen;
        _hasKey = readSecret(CredentialStore.FoundryKey) is not null;
        _hasShareWebhook = readSecret(CredentialStore.ShareWebhook) is not null;
        Load(settings);
    }

    /// <summary>What the model picker offers, in <see cref="Config.ModelMode"/>'s order.</summary>
    public static IReadOnlyList<string> ModelModes { get; } =
        ["Azure AI Foundry", "Azure, and a local model when Azure is down", "A local model only (nothing leaves this PC)"];

    /// <summary>What the Whisper picker offers, in <see cref="WhisperModels.All"/>'s order.</summary>
    public static IReadOnlyList<string> WhisperModelNames { get; } = [.. WhisperModels.All.Select(m => m.Label)];

    public bool UsesAzure => (ModelMode)ModelMode != Config.ModelMode.Local;
    public bool UsesLocal => (ModelMode)ModelMode != Config.ModelMode.Azure;

    /// <summary>The settings for the model, Whisper, voices and sharing, which Revert puts back too.</summary>
    private void Load(Settings settings)
    {
        ModelMode = (int)settings.ModelMode;
        LocalEndpoint = settings.LocalEndpoint;
        LocalModel = settings.LocalModel;
        WhisperModel = Math.Max(0, WhisperModels.All.ToList().FindIndex(m => m.Id == WhisperModels.Find(settings.WhisperModel).Id));
        UseGpu = settings.UseGpu;
        SeparateSpeakers = settings.SeparateSpeakers;
        OfferToShare = settings.OfferToShare;
    }

    /// <summary>The build's date (YYMMDD) and version, in the page's corner.</summary>
    public string BuildStamp { get; init; } = "";

    /// <summary>Applied or cancelled: the window leaves the settings.</summary>
    public event Action? Done;

    /// <summary>What the Wikipedia switch means for privacy.</summary>
    public string LookUpNote => LookUpClaims
        ? "The key words of each claim go to Wikipedia's and Wikidata's search (and, for a claim about the present, a news search), and the verdict can link what it used."
        : "Claims are judged from the model's own knowledge; nothing goes to Wikipedia, Wikidata or the news search.";

    public Settings ToSettings() => _original with
    {
        FoundryEndpoint = Blank(Endpoint),
        Repository = Blank(Repository),
        LookUpClaims = LookUpClaims,
        StartWithWindows = StartWithWindows,
        Sounds = Sounds,
        CueVolume = (int)Math.Round(Math.Clamp(CueVolume, 0, 100)),
        CallsOnly = CallsOnly,
        WatchScreen = WatchScreen,
        ModelMode = (ModelMode)Math.Clamp(ModelMode, 0, ModelModes.Count - 1),
        LocalEndpoint = Blank(LocalEndpoint),
        LocalModel = Blank(LocalModel),
        WhisperModel = WhisperModels.All[Math.Clamp(WhisperModel, 0, WhisperModels.All.Count - 1)].Id,
        UseGpu = UseGpu,
        SeparateSpeakers = SeparateSpeakers,
        OfferToShare = OfferToShare,
    };

    /// <summary>The endpoint or key cannot be used (or was never set): the app stays in Settings until Apply succeeds.</summary>
    public void RequireSetup(string? why)
    {
        RequiresSetup = true;
        Message = why is null ? "" : $"✗ {why}";
    }

    /// <summary>Puts back what was last applied: leaving the settings without Apply discards the edits.</summary>
    public void Revert()
    {
        Endpoint = _original.FoundryEndpoint;
        Key = "";
        Repository = _original.Repository;
        RepositoryToken = "";
        LookUpClaims = _original.LookUpClaims;
        StartWithWindows = _original.StartWithWindows;
        Sounds = _original.Sounds;
        CueVolume = _original.CueVolume;
        CallsOnly = _original.CallsOnly;
        WatchScreen = _original.WatchScreen;
        ShareWebhook = "";
        Load(_original);
        if (!RequiresSetup) Message = "";
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!NotSettings.Contains(e.PropertyName)) ApplyCommand.NotifyCanExecuteChanged();
        if (e.PropertyName == nameof(RequiresSetup))
        {
            ApplyCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanApply() => RequiresSetup || ToSettings() != _original || Blank(Key) is not null || Blank(RepositoryToken) is not null
        || Blank(ShareWebhook) is not null;

    /// <summary>
    /// Checks a new endpoint or key with one real call and reads a new repository, then stores the secrets and saves and
    /// applies the settings. Anything that fails stops it, with the reason in <see cref="Message"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        var settings = ToSettings();
        var endpoint = Blank(Endpoint);
        var typed = Blank(Key);
        var key = typed ?? _readSecret(CredentialStore.FoundryKey);
        if (UsesAzure && !FoundryClient.IsValidEndpoint(endpoint))
        {
            Message = "✗ The endpoint must be an https:// URL, such as https://<resource>.services.ai.azure.com";
            return;
        }
        if (UsesAzure && key is null)
        {
            Message = "✗ Enter the key.";
            return;
        }
        if (UsesLocal && !FoundryClient.IsValidLocalEndpoint(settings.LocalEndpoint))
        {
            Message = "✗ The local model's address must be an http:// URL, such as http://localhost:11434/v1 for Ollama.";
            return;
        }
        if (UsesLocal && settings.LocalModel is null)
        {
            Message = "✗ Enter the local model's name, such as qwen2.5:7b.";
            return;
        }
        var webhook = Blank(ShareWebhook);
        if (webhook is not null && !SummaryShare.IsValidWebhook(webhook))
        {
            Message = "✗ The webhook must be an https:// URL: Slack, a Teams workflow, Discord, or another service's incoming webhook.";
            return;
        }

        var found = new List<string>();
        var modeChanged = settings.ModelMode != _original.ModelMode;
        var connectionChanged = UsesAzure && (RequiresSetup || modeChanged || typed is not null || endpoint != _original.FoundryEndpoint);
        if (connectionChanged)
        {
            Message = "Checking the connection…";
            if (await Connection.TestAsync(_connect(new Uri(endpoint!), key), CancellationToken.None) is { } problem)
            {
                Message = $"✗ {problem.Message}";
                return;
            }
            found.Add($"✓ Connected — {FoundryClient.Model} answered.");
        }
        var localChanged = UsesLocal && (RequiresSetup || modeChanged || settings.LocalEndpoint != _original.LocalEndpoint || settings.LocalModel != _original.LocalModel);
        if (localChanged && _connectLocal is { } connectLocal)
        {
            Message = "Asking the local model (the first call loads it, which can take a while)…";
            if (await Connection.TestAsync(connectLocal(new Uri(settings.LocalEndpoint!), settings.LocalModel!), CancellationToken.None) is { } problem)
            {
                Message = $"✗ {problem.Message}";
                return;
            }
            found.Add($"✓ The local model {settings.LocalModel} answered.");
        }

        var repository = Blank(Repository);
        var typedToken = Blank(RepositoryToken);
        if (repository is not null && (repository != _original.Repository || typedToken is not null))
        {
            // Each repository is read; the first that cannot be stops Apply, saying which.
            var locations = RepoSource.Locations(repository);
            foreach (var location in locations)
            {
                Message = locations.Count == 1 ? "Reading the repository…" : $"Reading {RepoSource.NameOf(location)}…";
                var probe = await _probeRepository(location, typedToken ?? _readSecret(CredentialStore.RepoToken));
                if (!probe.StartsWith('✓'))
                {
                    Message = locations.Count == 1 ? probe : $"{RepoSource.NameOf(location)}: {probe}";
                    return;
                }
                found.Add(locations.Count == 1 ? probe : $"✓ {RepoSource.NameOf(location)}: {probe.TrimStart('✓', ' ')}");
            }
        }

        // The key first: applying the settings builds a fact checker, which reads it.
        if (typed is not null)
        {
            _writeSecret(CredentialStore.FoundryKey, typed);
            HasKey = true;
            Key = "";
        }
        if (typedToken is not null)
        {
            _writeSecret(CredentialStore.RepoToken, typedToken);
            HasRepositoryToken = true;
            RepositoryToken = "";
        }
        if (webhook is not null)
        {
            _writeSecret(CredentialStore.ShareWebhook, webhook);
            HasShareWebhook = true;
            ShareWebhook = "";
            found.Add($"✓ Summaries can be posted to {SummaryShare.NameOf(new Uri(webhook))}.");
        }
        if (settings.UseGpu != _original.UseGpu) found.Add("✓ Saved. The graphics card switch takes effect the next time PoVoiceHud starts.");
        _original = settings;
        RequiresSetup = false;
        Message = string.Join("\n", found);
        ApplyCommand.NotifyCanExecuteChanged();
        _save(settings);
        Done?.Invoke();
    }

    private bool CanCancel() => !RequiresSetup;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        Revert();
        Done?.Invoke();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
