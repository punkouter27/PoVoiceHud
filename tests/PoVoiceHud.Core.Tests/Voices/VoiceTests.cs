using PoVoiceHud.Core.Tests.Transcription;
using PoVoiceHud.Core.Transcription;
using PoVoiceHud.Core.Voices;


namespace PoVoiceHud.Core.Tests.Voices;

public class VoiceClustersTests
{
    private static float[] Print(params float[] direction) => VoicePrinter.Normalised(direction);

    [Fact]
    public void Prints_join_the_voice_they_match_new_voices_need_a_sure_print_and_a_quiet_spell_starts_again()
    {
        var clusters = new VoiceClusters();
        var at = DateTimeOffset.Parse("2026-11-04T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(1, clusters.Assign(Print(1, 0.1f, 0), at));
        Assert.Equal(2, clusters.Assign(Print(0, 1, 0.1f), at.AddSeconds(5)));
        Assert.Equal(1, clusters.Assign(Print(0.9f, 0.2f, 0), at.AddSeconds(9)));   // close to the first voice
        Assert.Equal(0, clusters.Assign(Print(0, 0, 1), at.AddSeconds(12), sure: false));   // a short one never starts a voice
        Assert.Equal(2, clusters.Assign(Print(0.1f, 1, 0), at.AddSeconds(15), sure: false));   // but joins one it matches
        Assert.Equal(3, clusters.Assign(Print(0, 0, 1), at.AddSeconds(20)));
        Assert.Equal(3, clusters.Count);

        Assert.Equal(1, clusters.Assign(Print(0, 0, 1), at.AddMinutes(31)));   // another meeting: numbered afresh
        Assert.Equal(1, clusters.Count);
    }

    [Fact]
    public void The_filterbank_has_one_row_per_10_ms_puts_each_tone_in_its_mel_bin_and_removes_the_mean()
    {
        // 0.5 s of 500 Hz, then 0.5 s of 2 kHz: 98 frames of 25 ms every 10 ms.
        var pcm = new short[16_000];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * (i < 8000 ? 500 : 2000) * i / 16_000.0));

        var features = Fbank.Compute(pcm, out var frames);
        int Loudest(int frame) => Enumerable.Range(0, Fbank.Bins).MaxBy(m => features[frame * Fbank.Bins + m]);

        Assert.Equal(98, frames);
        Assert.Equal(98 * Fbank.Bins, features.Length);
        var mean = Enumerable.Range(0, Fbank.Bins).Select(m => Enumerable.Range(0, frames).Average(f => features[f * Fbank.Bins + m]));
        Assert.All(mean, m => Assert.True(Math.Abs(m) < 1e-3, $"bin mean {m}"));
        // On Kaldi's mel scale, 80 bins from 20 Hz to 8 kHz: 500 Hz falls in bins 15–16 and 2 kHz in 42.
        Assert.InRange(Loudest(10), 14, 17);
        Assert.InRange(Loudest(85), 41, 44);
        Assert.Empty(Fbank.Compute(new short[399], out var none));
        Assert.Equal(0, none);
    }
}

[Trait("Category", "Integration")]
[Collection("Timing")]
public class VoicePrinterTests
{
    private static readonly string[] Said = ["voice-b.wav", "voice-a.wav", "voice-a.wav"];

    [Fact]
    public async Task Two_recordings_of_one_voice_print_alike_and_another_voice_does_not()
    {
        var models = await ModelStore.EnsureAsync(WhisperModels.Default, voices: true, progress: null, TestContext.Current.CancellationToken);
        using var printer = new VoicePrinter(models.Voices!);
        var a = printer.Print(SileroVadTests.ReadFixture("voice-a.wav"))!;
        var b = printer.Print(SileroVadTests.ReadFixture("voice-b.wav"))!;
        var b2 = printer.Print(SileroVadTests.ReadFixture("voice-b2.wav"))!;
        static float Cosine(float[] x, float[] y) => x.Zip(y, (p, q) => p * q).Sum();
        TestContext.Current.TestOutputHelper?.WriteLine($"same voice {Cosine(b, b2):0.00}, different {Cosine(a, b):0.00}");

        Assert.Equal(256, a.Length);
        Assert.Null(printer.Print(new short[VoicePrinter.MinSamples - 1]));   // under a second: too unsure
        Assert.True(Cosine(b, b2) >= VoiceClusters.SameVoice + 0.2f, $"same voice scored {Cosine(b, b2):0.00}");
        Assert.True(Cosine(a, b) < VoiceClusters.SameVoice - 0.1f, $"different voices scored {Cosine(a, b):0.00}");

        var voices = new CallVoices(printer);
        var at = DateTimeOffset.Now;
        Assert.Equal([1, 2, 2], Said.Select(f => voices.Identify(SileroVadTests.ReadFixture(f), at)));
    }
}
