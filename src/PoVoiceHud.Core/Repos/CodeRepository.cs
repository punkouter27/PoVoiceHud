using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LibGit2Sharp;

namespace PoVoiceHud.Core.Repos;

/// <summary>Part of a file of the repository: its path, the line the excerpt is about, and up to 6 000 characters around it.</summary>
public sealed record CodeExcerpt(string Path, int Line, string Text);

/// <summary>
/// The connected code repository's local copy. Keeps a <see cref="SymbolIndex"/> (and the text of its files, for search)
/// fresh with a debounced file watcher, and finds what a sentence is about: the files it names, and the files whose text
/// best matches its words.
/// </summary>
public sealed partial class CodeRepository : IDisposable
{
    private const int MaxCharsPerExcerpt = 6_000;
    private const int LinesBefore = 20;   // an excerpt starts a little above the line it is about
    private const int MaxFileBytes = 256 * 1024;   // larger files are generated or data, not code worth reading
    private const long MaxTextBytes = 40L * 1024 * 1024;   // what the search keeps in memory, at most

    private static readonly HashSet<string> Binary = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp", ".svgz", ".pdf", ".zip", ".gz", ".7z", ".dll", ".exe", ".pdb",
        ".bin", ".onnx", ".wav", ".mp3", ".mp4", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".snk", ".pfx", ".lock",
    };

    // Words that say nothing about which file is meant.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "these", "those", "have", "has", "had", "does", "did", "what", "where", "when",
        "which", "who", "why", "how", "many", "much", "there", "their", "they", "them", "our", "your", "you", "are", "was", "were",
        "will", "would", "should", "could", "can", "into", "from", "about", "code", "codebase", "repo", "repository", "app",
        "application", "website", "site", "use", "uses", "used", "using", "just", "like", "really", "think", "know", "right", "okay",
    };

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly Func<string, Uri> _link;
    private volatile Dictionary<string, string> _text;   // replaced whole on a change, never changed in place
    // Which files hold each word searched for, worked out once per word until the files change: a meeting asks about the
    // same few things again and again, and each word otherwise means reading every file.
    private readonly ConcurrentDictionary<string, string[]> _holders = new(StringComparer.Ordinal);
    // Files saved since the last refresh: only they are read again. A new, deleted or renamed file, a commit (.git/index) or
    // a migration snapshot (its entities are in the index) rebuilds everything.
    private readonly ConcurrentDictionary<string, byte> _saved = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _rebuild;

    /// <param name="root">A folder that holds a git repository (a clone, or the user's own working copy).</param>
    /// <param name="name">What the repository is called, as people would say it: the last part of its URL or folder.</param>
    /// <param name="link">Where a repo-relative path opens: <see cref="RepoSource.Link"/>.</param>
    public CodeRepository(string root, string name, Func<string, Uri> link)
    {
        if (!Repository.IsValid(root)) throw new ArgumentException($"'{root}' is not a git repository.", nameof(root));
        Root = Path.GetFullPath(root);
        Name = name;
        Index = SymbolIndex.Build(Root);
        _text = ReadText(Root, Index);
        _link = link;

        _debounce = new Timer(_ => Refresh());
        _watcher = new FileSystemWatcher(Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnFileSystemEvent;
        _watcher.EnableRaisingEvents = true;
    }

    public string Root { get; }
    public string Name { get; }
    public SymbolIndex Index { get; private set; }

    /// <summary>Every tracked file, repo-relative.</summary>
    public IEnumerable<string> Paths => Index.Symbols.Where(s => s.Kind == SymbolKind.File).Select(s => s.Path);

    public event Action? Changed;

    /// <summary>Where a file opens: its page on GitHub or Azure DevOps, or the file itself.</summary>
    public Uri Link(string path) => _link(path);

    /// <summary>vscode://file/C:/src/app/InvoiceService.cs:12 — the file in VS Code at that line.</summary>
    public Uri Editor(string path, int line) => new($"vscode://file/{Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)).Replace('\\', '/')}:{line}");

    /// <summary>The files a sentence names ("the invoice service"), each at the line that declares the type (or the entity).</summary>
    public IReadOnlyList<CodeExcerpt> Named(string sentence, int max)
    {
        var excerpts = new List<CodeExcerpt>();
        foreach (var symbol in SymbolMatcher.Mentions(SymbolMatcher.Match(sentence, Index)).Select(m => m.Symbol)
                     .DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (excerpts.Count == max) break;
            if (!_text.TryGetValue(symbol.Path, out var text)) continue;
            var line = symbol.Kind == SymbolKind.Entity ? symbol.Line : Declaration(text, symbol.Name);
            excerpts.Add(Excerpt(symbol.Path, text, line));
        }
        return excerpts;
    }

    /// <summary>
    /// The files whose text best matches the sentence's words (each word counted once per file, rarer words worth more),
    /// each at the line where the most of them meet.
    /// </summary>
    public IReadOnlyList<CodeExcerpt> Search(string sentence, int max, IEnumerable<string>? skip = null)
    {
        var words = SearchWords(sentence);
        var text = _text;
        var skipped = new HashSet<string>(skip ?? [], StringComparer.OrdinalIgnoreCase);
        return Rank(words, text)
            .Where(f => !skipped.Contains(f.Path))
            .Take(max)
            .Select(f => Excerpt(f.Path, text[f.Path], BestLine(text[f.Path], words)))
            .ToList();
    }

    /// <summary>How well the best file matches the sentence's words: 0 when none has any of them.</summary>
    public double Score(string sentence) => Rank(SearchWords(sentence), _text).Select(f => f.Score).FirstOrDefault();

    private static List<string> SearchWords(string sentence) =>
        Words().Matches(sentence).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length >= 3 && !StopWords.Contains(w)).Distinct().ToList();

    /// <summary>
    /// The files with any of <paramref name="words"/> in their text or path, best first. A word found in every file tells
    /// nothing, so each counts by how few files have it.
    /// </summary>
    private IEnumerable<(string Path, double Score)> Rank(List<string> words, Dictionary<string, string> text)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
        {
            var holders = _holders.GetOrAdd(word, w => [.. text.Where(f => f.Value.Contains(w, StringComparison.OrdinalIgnoreCase)
                || f.Key.Contains(w, StringComparison.OrdinalIgnoreCase)).Select(f => f.Key)]);
            foreach (var path in holders)
                scores[path] = scores.GetValueOrDefault(path) + 1.0 / holders.Length;
        }
        return scores.Where(s => text.ContainsKey(s.Key)).OrderByDescending(s => s.Value).Select(s => (s.Key, s.Value));
    }

    /// <summary>The line declaring <paramref name="name"/> ("class InvoiceService"), else the first that mentions it, else 1.</summary>
    private static int Declaration(string text, string name)
    {
        var lines = text.Split('\n');
        string[] declarations = [$"class {name}", $"record {name}", $"interface {name}", $"struct {name}", $"enum {name}"];
        var at = Array.FindIndex(lines, l => declarations.Any(d => l.Contains(d, StringComparison.Ordinal)));
        if (at < 0) at = Array.FindIndex(lines, l => l.Contains(name, StringComparison.Ordinal));
        return at < 0 ? 1 : at + 1;
    }

    /// <summary>The line with the most of <paramref name="words"/> on it, 1-based.</summary>
    private static int BestLine(string text, IReadOnlyList<string> words)
    {
        var lines = text.Split('\n');
        var best = (Line: 0, Hits: 0);
        for (var i = 0; i < lines.Length; i++)
        {
            var hits = words.Count(w => lines[i].Contains(w, StringComparison.OrdinalIgnoreCase));
            if (hits > best.Hits) best = (i, hits);
        }
        return best.Line + 1;
    }

    private static CodeExcerpt Excerpt(string path, string text, int line)
    {
        // Start a few lines above the line it is about, so the excerpt shows what it sits in.
        var start = 0;
        for (var i = 1; i < line - LinesBefore; i++)
        {
            var next = text.IndexOf('\n', start);
            if (next < 0) break;
            start = next + 1;
        }
        return new CodeExcerpt(path, line, text.Substring(start, Math.Min(MaxCharsPerExcerpt, text.Length - start)));
    }

    /// <summary>The text of every tracked file worth searching: not binary, not huge, and no more than 40 MB in all.</summary>
    private static Dictionary<string, string> ReadText(string root, SymbolIndex index)
    {
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var symbol in index.Symbols.Where(s => s.Kind == SymbolKind.File))
        {
            var file = new FileInfo(symbol.AbsolutePath(root));
            if (Binary.Contains(file.Extension) || !file.Exists || file.Length > MaxFileBytes) continue;
            if ((total += file.Length) <= MaxTextBytes && Read(file) is { } content) text[symbol.Path] = content;
        }
        return text;
    }

    /// <summary>A file's text, or null when it is binary, too big, locked or gone (the next refresh catches up).</summary>
    private static string? Read(FileInfo file)
    {
        if (Binary.Contains(file.Extension) || !file.Exists || file.Length > MaxFileBytes) return null;
        try
        {
            var content = File.ReadAllText(file.FullName);
            return content.Contains('\0', StringComparison.Ordinal) ? null : content;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (!IsRelevant(e.FullPath)) return;
        var path = Path.GetRelativePath(Root, e.FullPath).Replace('\\', '/');
        if (e.ChangeType == WatcherChangeTypes.Changed && !path.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            _saved[path] = 0;
        else
            _rebuild = true;
        _debounce.Change(500, Timeout.Infinite);
    }

    /// <summary>After a pause in changes: a full rebuild when the set of files may have changed, else the saved files read again.</summary>
    private void Refresh()
    {
        if (_rebuild)
        {
            _rebuild = false;
            _saved.Clear();
            Rebuild();
            return;
        }
        var saved = _saved.Keys.ToList();
        foreach (var path in saved) _saved.TryRemove(path, out _);
        var text = new Dictionary<string, string>(_text, StringComparer.OrdinalIgnoreCase);
        var tracked = Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var path in saved.Where(tracked.Contains))
        {
            if (Read(new FileInfo(Path.Combine(Root, path))) is { } content) text[path] = content;
            else text.Remove(path);
            changed = true;
        }
        if (!changed) return;
        _text = text;
        _holders.Clear();
        Changed?.Invoke();
    }

    /// <summary>Builds and fetches churn thousands of files under bin/obj/.git; only tracked-file changes move the index.</summary>
    private static bool IsRelevant(string fullPath)
    {
        var relative = fullPath.Replace('\\', '/');
        if (relative.EndsWith("/.git/index", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var noise in new[] { "/.git/", "/bin/", "/obj/", "/node_modules/" })
            if (relative.Contains(noise, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private void Rebuild()
    {
        try
        {
            var index = SymbolIndex.Build(Root);
            _text = ReadText(Root, index);
            _holders.Clear();
            Index = index;
            Changed?.Invoke();
        }
        catch (Exception e) when (e is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            // .git mid-write or a file locked/deleted by the editor; this runs on a timer thread, so never let it escape.
            // The next change event retries.
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex Words();

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
