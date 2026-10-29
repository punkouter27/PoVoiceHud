using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PoVoiceHud.Core.Providers;

/// <summary>
/// The user message as JSON, which keeps a transcript unmistakably data. Relaxed escaping sends code and quotes as written
/// instead of the <-style escapes the model has to read past. Built from JSON nodes, not by reflection, so it trims.
/// </summary>
public static class PromptJson
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(JsonNode value) => value.ToJsonString(Options);

    /// <summary>A list of strings as a JSON array.</summary>
    public static JsonArray List(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode?)i)]);
}
