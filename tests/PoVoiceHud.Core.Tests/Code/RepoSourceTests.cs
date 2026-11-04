using PoVoiceHud.Core.Code;
using PoVoiceHud.Core.Tests.Fixtures;




namespace PoVoiceHud.Core.Tests.Code;

[Trait("Category", "Integration")]
public sealed class RepoSourceTests : IDisposable
{
    private readonly string _clone = Path.Combine(Path.GetTempPath(), "PoVoiceHud.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_file_links_to_its_page_on_github_or_azure_devops_and_to_itself_in_a_folder()
    {
        const string path = "src/Services/Invoice Service.cs";
        Assert.Equal("https://github.com/acme/pmis/blob/HEAD/src/Services/Invoice%20Service.cs",
            RepoSource.Link("https://github.com/acme/pmis.git", @"C:\cache", path).AbsoluteUri);
        Assert.Equal("https://dev.azure.com/acme/PMIS/_git/pmis-api?path=/src/Services/Invoice%20Service.cs",
            RepoSource.Link("https://acme@dev.azure.com/acme/PMIS/_git/pmis-api", @"C:\cache", path).AbsoluteUri);   // the form "Clone" copies
        Assert.Equal("https://acme.visualstudio.com/PMIS/_git/pmis?path=/src/Services/Invoice%20Service.cs",
            RepoSource.Link("https://acme.visualstudio.com/PMIS/_git/pmis", @"C:\cache", path).AbsoluteUri);
        Assert.Equal(@"C:\src\pmis\src\Services\Invoice Service.cs", RepoSource.Link(@"C:\src\pmis", @"C:\src\pmis", path).LocalPath);

        Assert.True(RepoSource.IsRemote("https://github.com/acme/pmis"));
        Assert.False(RepoSource.IsRemote("http://github.com/acme/pmis"));   // the token must not travel over plain http
    }

    [Fact]
    public void A_remote_is_cloned_once_and_then_follows_new_commits()
    {
        using var remote = TempRepo.WithSampleProject();

        RepoSource.CloneOrUpdate(remote.Root, token: null, _clone);
        Assert.True(File.Exists(Path.Combine(_clone, "src", "Services", "InvoiceService.cs")));

        remote.Write("src/Services/RetryPolicy.cs", "namespace Pmis.Services;\n\npublic class RetryPolicy { }\n");
        remote.Commit("Add retry policy", new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        RepoSource.CloneOrUpdate(remote.Root, token: null, _clone);

        Assert.True(File.Exists(Path.Combine(_clone, "src", "Services", "RetryPolicy.cs")));
    }

    [Fact]
    public void A_folder_is_read_where_it_is_and_the_probe_says_what_it_found()
    {
        using var repo = TempRepo.WithSampleProject();

        Assert.Equal((repo.Root, null), RepoSource.Open(repo.Root, token: null));
        Assert.Equal((null, $"{Path.GetTempPath()} is not a git repository."), RepoSource.Open(Path.GetTempPath(), token: null));

        Assert.Matches(@"^✓ Git repository on branch \w+, 6 tracked files\.$", RepoSource.Probe(repo.Root, null));
        Assert.StartsWith("✗ That folder is not a git repository", RepoSource.Probe(Path.GetTempPath(), null));
        Assert.StartsWith("✗ The repository is neither a folder", RepoSource.Probe(@"C:\no\such\folder", null));
        Assert.StartsWith("✗ Enter a repository", RepoSource.Probe(" ", null));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_clone)) return;
        foreach (var file in Directory.EnumerateFiles(_clone, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);   // git marks its object files read-only
        Directory.Delete(_clone, recursive: true);
    }
}
