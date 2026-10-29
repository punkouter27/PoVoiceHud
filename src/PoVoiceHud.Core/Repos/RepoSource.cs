using System.Text.RegularExpressions;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace PoVoiceHud.Core.Repos;

/// <summary>
/// Where the code repository comes from: a folder on this PC that holds a git repository, or an https git URL (GitHub or
/// Azure DevOps) that is cloned into the app's own cache and refreshed on every start. Everything downstream reads the
/// local copy either way.
/// </summary>
public static partial class RepoSource
{
    /// <summary>Clones of remote repositories live here, one folder per repository.</summary>
    public static string CacheDir { get; } = Path.Combine(Config.AppPaths.DataDir, "repos");

    /// <summary>
    /// The repositories in the setting: one per line (or separated by ";"), so several can be connected at once. The one
    /// access token is used for each.
    /// </summary>
    public static IReadOnlyList<string> Locations(string? setting) =>
        (setting ?? "").Split([';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>An https git URL. Plain http is refused: the access token travels with every request.</summary>
    public static bool IsRemote(string? location) => Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// The local copy to read: a folder as it is, a URL cloned on first use and afterwards brought up to the remote's latest
    /// commit. Null when there is nothing to read; <c>Warning</c> says why, or that a refresh failed and last time's copy is used.
    /// </summary>
    public static (string? Root, string? Warning) Open(string location, string? token)
    {
        if (!IsRemote(location))
            return Directory.Exists(location) && Repository.IsValid(location) ? (location, null) : (null, $"{location} is not a git repository.");

        var target = Path.Combine(CacheDir, CacheName(new Uri(location)));
        try
        {
            CloneOrUpdate(location, token, target);
            return (target, null);
        }
        catch (Exception e) when (e is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            return Repository.IsValid(target)
                ? (target, $"The repository was not refreshed ({e.Message.TrimEnd('.')}); using the copy from last time.")
                : (null, $"The repository could not be fetched: {e.Message.TrimEnd('.')}.");
        }
    }

    /// <summary>Clones <paramref name="url"/> into <paramref name="target"/>, or brings an existing clone there up to date.</summary>
    public static void CloneOrUpdate(string url, string? token, string target)
    {
        var credentials = Credentials(token);
        if (Repository.IsValid(target))
        {
            using var repo = new Repository(target);
            Commands.Fetch(repo, "origin", [], new FetchOptions { CredentialsProvider = credentials }, null);
            // Nobody edits the cached clone, so there is nothing to merge: just move to where the remote branch is now.
            if (repo.Head.TrackedBranch?.Tip is { } tip) repo.Reset(ResetMode.Hard, tip);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var options = new CloneOptions();
        options.FetchOptions.CredentialsProvider = credentials;
        Repository.Clone(url, target, options);
    }

    /// <summary>For Apply in Settings: one line saying whether the repository can be read, starting with ✓ or ✗.</summary>
    public static string Probe(string? location, string? token)
    {
        if (string.IsNullOrWhiteSpace(location)) return "✗ Enter a repository URL or a folder.";
        try
        {
            if (IsRemote(location))
            {
                var branches = Repository.ListRemoteReferences(location, Credentials(token)).Count(r => r.CanonicalName.StartsWith("refs/heads/", StringComparison.Ordinal));
                return $"✓ Repository found: {branches} {(branches == 1 ? "branch" : "branches")}.";
            }
            if (!Directory.Exists(location)) return "✗ The repository is neither a folder on this PC nor an https:// URL.";
            if (!Repository.IsValid(location)) return "✗ That folder is not a git repository.";
            using var repo = new Repository(location);
            return $"✓ Git repository on branch {repo.Head.FriendlyName}, {repo.Index.Count} tracked files.";
        }
        catch (LibGit2SharpException e)
        {
            // GitHub and Azure DevOps answer a private repository without a valid token with a sign-in demand, which libgit2 reports in its own words.
            return $"✗ Could not read the repository: {e.Message.TrimEnd('.')}. Check the URL, and for a private repository the access token.";
        }
    }

    /// <summary>
    /// Where a file of the repository opens: its page on GitHub or Azure DevOps for those URLs, otherwise the file itself.
    /// <paramref name="path"/> is repo-relative with forward slashes.
    /// </summary>
    public static Uri Link(string? location, string root, string path)
    {
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        if (GitHubUrl().Match(location ?? "") is { Success: true } hub)
            return new Uri($"https://github.com/{hub.Groups["owner"].Value}/{hub.Groups["repo"].Value}/blob/HEAD/{escaped}");
        if (AzdoUrl().Match(location ?? "") is { Success: true } azdo)
            return new Uri($"https://{azdo.Groups["site"].Value}/_git/{azdo.Groups["repo"].Value}?path=/{escaped}");
        return new Uri(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>What people call the repository: the last part of its URL ("PoPunkouterSoftware") or of its folder.</summary>
    public static string NameOf(string location) =>
        IsRemote(location)
            ? Path.GetFileNameWithoutExtension(new Uri(location).AbsolutePath.TrimEnd('/').Split('/')[^1])
            : Path.GetFileName(location.TrimEnd('\\', '/'));

    // GitHub and Azure DevOps both take the token as the password; the user name is not checked. Without a token the request goes out anonymously.
    private static CredentialsHandler? Credentials(string? token) =>
        string.IsNullOrEmpty(token) ? null : (_, _, _) => new UsernamePasswordCredentials { Username = "pat", Password = token };

    /// <summary>"github.com-acme-api" or "dev.azure.com-acme-PMIS-pmis" from the URL: readable, and unique per repository.</summary>
    private static string CacheName(Uri url) =>
        Unsafe().Replace(string.Join('-', new[] { url.Host }.Concat(url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != "_git"))), "_");

    [GeneratedRegex(@"^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/?#]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrl();

    // https://dev.azure.com/{org}/{project}/_git/{repo} (with or without "{user}@") or https://{org}.visualstudio.com/{project}/_git/{repo}
    [GeneratedRegex(@"^https://(?:[^@/]+@)?(?<site>(?:dev\.azure\.com/[^/]+|[^./]+\.visualstudio\.com(?:/DefaultCollection)?)/[^/]+)/_git/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AzdoUrl();

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex Unsafe();
}
