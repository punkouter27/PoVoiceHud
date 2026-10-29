using System.Text.Json;

namespace PoVoiceHud.Core.Config;

public static class SettingsStore
{
    private static readonly CoreJson Json = new(new JsonSerializerOptions { WriteIndented = true });

    public static string DefaultPath { get; } = Path.Combine(AppPaths.DataDir, "settings.json");

    public static (Settings Settings, string? Warning) Load(string path)
    {
        if (!File.Exists(path)) return (new Settings(), null);
        try
        {
            return (JsonSerializer.Deserialize(File.ReadAllText(path), Json.Settings) ?? new Settings(), null);
        }
        catch (JsonException e)
        {
            return (new Settings(), $"{Path.GetFileName(path)} is unreadable, using defaults: {e.Message}");
        }
    }

    public static void Save(Settings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Json.Settings));
    }

    /// <summary>A file of string pairs (the window's placement), or null when it is missing or half-written.</summary>
    public static Dictionary<string, string>? LoadPairs(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.DictionaryStringString) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    public static void SavePairs(Dictionary<string, string> pairs, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(pairs, CoreJson.Default.DictionaryStringString));
}
