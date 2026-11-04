using System.Net;
using PoVoiceHud.Core.Config;
using PoVoiceHud.Core.Models;
using PoVoiceHud.Core.Tests.Fixtures;





namespace PoVoiceHud.Core.Tests.Models;

public class LocalModelTests
{
    private const string Reply = """{"choices":[{"message":{"content":"OK"}}],"usage":{"total_tokens":7}}""";

    [Fact]
    public async Task A_local_model_is_asked_on_its_own_chat_url_by_name_with_a_bearer_key_and_counted_as_local()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(Reply));
        var stats = new ModelStats();
        var local = new FoundryClient(new HttpClient(handler), new Uri("http://localhost:11434/v1"), "secret", stats: stats, localModel: "qwen2.5:7b");

        Assert.Equal("OK", await local.CompleteAsync("system", "user", TestContext.Current.CancellationToken));

        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("Bearer secret", handler.Requests[0].Headers.Authorization!.ToString());
        Assert.False(handler.Requests[0].Headers.Contains("api-key"));
        Assert.Contains("\"model\":\"qwen2.5:7b\"", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"json_schema\"", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Equal(("qwen2.5:7b", true), (local.Name, local.IsLocal));
        Assert.Equal(new Uri("http://localhost:1234/v1/chat/completions"), FoundryClient.ResolveLocal(new Uri("http://localhost:1234")));
        Assert.Equal(new Uri("http://box:5273/v1/chat/completions"), FoundryClient.ResolveLocal(new Uri("http://box:5273/v1/chat/completions")));
        Assert.True(FoundryClient.IsValidLocalEndpoint("http://localhost:11434/v1"));
        Assert.False(FoundryClient.IsValidLocalEndpoint("localhost:11434"));
        Assert.Contains("1 model call", stats.Summary, StringComparison.Ordinal);

        // A model the server does not have is a setup problem that says how to get it.
        var missing = new FoundryClient(new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            new Uri("http://localhost:11434/v1"), null, localModel: "llama3.1");
        var problem = await Connection.TestAsync(missing, TestContext.Current.CancellationToken);
        Assert.True(problem is { NeedsSetup: true });
        Assert.Contains("ollama pull llama3.1", problem!.Message, StringComparison.Ordinal);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Scripted(string name, Func<int, string> reply) : IChatModel
    {
        public int Calls { get; private set; }
        public string Name => name;
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) => Task.FromResult(reply(++Calls));
        public Task<string> CompleteAsync(string system, string user, ReadOnlyMemory<byte> jpeg, CancellationToken ct) => CompleteAsync(system, user, ct);
    }

    [Fact]
    public async Task Azure_failing_moves_every_call_to_the_local_model_for_a_while_then_Azure_is_tried_again()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-04T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var circuit = new FallbackCircuit(clock);
        var switched = new List<bool>();
        circuit.Switched += switched.Add;
        var azureUp = true;
        var azure = new Scripted("gpt-4.1-mini", _ => azureUp ? "azure" : throw new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable));
        var local = new Scripted("qwen2.5:7b", _ => "local");
        var model = new FallbackModel(azure, local, circuit);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("azure", await model.CompleteAsync("s", "u", ct));
        azureUp = false;
        Assert.Equal("local", await model.CompleteAsync("s", "u", ct));   // the failed call is answered locally
        Assert.Equal("local", await model.CompleteAsync("s", "u", ct));   // and Azure is left alone meanwhile
        Assert.Equal((2, "qwen2.5:7b"), (azure.Calls, model.Name));

        azureUp = true;
        clock.Now += FallbackCircuit.Pause + TimeSpan.FromSeconds(1);
        Assert.Equal("azure", await model.CompleteAsync("s", "u", ct));
        Assert.Equal([true, false], switched);

        // One sentence refused by Azure's filter is judged locally, without leaving Azure.
        var filtered = new FallbackModel(new Scripted("gpt-4.1-mini", _ => throw new ContentFilteredException()), local, circuit);
        Assert.Equal("local", await filtered.CompleteAsync("s", "u", ct));
        Assert.False(circuit.UsesLocal);
    }

    [Fact]
    public void Setup_is_complete_when_the_chosen_mode_has_what_it_needs()
    {
        var azure = new Settings { FoundryEndpoint = "https://acme.services.ai.azure.com" };
        var local = new Settings { ModelMode = ModelMode.Local, LocalEndpoint = "http://localhost:11434/v1", LocalModel = "qwen2.5:7b" };

        Assert.True(Connection.IsReady(azure, hasKey: true));
        Assert.False(Connection.IsReady(azure, hasKey: false));
        Assert.True(Connection.IsReady(local, hasKey: false));   // no Azure key needed for a model on this PC
        Assert.False(Connection.IsReady(local with { LocalModel = " " }, hasKey: true));
        Assert.False(Connection.IsReady(azure with { ModelMode = ModelMode.AzureThenLocal }, hasKey: true));   // the fallback needs its server too
        Assert.True(Connection.IsReady(local with { ModelMode = ModelMode.AzureThenLocal, FoundryEndpoint = azure.FoundryEndpoint }, hasKey: true));
    }
}
