using System.Text.Json.Serialization;
using PoVoiceHud.Core.Sessions;

namespace PoVoiceHud.Core.Config;

/// <summary>
/// The types the app reads and writes as JSON, with their (de)serializers generated at build time rather than found by
/// reflection: trimming and Native AOT keep them working. Each caller wraps it in the options it needs.
/// </summary>
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(SessionEntry))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class CoreJson : JsonSerializerContext;
