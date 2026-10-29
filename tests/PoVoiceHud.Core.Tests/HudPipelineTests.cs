using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoVoiceHud.Core.Asr;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Config;
using PoVoiceHud.Core.Providers;
using PoVoiceHud.Core.Providers.FactCheck;
using PoVoiceHud.Core.Tests.Fixtures;
using PoVoiceHud.Core.Tests.Vad;
using PoVoiceHud.Core.Vad;

namespace PoVoiceHud.Core.Tests;

[Trait("Category", "Integration")]
[Collection("Timing")]
public class HudPipelineTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Blocks inside TranscribeAsync until released, so tests can mute mid-transcription.</summary>
    private sealed class GatedTranscriber(string text) : ITranscriber
    {
        public TaskCompletionSource Started { get; } = new();
        public TaskCompletionSource Release { get; } = new();
        public int Calls { get; private set; }

        public async Task<string> TranscribeAsync(short[] pcm, CancellationToken ct)
        {
            Calls++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return text;
        }

        public void Dispose() { }
    }

    /// <summary>Scripted fact checker that counts what it is asked.</summary>
    private sealed class FakeChecker(Func<string, Insight?> answer) : IFactChecker
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public System.Collections.Concurrent.ConcurrentQueue<(string Sentence, IReadOnlyList<string> Context)> Asked { get; } = new();

        public Task<Insight?> CheckAsync(string sentence, IReadOnlyList<string> context, string? screen, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Asked.Enqueue((sentence, context));
            return Task.FromResult(answer(sentence));
        }
    }

    private const string Claim = "The Eiffel Tower was completed in 1889";

    private static async Task<SileroVad> VadAsync()
    {
        await SileroVadTests.EnsureModelAsync();
        return new SileroVad(SileroVadTests.ModelPath);
    }

    private static FakeAudioSource Replay() => new(SileroVadTests.ReadFixture("tenant-question.wav"), TimeSpan.FromSeconds(1));

    [Fact]
    public async Task Replayed_speech_is_transcribed_within_1200ms_and_a_question_is_not_sent_to_the_checker()
    {
        var models = await ModelStore.EnsureAsync(null, _ct);
        using var vad = await VadAsync();
        using var transcriber = new WhisperTranscriber(models.Whisper, threads: 8);
        var checker = new FakeChecker(_ => null);
        using var pipeline = new HudPipeline(Replay(), vad, transcriber, checker, NullLogger<HudPipeline>.Instance);

        var sw = Stopwatch.StartNew();
        pipeline.Start();
        var line = await pipeline.Transcript.ReadAsync(_ct).AsTask().WaitAsync(Timeout, _ct);
        sw.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"transcribed in {sw.ElapsedMilliseconds} ms: \"{line.Text}\"");
        Assert.Contains("tenant", line.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SourceHint.Teams, line.Source);
        Assert.True(sw.ElapsedMilliseconds <= 1200, $"transcription took {sw.ElapsedMilliseconds} ms (≤ 1200 ms)");
        Assert.Equal(0, checker.Calls);   // "does the inspection table have…" is a question, not a claim
    }

    [Fact]
    public async Task A_claim_is_marked_checking_until_its_verdict_arrives_with_its_line()
    {
        using var vad = await VadAsync();
        var transcriber = new GatedTranscriber(Claim);
        transcriber.Release.SetResult();
        var checker = new FakeChecker(s => new Insight(s, Verdict.Supported, "Yes.", 0.9));
        using var pipeline = new HudPipeline(Replay(), vad, transcriber, checker, NullLogger<HudPipeline>.Instance);

        pipeline.Start();
        var line = await pipeline.Transcript.ReadAsync(_ct).AsTask().WaitAsync(Timeout, _ct);
        var verdict = await pipeline.Verdicts.ReadAsync(_ct).AsTask().WaitAsync(Timeout, _ct);

        Assert.Equal(line, verdict.Line);
        Assert.Equal(Verdict.Supported, verdict.Verdict.Verdict);
        Assert.Equal((line, true), await pipeline.Checking.ReadAsync(_ct).AsTask().WaitAsync(Timeout, _ct));    // busy while the checker works…
        Assert.Equal((line, false), await pipeline.Checking.ReadAsync(_ct).AsTask().WaitAsync(Timeout, _ct));   // …and released afterwards
    }

    [Fact]
    public async Task A_checker_that_cannot_be_reached_is_reported_once_and_context_includes_lines_that_were_not_checked()
    {
        using var vad = await VadAsync();
        var transcriber = new GatedTranscriber("Okay. The Eiffel Tower was completed in 1889. Python was created by Dennis Ritchie.");
        transcriber.Release.SetResult();
        var checker = new FakeChecker(_ => throw new FactCheckUnavailableException("The fact-check service answered HTTP 503."));
        using var pipeline = new HudPipeline(Replay(), vad, transcriber, checker, NullLogger<HudPipeline>.Instance);

        pipeline.Start();
        var deadline = DateTime.UtcNow + Timeout;
        while (checker.Calls < 2 && DateTime.UtcNow < deadline) await Task.Delay(20, _ct);
        await Task.Delay(200, _ct);
        Assert.Equal(2, checker.Calls);

        var statuses = new List<string>();
        while (pipeline.Status.TryRead(out var status)) statuses.Add(status);
        Assert.Single(statuses, s => s == "Fact checks paused: The fact-check service answered HTTP 503.");   // once, not per line
        Assert.False(pipeline.Verdicts.TryRead(out _));                                                         // and no "can't tell" on the lines
        var context = checker.Asked.First(a => a.Sentence.StartsWith("The Eiffel", StringComparison.Ordinal)).Context;
        Assert.Equal(["Okay."], context);   // "Okay." was never checked, but it is still what came before
    }

    [Fact]
    public async Task Mute_discards_queued_audio_and_the_transcription_in_flight()
    {
        using var vad = await VadAsync();
        var transcriber = new GatedTranscriber(Claim);
        var audio = Replay();
        var checker = new FakeChecker(_ => null);
        using var pipeline = new HudPipeline(audio, vad, transcriber, checker, NullLogger<HudPipeline>.Instance);

        pipeline.Start();
        await transcriber.Started.Task.WaitAsync(Timeout, _ct);
        pipeline.IsMuted = true;
        transcriber.Release.SetResult();
        await Task.Delay(300, _ct);

        Assert.True(audio.IsMuted);
        Assert.False(pipeline.Transcript.TryRead(out _));
        Assert.False(pipeline.Verdicts.TryRead(out _));
        Assert.Equal(0, checker.Calls);
        Assert.Equal(1, transcriber.Calls);
    }

    [Fact]
    public async Task Blank_transcriptions_never_reach_the_transcript_or_the_checker()
    {
        using var vad = await VadAsync();
        var transcriber = new GatedTranscriber("");
        transcriber.Release.SetResult();
        var checker = new FakeChecker(_ => null);
        using var pipeline = new HudPipeline(Replay(), vad, transcriber, checker, NullLogger<HudPipeline>.Instance);

        pipeline.Start();
        await transcriber.Started.Task.WaitAsync(Timeout, _ct);
        await Task.Delay(300, _ct);

        Assert.False(pipeline.Transcript.TryRead(out _));
        Assert.Equal(0, checker.Calls);
    }

    [Fact]
    public async Task Status_from_the_audio_source_is_forwarded()
    {
        using var vad = await VadAsync();
        var transcriber = new GatedTranscriber("");
        transcriber.Release.SetResult();
        using var pipeline = new HudPipeline(new FakeAudioSource([], TimeSpan.Zero), vad, transcriber, new FakeChecker(_ => null), NullLogger<HudPipeline>.Instance);

        pipeline.Start();

        Assert.Equal("Replaying", await pipeline.Status.ReadAsync(_ct).AsTask().WaitAsync(Timeout, _ct));
    }

    [Fact]
    public async Task Container_without_an_endpoint_wires_a_checker_that_says_setup_is_needed()
    {
        await SileroVadTests.EnsureModelAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IAudioSource>(new FakeAudioSource([], TimeSpan.Zero));
        services.AddSingleton<ITranscriber>(new GatedTranscriber(""));
        services.AddPoVoiceHudCore(new Settings(), new ModelPaths("unused", SileroVadTests.ModelPath));
        await using var container = services.BuildServiceProvider();

        Assert.NotNull(container.GetRequiredService<HudPipeline>());
        var problem = await Assert.ThrowsAsync<FactCheckUnavailableException>(() => container.GetRequiredService<IFactChecker>().CheckAsync(Claim, [], null, _ct));
        Assert.True(problem.NeedsSetup);
    }
}
