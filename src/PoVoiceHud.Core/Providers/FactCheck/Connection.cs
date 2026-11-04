using System.Globalization;
using System.Net;
using PoVoiceHud.Core.Config;

namespace PoVoiceHud.Core.Providers.FactCheck;

/// <summary>
/// Why claims cannot be checked right now: no endpoint or key, a refused key, no deployment, or a service that is down or slow.
/// The pipeline shows it once in the header instead of a "can't tell" on every line.
/// </summary>
public sealed class FactCheckUnavailableException : Exception
{
    public FactCheckUnavailableException() { }

    public FactCheckUnavailableException(string message) : base(message) { }

    public FactCheckUnavailableException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>The endpoint or key is wrong (rather than the service being down): only Settings can fix it.</summary>
    public bool NeedsSetup { get; init; }
}

/// <summary>One round trip to the model, so a wrong endpoint or key shows up before the app relies on it.</summary>
public static class Connection
{
    public const string NotSetUp = "Add the Azure AI Foundry endpoint and key in Settings.";

    /// <summary>
    /// Settings that can check claims: an endpoint and a stored key for Azure, a local server and model name for a local
    /// model, and both for Azure with the local model as its fallback.
    /// </summary>
    public static bool IsReady(Settings settings, bool hasKey)
    {
        var azure = FoundryClient.IsReady(settings.FoundryEndpoint, hasKey);
        var local = FoundryClient.IsValidLocalEndpoint(settings.LocalEndpoint) && !string.IsNullOrWhiteSpace(settings.LocalModel);
        return settings.ModelMode switch
        {
            ModelMode.Local => local,
            ModelMode.AzureThenLocal => azure && local,
            _ => azure,
        };
    }

    /// <summary>Null when the model answered; otherwise what went wrong, in words for the person setting it up.</summary>
    public static async Task<FactCheckUnavailableException?> TestAsync(ILanguageModel model, CancellationToken ct)
    {
        try
        {
            await model.CompleteAsync("Reply with the single word OK.", "ping", ct);
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException)
        {
            return Describe(e, model as FoundryClient);
        }
    }

    /// <summary>A failed model call as a <see cref="FactCheckUnavailableException"/> that says what to do about it.</summary>
    /// <param name="model">The client that failed, for a local model's own wording.</param>
    public static FactCheckUnavailableException Describe(Exception e, FoundryClient? model = null) => e switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } when model is { IsLocal: true } =>
            new($"The local server has no model named {model.Name} (HTTP 404). Download it first (for Ollama: ollama pull {model.Name}).", e) { NeedsSetup = true },
        HttpRequestException { StatusCode: null } when model is { IsLocal: true } =>
            new($"Could not reach the local model ({e.Message}). Is its server running?", e),
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } h =>
            new($"The key was refused (HTTP {(int)h.StatusCode!}).", e) { NeedsSetup = true },
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            new($"No {FoundryClient.Model} deployment at that endpoint (HTTP 404).", e) { NeedsSetup = true },
        HttpRequestException { StatusCode: { } code } => new($"The fact-check service answered HTTP {((int)code).ToString(CultureInfo.InvariantCulture)}.", e),
        HttpRequestException => new($"Could not reach the fact-check service: {e.Message}", e),
        _ => new(e.Message, e),   // a TimeoutException already says how long it waited
    };
}
