using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Providers;
using PoVoiceHud.Core.Repos;

namespace PoVoiceHud.Core.ViewModels;

/// <summary>One file a code fact rests on: its name and folder, its kind of file, and two ways to open it.</summary>
public sealed class CodeSourceViewModel(CodeSource source, Func<Uri, Task> openUri)
{
    public string Path => source.Path;
    /// <summary>"InvoiceService.cs ↗" and, dimmer, where it sits: "src/Services".</summary>
    public string Label => $"{System.IO.Path.GetFileName(source.Path)} ↗";
    public string Folder => System.IO.Path.GetDirectoryName(source.Path)?.Replace('\\', '/') ?? "";
    /// <summary>A Segoe Fluent icon for the kind of file: code, markup, a document, data or a picture.</summary>
    public string Glyph => GlyphFor(source.Path);
    public IAsyncRelayCommand OpenCommand { get; } = new AsyncRelayCommand(() => openUri(source.Link));
    public IAsyncRelayCommand OpenInEditorCommand { get; } = new AsyncRelayCommand(() => openUri(source.Editor));

    public static string GlyphFor(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".fs" or ".vb" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".java" or ".kt" or ".go" or ".rs" or ".rb" or ".php"
            or ".swift" or ".cpp" or ".cc" or ".c" or ".h" or ".hpp" or ".dart" or ".scala" or ".ps1" or ".sh" => "",   // code
        ".xaml" or ".razor" or ".cshtml" or ".html" or ".htm" or ".vue" or ".svelte" or ".css" or ".scss" => "",        // a page
        ".md" or ".txt" or ".rst" or ".adoc" => "",                                                                       // a document
        ".json" or ".xml" or ".yml" or ".yaml" or ".config" or ".props" or ".targets" or ".csproj" or ".sln" or ".toml" or ".ini"
            or ".editorconfig" => "",                                                                                     // settings
        ".sql" => "",                                                                                                     // a database
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" or ".webp" => "",                                         // a picture
        _ => "",                                                                                                          // a file
    };
}

/// <summary>
/// One row of the Code facts page: a question about the code and its answer, or a statement about it and its verdict,
/// with the files it rests on.
/// </summary>
public sealed partial class CodeFactViewModel(CodeFact fact, DateTimeOffset at, SourceHint source, Func<Uri, Task> openUri, SpeakerNames? names = null)
    : ObservableObject
{
    public CodeFact Fact => fact;
    public bool IsAnswer => fact.IsAnswer;
    public Verdict? Verdict => fact.Verdict;
    /// <summary>"Answer", or the verdict in plain words.</summary>
    public string Label => fact.Verdict is { } verdict ? verdict.Label() : "Answer";
    public string Topic => fact.Topic;
    public string Text => fact.Text;
    public string Time { get; } = at.ToString("t", CultureInfo.CurrentCulture);
    public SourceHint Source => source;
    public string Speaker => names?.Of(source) ?? source.Speaker();
    /// <summary>The repository it was looked up in, shown when more than one is connected.</summary>
    public string Repository => fact.Repository;
    public double Confidence => fact.Confidence;
    public string ConfidenceText => $"Model confidence: {Confidence:P0}";
    public IReadOnlyList<CodeSourceViewModel> Sources { get; } = [.. fact.Sources.Select(s => new CodeSourceViewModel(s, openUri))];
    public bool HasSources => fact.Sources.Count > 0;

    /// <summary>The sides were renamed.</summary>
    internal void Renamed() => OnPropertyChanged(nameof(Speaker));

    /// <summary>One line for "Copy": "💬 Answer: question — answer (files)" or "❌ Incorrect: statement — reason (files)".</summary>
    public string CopyLine =>
        $"{(fact.Verdict is { } verdict ? verdict.Emoji() : "💬")} {Label}: {Topic} — {Text}{(fact.Sources.Count > 0 ? $" ({string.Join(", ", fact.Sources.Select(s => s.Path))})" : "")}";
}
