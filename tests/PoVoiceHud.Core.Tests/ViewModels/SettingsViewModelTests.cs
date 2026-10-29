using System.Net;
using PoVoiceHud.Core.Config;
using PoVoiceHud.Core.Providers.FactCheck;
using PoVoiceHud.Core.ViewModels;

namespace PoVoiceHud.Core.Tests.ViewModels;

public class SettingsViewModelTests
{
    private const string Endpoint = "https://acme.services.ai.azure.com";
    private readonly Dictionary<string, string> _secrets = [];
    private readonly List<(Uri Endpoint, string? Key)> _connections = [];
    private readonly List<(string Location, string? Token)> _probes = [];
    private Func<string> _modelReply = () => "OK";
    private Func<string, string> _probeReply = _ => "✓ Repository found: 2 branches.";
    private Settings? _saved;
    private string? _keyWhenSaved;
    private int _done;

    private sealed class StubModel(Func<string> reply) : ILanguageModel
    {
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) => Task.FromResult(reply());
    }

    private SettingsViewModel Build(Settings? settings = null)
    {
        var vm = new SettingsViewModel(
            settings ?? new Settings(),
            readSecret: name => _secrets.GetValueOrDefault(name),
            writeSecret: (name, value) => _secrets[name] = value,
            save: s =>
            {
                _saved = s;
                _keyWhenSaved = _secrets.GetValueOrDefault(CredentialStore.FoundryKey);
            },
            connect: (endpoint, key) =>
            {
                _connections.Add((endpoint, key));
                return new StubModel(_modelReply);
            },
            probeRepository: (location, token) =>
            {
                _probes.Add((location, token));
                return Task.FromResult(_probeReply(location));
            });
        vm.Done += () => _done++;
        return vm;
    }

    [Fact]
    public void Loads_the_settings_and_whether_a_key_is_stored()
    {
        _secrets[CredentialStore.FoundryKey] = "k";
        var vm = Build(new Settings { FoundryEndpoint = Endpoint, Sounds = false });

        Assert.Equal(Endpoint, vm.Endpoint);
        Assert.True(vm.HasKey);
        Assert.Equal("", vm.Key);
        Assert.False(vm.Sounds);
        Assert.True(vm.LookUpClaims);
        Assert.Contains("Wikipedia's and Wikidata's search", vm.LookUpNote);

        _secrets.Clear();
        Assert.False(Build().HasKey);
    }

    [Fact]
    public async Task Apply_checks_a_new_endpoint_and_key_then_writes_the_key_before_the_settings()
    {
        var vm = Build();
        Assert.False(vm.ApplyCommand.CanExecute(null));   // nothing edited yet

        vm.Endpoint = Endpoint;
        vm.Key = "new-key";
        vm.LookUpClaims = false;
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal((new Uri(Endpoint), "new-key"), Assert.Single(_connections));   // the typed key is what gets checked
        Assert.NotNull(_saved);
        Assert.Equal("new-key", _keyWhenSaved);   // applying the settings builds a checker, which reads the key
        Assert.Equal(Endpoint, _saved.FoundryEndpoint);
        Assert.False(_saved.LookUpClaims);
        Assert.True(vm.HasKey);
        Assert.Equal("", vm.Key);
        Assert.Equal("✓ Connected — gpt-4.1-mini answered.", vm.Message);
        Assert.Equal(1, _done);
        Assert.False(vm.ApplyCommand.CanExecute(null));   // the applied state is the new baseline

        vm.Sounds = false;                                // a switch alone needs no new connection check
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.False(_saved.Sounds);
        Assert.Single(_connections);
    }

    [Fact]
    public async Task A_wrong_endpoint_or_key_is_reported_and_nothing_is_applied()
    {
        _secrets[CredentialStore.FoundryKey] = "stored-key";
        var vm = Build();

        vm.Endpoint = "http://acme.services.ai.azure.com";
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Contains("https://", vm.Message);
        Assert.Empty(_connections);

        vm.Endpoint = Endpoint;
        _modelReply = () => throw new HttpRequestException("401", null, HttpStatusCode.Unauthorized);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("✗ The key was refused (HTTP 401).", vm.Message);
        Assert.Equal("stored-key", _connections[^1].Key);   // nothing typed: the stored key is checked

        _modelReply = () => throw new HttpRequestException("404", null, HttpStatusCode.NotFound);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Contains("No gpt-4.1-mini deployment", vm.Message);

        Assert.Null(_saved);
        Assert.Equal(0, _done);   // the settings stay open to fix it
    }

    [Fact]
    public async Task A_repository_is_read_before_it_is_applied_and_its_token_is_stored_only_then()
    {
        _secrets[CredentialStore.FoundryKey] = "k";
        var vm = Build(new Settings { FoundryEndpoint = Endpoint });

        vm.Repository = "https://github.com/acme/private-api";
        vm.RepositoryToken = "wrong-token";
        _probeReply = _ => "✗ Could not read the repository: too many redirects or authentication replays. Check the URL, and for a private repository the access token.";
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.StartsWith("✗ Could not read the repository", vm.Message);
        Assert.Null(_saved);
        Assert.False(_secrets.ContainsKey(CredentialStore.RepoToken));   // a token that did not work is not kept

        vm.RepositoryToken = "good-token";
        _probeReply = _ => "✓ Repository found: 2 branches.";
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(("https://github.com/acme/private-api", "good-token"), _probes[^1]);
        Assert.Equal("https://github.com/acme/private-api", _saved?.Repository);
        Assert.Equal("good-token", _secrets[CredentialStore.RepoToken]);
        Assert.True(vm.HasRepositoryToken);
        Assert.Equal("✓ Repository found: 2 branches.", vm.Message);
        Assert.Empty(_connections);   // the Foundry endpoint and key did not change, so they were not checked again

        vm.Repository = " ";          // clearing it needs no check
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Null(_saved?.Repository);
        Assert.NotNull(_saved);
        Assert.Equal(2, _probes.Count);
    }

    [Fact]
    public async Task Cancel_puts_back_what_was_applied_and_setup_cannot_be_cancelled_until_it_works()
    {
        var vm = Build(new Settings { FoundryEndpoint = Endpoint });
        vm.Endpoint = "https://other.services.ai.azure.com";
        vm.Key = "typed";
        vm.Sounds = false;

        vm.CancelCommand.Execute(null);
        Assert.Equal(Endpoint, vm.Endpoint);
        Assert.Equal("", vm.Key);
        Assert.True(vm.Sounds);
        Assert.Equal(1, _done);
        Assert.Null(_saved);

        _secrets[CredentialStore.FoundryKey] = "stored-key";
        vm.RequireSetup("The key was refused (HTTP 401).");
        Assert.Equal("✗ The key was refused (HTTP 401).", vm.Message);
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.True(vm.ApplyCommand.CanExecute(null));   // nothing edited, but setup always checks again

        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Single(_connections);
        Assert.NotNull(_saved);
        Assert.False(vm.RequiresSetup);
        Assert.True(vm.CancelCommand.CanExecute(null));
    }
}
