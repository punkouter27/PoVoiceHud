using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Polly;

namespace PoVoiceHud.Core.Providers.FactCheck;

/// <summary>The resource's content filter refused this one prompt; the next sentence may well go through.</summary>
public sealed class ContentFilteredException : Exception
{
    public ContentFilteredException() { }

    public ContentFilteredException(string message) : base(message) { }

    public ContentFilteredException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>
/// Chat completions on an Azure AI Foundry resource: the resource root (https://&lt;resource&gt;.services.ai.azure.com) gets
/// /models/chat/completions?api-version=… appended and the key goes in the api-key header. The model is fixed, and the
/// reply is held to the verdict schema. Each attempt has its own time limit and transient failures are retried.
/// </summary>
/// <param name="responseFormat">
/// The JSON schema the reply is held to, as JSON: the fact checker's verdict unless another is given
/// (<see cref="Repos.CodeAnswerer.ReplyFormat"/>).
/// </param>
/// <param name="stats">Where each call's time, tokens, retries and failures are counted, if anywhere.</param>
/// <param name="maxTokens">The longest reply: a verdict needs 300; a meeting's summary more.</param>
public sealed class FoundryClient(HttpClient http, Uri endpoint, string? apiKey, ResiliencePipeline? retry = null, TimeSpan? timeout = null,
    string? responseFormat = null, ModelStats? stats = null, int maxTokens = 300) : ILanguageModel, IVisionModel
{
    /// <summary>
    /// ~1–1.5 s per verdict on a GlobalStandard deployment, and it honours the JSON schema natively. The resource must have
    /// a deployment of this name.
    /// </summary>
    public const string Model = "gpt-4.1-mini";

    private const string ApiVersion = "2024-05-01-preview";

    /// <summary>
    /// The verdict shape <see cref="FactChecker"/> reads; the server enforces it. The model writes the properties in this
    /// order, so it restates the claim, gives its reason and source, and only then decides.
    /// </summary>
    public const string VerdictFormat = """
        {"type": "json_schema", "json_schema": {"name": "verdict", "strict": true, "schema": {
          "type": "object",
          "properties": {
            "claim": {"type": "string"},
            "reason": {"type": "string"},
            "source": {"type": "string"},
            "verdict": {"type": "string", "enum": ["supported", "contradicted", "unverified", "not_a_claim"]},
            "confidence": {"type": "number"}
          },
          "required": ["claim", "reason", "source", "verdict", "confidence"],
          "additionalProperties": false}}}
        """;

    private readonly ResiliencePipeline _retry = retry ?? Resilience.Default;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(8);
    private readonly string _format = responseFormat ?? VerdictFormat;

    /// <summary>The resource root: an https URL.</summary>
    public static bool IsValidEndpoint(string? endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https";

    /// <summary>Usable as configured: a valid endpoint and a stored key.</summary>
    public static bool IsReady(string? endpoint, bool hasKey) => IsValidEndpoint(endpoint) && hasKey;

    public static Uri Resolve(Uri endpoint) =>
        endpoint.AbsolutePath is "" or "/" ? new Uri(endpoint, $"/models/chat/completions?api-version={ApiVersion}") : endpoint;

    /// <summary>Throws HttpRequestException on transport/HTTP failure and TimeoutException when an attempt gets no reply in time.</summary>
    public Task<string> CompleteAsync(string system, string user, CancellationToken ct) => SendAsync(system, user, ct);

    /// <summary>The same, with a picture after the text (a data URL: nothing is uploaded anywhere else).</summary>
    public Task<string> CompleteAsync(string system, string user, ReadOnlyMemory<byte> jpeg, CancellationToken ct) =>
        SendAsync(system, new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = user },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg.Span) } }), ct);

    private async Task<string> SendAsync(string system, JsonNode user, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["temperature"] = 0,
            ["max_tokens"] = maxTokens,
            ["response_format"] = JsonNode.Parse(_format),
        }.ToJsonString();
        var clock = Stopwatch.StartNew();
        var attempts = 0;
        try
        {
            var (text, tokens) = await _retry.ExecuteAsync(async token =>
            {
                if (attempts++ > 0) stats?.Retried();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(_timeout);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, Resolve(endpoint)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    if (!string.IsNullOrEmpty(apiKey)) request.Headers.Add("api-key", apiKey);
                    using var response = await http.SendAsync(request, limit.Token);
                    return await ReadAsync(response, limit.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException($"No reply from {endpoint.Host} within {_timeout.TotalSeconds:0} s.");
                }
            }, ct);
            stats?.Record(clock.Elapsed, tokens);
            return text;
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException)
        {
            stats?.Failed();
            throw;
        }
    }

    /// <summary>The assistant text ("" when the body is not the expected shape: the caller treats that as unreadable), and the tokens used.</summary>
    private static async Task<(string Text, int Tokens)> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Azure's content filter refuses some sentences (one that sounds like a jailbreak, say) with a 400: about that sentence, not the service.
        if (response.StatusCode == HttpStatusCode.BadRequest && (await response.Content.ReadAsStringAsync(ct)).Contains("\"content_filter\"", StringComparison.Ordinal))
            throw new ContentFilteredException("Azure's content filter refused the sentence.");
        response.EnsureSuccessStatusCode();
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var body = doc.RootElement;
            var tokens = body.TryGetProperty("usage", out var usage) && usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var n) ? n : 0;
            var text = body.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                       && choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
                       && content.ValueKind == JsonValueKind.String
                ? content.GetString()!
                : "";
            return (text, tokens);
        }
        catch (JsonException)
        {
            return ("", 0);
        }
    }
}
