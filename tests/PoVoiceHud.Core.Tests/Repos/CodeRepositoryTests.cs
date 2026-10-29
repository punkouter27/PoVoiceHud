using System.Text.Json;
using PoVoiceHud.Core.Providers;
using PoVoiceHud.Core.Providers.FactCheck;
using PoVoiceHud.Core.Repos;
using PoVoiceHud.Core.Tests.Fixtures;

namespace PoVoiceHud.Core.Tests.Repos;

[Trait("Category", "Integration")]
public class CodeRepositoryTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class FakeModel(Func<string, string> answer) : ILanguageModel
    {
        public List<string> Messages { get; } = [];
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct)
        {
            Messages.Add(user);
            return Task.FromResult(answer(user));
        }
    }

    private static CodeRepository Open(TempRepo repo) => new(repo.Root, "PoPunkouterSoftware", path => new Uri($"https://github.com/acme/pmis/blob/HEAD/{path}"));

    [Fact]
    public void Indexes_tracked_files_and_ef_entities_and_gives_whisper_the_project_words()
    {
        using var repo = TempRepo.WithSampleProject();
        var index = SymbolIndex.Build(repo.Root);

        var file = Assert.Single(index.Symbols, s => s.Kind == SymbolKind.File && s.Name == "InvoiceService");
        Assert.Equal("src/Services/InvoiceService.cs", file.Path);
        var entity = Assert.Single(index.Symbols, s => s.Kind == SymbolKind.Entity && s.Name == "Inspection");
        Assert.Equal(("src/Infrastructure/Data/Migrations/AppDbContextModelSnapshot.cs", 9), (entity.Path, entity.Line));

        // Whisper's glossary: entities first, then the domain words in file names, without the ones every codebase has.
        var words = index.Vocabulary().ToList();
        Assert.Equal(["Inspection", "Tenant"], words.Take(2));
        Assert.Contains("Invoice", words);
        Assert.DoesNotContain("Service", words);
        Assert.Equal(words.Count, words.Distinct(StringComparer.OrdinalIgnoreCase).Count());   // "Inspection" is an entity and a file: once
    }

    [Fact]
    public void Files_are_found_by_the_names_said_and_by_what_their_text_says()
    {
        using var repo = TempRepo.WithSampleProject();
        repo.Write("src/Program.cs", "var app = WebApplication.Create();\napp.Run();\n");
        repo.Write("src/Payments/Gateway.cs", "namespace Pmis.Payments;\n\n// Card payments go through Stripe.\npublic class Gateway\n{\n    public void Charge() => Stripe.Charge();\n}\n");
        repo.Commit("Add a host and payments", DateTimeOffset.UtcNow);
        using var code = Open(repo);

        var named = Assert.Single(code.Named("The invoice service retries failed payments three times", 3));
        Assert.Equal(("src/Services/InvoiceService.cs", 3), (named.Path, named.Line));   // the line of "public class InvoiceService"
        Assert.Contains("public class InvoiceService", named.Text);
        Assert.Empty(code.Named("The program was first released in 1991", 3));        // a name every codebase has is not a mention
        Assert.Contains(code.Named("The inspections are tied to a tenent", 3), e => e.Path.EndsWith("Inspection.cs", StringComparison.Ordinal));   // plurals and mishearings

        var found = code.Search("which provider handles card payments, is it stripe?", 3);
        Assert.Equal(("src/Payments/Gateway.cs", 3), (found[0].Path, found[0].Line));   // the line where "card", "payments" and "stripe" meet
        Assert.Empty(code.Search("the weather is nice", 3));

        Assert.Equal($"vscode://file/{repo.Root.Replace('\\', '/')}/src/Payments/Gateway.cs:3", Uri.UnescapeDataString(code.Editor("src/Payments/Gateway.cs", 3).OriginalString));
        Assert.Contains("src/Payments/Gateway.cs", code.Paths);
    }

    [Fact]
    public void Talk_about_the_code_is_recognised_by_code_words_the_repository_name_or_a_file_it_names()
    {
        using var repo = TempRepo.WithSampleProject();
        repo.Write("src/Api/web.config", "<configuration />");          // files named like everyday words
        repo.Write("src/Styles/theme-kit.css", "body { }");
        repo.Write("src/Payments/Gateway.cs", "public class Gateway { }");
        repo.Commit("Add config, styles and payments", DateTimeOffset.UtcNow);
        var answerer = new CodeAnswerer(model: null, Open(repo));

        Assert.True(answerer.IsAbout("How many pages are in the Punkouter software website?"));   // the repository's name, as said
        Assert.True(answerer.IsAbout("How many pages are on our website?"));
        Assert.True(answerer.IsAbout("Is there a test for that in the codebase?"));
        Assert.True(answerer.IsAbout("The invoice service retries three times."));                // names a file
        Assert.False(answerer.IsAbout("The Eiffel Tower was completed in 1889."));
        Assert.False(answerer.IsAbout("our code?"));                                               // too short to say anything
        Assert.True(answerer.IsAbout(", which model does are app use for fact checking."));        // "our" as Whisper heard it
        Assert.False(answerer.IsAbout("There are app stores on every phone."));

        // Ordinary talk heard in a news podcast: short words are not file names ("we"/web.config, "there … it"/theme-kit.css).
        Assert.False(answerer.IsAbout("We're not safe because John Solomon"));
        Assert.False(answerer.IsAbout("How is there any other interpretation of not alerting the voting company if it's uncovered flaws?"));
        Assert.False(answerer.IsAbout("Now, six years later, we've had some midterm elections that are two weeks from today"));
        Assert.True(answerer.IsAbout("Does the gateway charge cards twice?"));                    // a one-word name that is a code file

        // A follow-on: it points back at, or asks about, what the sentence before was about.
        string[] repoTalk = ["Tell me about the Punkouter Software code repo."];
        Assert.True(answerer.IsAbout("How many different pages are in it?", repoTalk));
        Assert.True(answerer.IsAbout("And which one is the newest?", repoTalk));
        Assert.False(answerer.IsAbout("How many different pages are in it?", ["I read a great book last week."]));
        Assert.False(answerer.IsAbout("The Eiffel Tower was completed in 1889.", repoTalk));   // a new subject, said outright

        Assert.True(CodeAnswerer.IsAsked("Tell me about the Punkouter Software code repo."));
        Assert.True(CodeAnswerer.IsAsked("Okay, can you show me where payments are handled."));
        Assert.False(CodeAnswerer.IsAsked("The invoice service retries three times."));
        Assert.True(CodeAnswerer.IsAsked(", which model does are app use for fact checking."));   // Whisper's full stop on a question
        Assert.True(CodeAnswerer.IsAsked("How many pages are on our website."));
        Assert.False(CodeAnswerer.IsAsked("When the app starts, it clones the repo."));
        Assert.False(CodeAnswerer.IsAsked("Which is why our app retries three times."));
        Assert.False(answerer.IsAbout("Who wrote Hamlet.", repoTalk));                             // a new subject, not a question about the code
    }

    [Fact]
    public async Task A_question_gets_an_answer_and_a_statement_a_verdict_from_the_file_list_and_excerpts()
    {
        using var repo = TempRepo.WithSampleProject();
        using var code = Open(repo);
        var model = new FakeModel(user => user.Contains("How many", StringComparison.Ordinal)
            ? """{"kind":"question","topic":"How many entities are there","evidence":"","text":"Two: Inspection and Tenant.","verdict":"","files":["src/Domain/Entities/Inspection.cs","/src/Domain/Entities/Tenant.cs","src/Made/Up.cs"]}"""
            : """{"kind":"statement","topic":"The invoice service retries three times","evidence":"public  class InvoiceService { }","text":"It has no retry logic.","verdict":"contradicted","files":["src/Services/InvoiceService.cs"]}""");
        var answerer = new CodeAnswerer(model, code);

        var answer = await answerer.AskAsync("How many entities are there in our code?", ["We were looking at the schema."], _ct);
        Assert.NotNull(answer);
        Assert.True(answer.IsAnswer);
        Assert.Equal("Two: Inspection and Tenant.", answer.Text);
        Assert.Equal(["src/Domain/Entities/Inspection.cs", "src/Domain/Entities/Tenant.cs"], answer.Sources.Select(s => s.Path));   // the invented file is dropped
        using (var sent = JsonDocument.Parse(model.Messages[0]))
        {
            Assert.Equal("PoPunkouterSoftware", sent.RootElement.GetProperty("repository").GetString());
            Assert.Equal("We were looking at the schema.", sent.RootElement.GetProperty("context")[0].GetString());
            Assert.Contains(sent.RootElement.GetProperty("files").EnumerateArray(), f => f.GetString() == "src/Services/InvoiceService.cs");
        }

        var request = await new CodeAnswerer(new FakeModel(_ => """{"kind":"statement","verdict":"supported","topic":"Tell me about the repo","text":"A Blazor app.","files":[]}"""), code)
            .AskAsync("Tell me about the repo.", [], _ct);
        Assert.True(request?.IsAnswer);   // a request is answered, even when the model calls it a statement

        var verdict = await answerer.AskAsync("The invoice service retries three times.", [], _ct);
        Assert.Equal((Verdict.Contradicted, "The invoice service retries three times"), (verdict?.Verdict, verdict?.Topic));
        var source = Assert.Single(verdict!.Sources);
        Assert.Equal("https://github.com/acme/pmis/blob/HEAD/src/Services/InvoiceService.cs", source.Link.AbsoluteUri);
        Assert.EndsWith("InvoiceService.cs:3", source.Editor.OriginalString);   // at the class, since the excerpt sent was about it
        using (var sent = JsonDocument.Parse(model.Messages[1]))
            Assert.Equal("src/Services/InvoiceService.cs", sent.RootElement.GetProperty("excerpts")[0].GetProperty("path").GetString());

        // A verdict whose evidence is not in anything the model was shown is a guess: Can't tell.
        foreach (var quote in new[] { "File.WriteAllText(\\\"key.txt\\\", key)", "public" })   // invented, or too short to say anything
        {
            var guess = await new CodeAnswerer(new FakeModel(_ => $$"""{"kind":"statement","topic":"The repo stores the API key in a text file","evidence":"{{quote}}","text":"Keys are written by the settings.","verdict":"supported","files":[]}"""), code)
                .AskAsync("The repo stores the API key in a text file.", [], _ct);
            Assert.Equal(Verdict.Unverified, guess?.Verdict);
        }

        Assert.Null(await new CodeAnswerer(new FakeModel(_ => """{"kind":"not_about_code","verdict":"","topic":"","text":"","files":[]}"""), code)
            .AskAsync("Our website has a nice colour.", [], _ct));
        await Assert.ThrowsAsync<FactCheckUnavailableException>(() => new CodeAnswerer(model: null, code).AskAsync("How many pages are on our website?", [], _ct));
    }

    [Fact]
    public async Task The_index_follows_new_files_and_a_folder_that_is_not_a_repository_is_refused()
    {
        using var repo = TempRepo.WithSampleProject();
        using var code = Open(repo);
        var changed = new TaskCompletionSource();
        code.Changed += () => changed.TrySetResult();
        Assert.DoesNotContain(code.Index.Symbols, s => s.Name == "PaymentController");

        repo.Write("src/Api/PaymentController.cs", "public class PaymentController { }");
        repo.Commit("Add payments", DateTimeOffset.UtcNow);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        Assert.Contains(code.Index.Symbols, s => s.Name == "PaymentController");
        Assert.Contains("src/Api/PaymentController.cs", code.Paths);

        // A saved file (no commit) is read again on its own, and search finds what it says now.
        Assert.Empty(code.Search("refunds go through braintree", 3));
        var saved = new TaskCompletionSource();
        code.Changed += () => saved.TrySetResult();
        repo.Write("src/Api/PaymentController.cs", "public class PaymentController { /* refunds go through Braintree */ }");
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        Assert.Equal("src/Api/PaymentController.cs", code.Search("refunds go through braintree", 3)[0].Path);

        Assert.Throws<ArgumentException>(() => new CodeRepository(Path.GetTempPath(), "temp", path => new Uri("https://example.com/")));
    }

    [Fact]
    public async Task With_several_repositories_a_sentence_goes_to_the_one_it_is_about()
    {
        using var site = TempRepo.WithSampleProject();
        using var shop = TempRepo.WithSampleProject();
        shop.Write("src/Basket/CheckoutPipeline.cs", "public class CheckoutPipeline { }");
        shop.Commit("Add checkout", DateTimeOffset.UtcNow);
        using var siteCode = Open(site);
        using var shopCode = new CodeRepository(shop.Root, "AcmeShop", path => new Uri($"https://github.com/acme/shop/blob/HEAD/{path}"));
        var model = new FakeModel(_ => """{"kind":"question","topic":"t","evidence":"","text":"x","verdict":"","confidence":0.8,"files":[]}""");
        var answerer = new CodeAnswerer(model, [siteCode, shopCode]);

        Assert.Same(shopCode, answerer.Pick("What does the acme shop do at checkout?"));     // its name
        Assert.Same(shopCode, answerer.Pick("Is the checkout pipeline async?"));             // a class only it has
        Assert.Same(siteCode, answerer.Pick("Tell me about the code."));                     // nothing to go on: the first
        Assert.True(answerer.IsAbout("Is the checkout pipeline async?"));

        var fact = await answerer.AskAsync("Is the checkout pipeline async?", [], _ct);
        Assert.Equal(("AcmeShop", 0.8), (fact?.Repository, fact?.Confidence));   // named, since there are two
        using var sent = JsonDocument.Parse(model.Messages[0]);
        Assert.Equal("AcmeShop", sent.RootElement.GetProperty("repository").GetString());
    }
}
