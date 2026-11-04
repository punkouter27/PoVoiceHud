using NAudio.Wave;
using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Transcription;



namespace PoVoiceHud.Core.Tests.Transcription;

[Trait("Category", "Integration")]
[Collection("Timing")]
public class SileroVadTests
{
    internal static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoVoiceHud", "models", "silero_vad.onnx");

    internal static async Task EnsureModelAsync()
    {
        if (!File.Exists(ModelPath)) await Core.Transcription.ModelStore.EnsureAsync(progress: null, CancellationToken.None);   // pinned and hash-checked
    }

    internal static short[] ReadFixture(string name)
    {
        using var reader = new WaveFileReader(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        Assert.Equal(PcmFrame.SampleRate, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        var bytes = new byte[reader.Length];
        _ = reader.Read(bytes, 0, bytes.Length);
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    private static List<float> Probabilities(SileroVad vad, short[] samples)
    {
        var probs = new List<float>();
        for (var i = 0; i + PcmFrame.SamplesPerFrame <= samples.Length; i += PcmFrame.SamplesPerFrame)
            probs.Add(vad.Process(samples.AsSpan(i, PcmFrame.SamplesPerFrame)));
        return probs;
    }

    [Fact]
    public async Task Speech_scores_high_and_silence_scores_low()
    {
        await EnsureModelAsync();
        using var vad = new SileroVad(ModelPath);

        var speech = Probabilities(vad, ReadFixture("tenant-question.wav"));
        Assert.True(speech.Count(p => p > 0.5f) > speech.Count / 2, $"speech frames above 0.5: {speech.Count(p => p > 0.5f)}/{speech.Count}");

        vad.Reset();
        var silence = Probabilities(vad, new short[PcmFrame.SampleRate]);
        Assert.All(silence, p => Assert.True(p < 0.1f, $"silence frame scored {p}"));
    }

    [Fact]
    public async Task Reset_clears_carried_state()
    {
        await EnsureModelAsync();
        using var vad = new SileroVad(ModelPath);
        _ = Probabilities(vad, ReadFixture("tenant-question.wav"));

        vad.Reset();
        var first = vad.Process(new short[PcmFrame.SamplesPerFrame]);

        Assert.True(first < 0.1f, $"first silence frame after reset scored {first}");
    }
}
