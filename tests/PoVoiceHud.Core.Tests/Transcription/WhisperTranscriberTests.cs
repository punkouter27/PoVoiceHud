using System.Diagnostics;
using PoVoiceHud.Core.Transcription;



namespace PoVoiceHud.Core.Tests.Transcription;

[Trait("Category", "Integration")]
[Collection("Timing")]
public class WhisperTranscriberTests
{
    [Fact]
    public async Task ModelStore_downloads_once_and_is_idempotent()
    {
        var first = await ModelStore.EnsureAsync(progress: null, TestContext.Current.CancellationToken);

        var sw = Stopwatch.StartNew();
        var second = await ModelStore.EnsureAsync(progress: null, TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.Equal(first, second);
        Assert.True(new FileInfo(first.Whisper).Length > 100_000_000, "base.en should be ~148 MB");
        Assert.True(new FileInfo(first.Silero).Length > 1_000_000, "silero_vad.onnx should be ~2 MB");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"second EnsureAsync took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Transcribes_the_tenant_question_within_450ms_median()
    {
        var models = await ModelStore.EnsureAsync(progress: null, TestContext.Current.CancellationToken);
        using var transcriber = new WhisperTranscriber(models.Whisper, threads: 8);
        var pcm = SileroVadTests.ReadFixture("tenant-question.wav");

        var text = await transcriber.TranscribeAsync(pcm, TestContext.Current.CancellationToken); // warm-up
        var timings = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            text = await transcriber.TranscribeAsync(pcm, TestContext.Current.CancellationToken);
            timings.Add(sw.ElapsedMilliseconds);
        }
        timings.Sort();
        TestContext.Current.TestOutputHelper?.WriteLine($"text: \"{text}\"  timings ms: {string.Join(", ", timings)}  median: {timings[2]}");

        Assert.Contains("tenant", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(timings[2] <= 450, $"median {timings[2]} ms exceeds the 450 ms ASR budget (SPEC criterion 3)");
    }

    [Fact]
    public async Task A_glossary_prompt_steers_spelling_without_being_echoed_and_can_be_swapped_while_running()
    {
        var models = await ModelStore.EnsureAsync(progress: null, TestContext.Current.CancellationToken);
        var prompt = WhisperTranscriber.PromptFor(["Inspection", "TenantId"]);
        using var transcriber = new WhisperTranscriber(models.Whisper, threads: 8, prompt);
        var pcm = SileroVadTests.ReadFixture("tenant-question.wav");

        var steered = await transcriber.TranscribeAsync(pcm, TestContext.Current.CancellationToken);
        transcriber.Prompt = null;
        var plain = await transcriber.TranscribeAsync(pcm, TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine($"with \"{prompt}\": \"{steered}\"  without: \"{plain}\"");

        Assert.Equal("Glossary: Inspection, TenantId.", prompt);
        Assert.Equal("Glossary: Inspection.", WhisperTranscriber.PromptFor(["Inspection", "TenantId"], maxChars: 15));   // only what fits
        Assert.Null(WhisperTranscriber.PromptFor([]));
        Assert.Equal("Does the inspection table have a TenantId foreign key?", steered);   // spelled as in the code
        Assert.Equal("Does the inspection table have a tenant ID foreign key?", plain);
    }
}
