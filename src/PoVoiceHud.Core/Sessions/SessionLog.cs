using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoVoiceHud.Core.Config;

namespace PoVoiceHud.Core.Sessions;

/// <summary>One line of sessions\{date}.jsonl: a heard sentence sets Heard; a verdict sets Claim, Answer and Verdict; a new screen sets Screen.</summary>
public sealed record SessionEntry
{
    public DateTimeOffset At { get; init; }
    /// <summary>Who said it: "Mic" or "Teams" (<see cref="Audio.SourceHint"/>).</summary>
    public string? Source { get; init; }
    public string? Heard { get; init; }
    /// <summary>What the screen showed from this time on, in the model's words.</summary>
    public string? Screen { get; init; }
    public string? Claim { get; init; }
    public string? Answer { get; init; }
    public string? Verdict { get; init; }
    public Uri? Link { get; init; }
}

/// <summary>
/// What was heard and checked, appended one JSON object per line to a file per day, on this PC only: a history to look
/// back at (tray → Open the history folder). Writing is best-effort: a locked or full disk must not interrupt a call.
/// </summary>
public sealed class SessionLog(string directory)
{
    public static readonly string DefaultDirectory = Path.Combine(AppPaths.DataDir, "sessions");

    private static readonly CoreJson Json = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // the file is read by people: keep ✅ and quotes as written
    });

    private readonly Lock _gate = new();

    public string Directory => directory;

    public void Append(SessionEntry entry)
    {
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"{entry.At:yyyy-MM-dd}.jsonl"), JsonSerializer.Serialize(entry, Json.SessionEntry) + "\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the log is a convenience
        }
    }

    /// <summary>The days that have a history, newest first.</summary>
    public IReadOnlyList<DateOnly> Days()
    {
        if (!System.IO.Directory.Exists(directory)) return [];
        return System.IO.Directory.EnumerateFiles(directory, "*.jsonl")
            .Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", out var day) ? day : (DateOnly?)null)
            .OfType<DateOnly>()
            .OrderDescending()
            .ToList();
    }

    /// <summary>The day's meeting summary as Markdown, saved beside its history: {date}-summary.md. Null when there is none.</summary>
    public string? ReadSummary(DateOnly day)
    {
        try
        {
            return File.Exists(SummaryPath(day)) ? File.ReadAllText(SummaryPath(day)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void SaveSummary(DateOnly day, string markdown)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(SummaryPath(day), markdown);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the summary is still on screen
        }
    }

    private string SummaryPath(DateOnly day) => Path.Combine(directory, $"{day:yyyy-MM-dd}-summary.md");

    /// <summary>What was heard and checked on <paramref name="day"/>, in order. A line that cannot be read is skipped.</summary>
    public IReadOnlyList<SessionEntry> Read(DateOnly day)
    {
        var path = Path.Combine(directory, $"{day:yyyy-MM-dd}.jsonl");
        string[] lines;
        try
        {
            lock (_gate) lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var entries = new List<SessionEntry>(lines.Length);
        foreach (var line in lines)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(line) && JsonSerializer.Deserialize(line, Json.SessionEntry) is { } entry) entries.Add(entry);
            }
            catch (JsonException)
            {
                // a line cut short by a crash
            }
        }
        return entries;
    }
}
