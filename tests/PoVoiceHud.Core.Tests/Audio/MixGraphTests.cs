using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PoVoiceHud.Core.Audio;

namespace PoVoiceHud.Core.Tests.Audio;

public class MixGraphTests
{
    private static readonly WaveFormat Loop48kStereoFloat = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
    private static readonly WaveFormat Mic44kMono16 = new(44_100, 16, 1);

    private static byte[] Tone(WaveFormat format, double seconds, double gain)
    {
        var gen = new SignalGenerator(format.SampleRate, format.Channels) { Frequency = 440, Gain = gain }
            .Take(TimeSpan.FromSeconds(seconds));
        var provider = format.Encoding == WaveFormatEncoding.IeeeFloat ? gen.ToWaveProvider() : gen.ToWaveProvider16();
        var bytes = new byte[(int)(format.AverageBytesPerSecond * seconds)];
        var read = provider.Read(bytes, 0, bytes.Length);
        return bytes[..read];
    }

    private static double Rms(ReadOnlySpan<short> s)
    {
        double sum = 0;
        foreach (var v in s) sum += (double)v * v;
        return Math.Sqrt(sum / s.Length);
    }

    [Fact]
    public void Emits_100ms_frames_of_16kHz_mono_with_sequential_timestamps()
    {
        var graph = new MixGraph(Loop48kStereoFloat, Mic44kMono16);
        graph.AddLoopback(Tone(Loop48kStereoFloat, 1.0, 0.5));

        var frames = Enumerable.Range(0, 10).Select(_ => graph.ReadFrame()).ToList();

        Assert.All(frames, f => Assert.Equal(PcmFrame.SamplesPerFrame, f.Samples.Length));
        Assert.Equal(TimeSpan.Zero, frames[0].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(900), frames[9].Timestamp);
        Assert.All(frames.Skip(1), f => Assert.True(Rms(f.Samples.Span) > 1000, "tone should be audible in every frame"));
    }

    [Fact]
    public void Source_hint_follows_the_louder_input()
    {
        var graph = new MixGraph(Loop48kStereoFloat, Mic44kMono16);
        graph.AddLoopback(Tone(Loop48kStereoFloat, 0.5, 0.5));
        graph.AddMic(Tone(Mic44kMono16, 0.5, 0.01));
        Assert.Equal(SourceHint.Teams, graph.ReadFrame().Source);

        graph = new MixGraph(Loop48kStereoFloat, Mic44kMono16);
        graph.AddLoopback(Tone(Loop48kStereoFloat, 0.5, 0.01));
        graph.AddMic(Tone(Mic44kMono16, 0.5, 0.5));
        Assert.Equal(SourceHint.Mic, graph.ReadFrame().Source);

        graph = new MixGraph(Loop48kStereoFloat, Mic44kMono16);
        Assert.Equal(SourceHint.Unknown, graph.ReadFrame().Source);
    }

    [Fact]
    public void Pads_silence_when_an_input_is_starved()
    {
        var graph = new MixGraph(Loop48kStereoFloat, Mic44kMono16);
        graph.AddLoopback(Tone(Loop48kStereoFloat, 0.3, 0.5));

        var frames = Enumerable.Range(0, 10).Select(_ => graph.ReadFrame()).ToList();

        Assert.True(Rms(frames[1].Samples.Span) > 1000);
        Assert.All(frames.Skip(5), f => Assert.Equal(0, Rms(f.Samples.Span)));
    }

    [Fact]
    public void Mic_only_graph_hints_mic_for_everything_audible()
    {
        var graph = new MixGraph(loopFormat: null, Mic44kMono16);
        graph.AddMic(Tone(Mic44kMono16, 0.5, 0.5));

        var frame = graph.ReadFrame();

        Assert.Equal(SourceHint.Mic, frame.Source);
        Assert.True(Rms(frame.Samples.Span) > 1000);
    }

    [Fact]
    public void Clear_drops_everything_buffered()
    {
        var graph = new MixGraph(Loop48kStereoFloat, Mic44kMono16);
        graph.AddLoopback(Tone(Loop48kStereoFloat, 1.0, 0.5));
        graph.AddMic(Tone(Mic44kMono16, 1.0, 0.5));

        graph.Clear();

        Assert.Equal(0, Rms(graph.ReadFrame().Samples.Span));
        Assert.Equal(0, Rms(graph.ReadFrame().Samples.Span));
    }
}
