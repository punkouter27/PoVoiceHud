using PoVoiceHud.Core.Config;

namespace PoVoiceHud.Core.Tests.Config;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PoVoiceHud.Tests", Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Round_trips_all_fields()
    {
        var s = new Settings
        {
            FoundryEndpoint = "https://acme.services.ai.azure.com", LookUpClaims = false, StartWithWindows = true,
            Sounds = false, WhisperThreads = 4, Vocabulary = "Contoso, PMIS", UpdateFeed = @"\\share\PoVoiceHud\releases",
            CueVolume = 40, CallsOnly = true, Repository = "https://github.com/acme/site\nC:\\src\\shop",
        };

        SettingsStore.Save(s, File_);
        var (loaded, warning) = SettingsStore.Load(File_);

        Assert.Equal(s, loaded);
        Assert.Null(warning);
    }

    [Fact]
    public void Missing_file_gives_defaults_with_wikipedia_and_sounds()
    {
        var (loaded, warning) = SettingsStore.Load(File_);

        Assert.Equal(new Settings(), loaded);
        Assert.Null(warning);
        Assert.True(loaded.LookUpClaims && loaded.Sounds);
        Assert.InRange(loaded.WhisperThreads, 2, 8);
    }

    [Fact]
    public void A_file_from_an_older_version_loads_and_keys_that_are_gone_are_ignored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """{ "Mode": "Repo", "RepoPath": "C:\\src\\pmis", "FoundryModel": "Llama-3.3-70B-Instruct", "FoundryEndpoint": "https://acme.services.ai.azure.com", "Effects": "Full", "AudioInput": "Mic", "ListenToCall": false }""");

        var (loaded, warning) = SettingsStore.Load(File_);

        Assert.Null(warning);
        Assert.Equal("https://acme.services.ai.azure.com", loaded.FoundryEndpoint);
        // Keys the old file does not have keep their defaults rather than becoming false or 0.
        Assert.Equal(new Settings { FoundryEndpoint = loaded.FoundryEndpoint }, loaded);
        Assert.Equal(100, loaded.CueVolume);
    }

    [Fact]
    public void Corrupt_file_gives_defaults_with_warning()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ not json");

        var (loaded, warning) = SettingsStore.Load(File_);

        Assert.Equal(new Settings(), loaded);
        Assert.Contains("settings.json", warning);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
