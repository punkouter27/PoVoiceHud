using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PoVoiceHud.Core.Audio;

/// <summary>The moments that have a sound.</summary>
public enum Cue { Supported, Contradicted, Unverified, Answer, Muted, Unmuted }

/// <summary>
/// Short FM-synthesized cues on the default output, rendered once and replayed. Every buffer is cut at
/// <see cref="Longest"/>, well under the 300 ms blip the segmenter drops, so a cue that loopback capture
/// hears never comes back as a transcript.
/// </summary>
public static class SoundPalette
{
    public static readonly TimeSpan Longest = TimeSpan.FromMilliseconds(240);

    private const int Rate = 48_000;
    private static readonly int LongestSamples = (int)(Rate * Longest.TotalSeconds);
    private static readonly Dictionary<Cue, float[]> Rendered = Enum.GetValues<Cue>().ToDictionary(c => c, c => Render(Notes(c)));

    /// <summary>One voice: a sine carrier phase-modulated by a sine at <c>Ratio</c> × its pitch, the modulation dying away
    /// like a struck bar. A pitch that differs at the end glides there.</summary>
    private readonly record struct Note(double StartMs, double DurationMs, double Hz, double EndHz, double Ratio, double Index, double Gain);

    private static Note Pluck(double start, double hz, double duration = 150, double gain = 0.2) => new(start, duration, hz, hz, 2, 2.4, gain);

    private static IEnumerable<Note> Notes(Cue cue) => cue switch
    {
        // a rising fifth: right
        Cue.Supported => [Pluck(0, 659.3), Pluck(70, 987.8)],
        // a low minor second with an inharmonic ratio: buzzy, unmistakably wrong, and quiet
        Cue.Contradicted => [new(0, 210, 220, 214, 1.41, 3, 0.16), new(0, 210, 233.1, 226, 1.41, 3, 0.12)],
        // two glassy ticks: not sure
        Cue.Unverified => [new(0, 45, 1760, 1760, 3.5, 4, 0.1), new(65, 45, 1568, 1568, 3.5, 4, 0.08)],
        // one soft bell, a little sparkle on top: the code has an answer
        Cue.Answer => [new(0, 200, 1046.5, 1046.5, 1, 0.8, 0.12), new(30, 170, 1568, 1568, 1, 0.5, 0.05)],
        Cue.Muted => [new(0, 150, 880, 440, 1, 1.2, 0.14)],
        _ => [new(0, 150, 440, 880, 1, 1.2, 0.14)],   // unmuted
    };

    /// <summary>How loud the cues play, 0 (silent) to 1 (as rendered): the Settings slider.</summary>
    public static float Volume { get; set; } = 1;

    /// <summary>Plays <paramref name="cue"/> panned −1 (left) … 1 (right) at <see cref="Volume"/>; returns at once.</summary>
    public static void Play(Cue cue, float pan = 0)
    {
        if (Volume <= 0) return;
        var mono = Rendered[cue];
        // Equal-power pan, so a cue sounds as loud on either side as in the middle.
        var angle = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4;
        var (left, right) = ((float)Math.Cos(angle) * Volume, (float)Math.Sin(angle) * Volume);
        var bytes = new byte[mono.Length * 4];
        for (var i = 0; i < mono.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4), (short)(mono[i] * left * short.MaxValue));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2), (short)(mono[i] * right * short.MaxValue));
        }
        try
        {
            var output = new WasapiOut(AudioClientShareMode.Shared, 50);
            output.PlaybackStopped += (_, _) => output.Dispose();
            output.Init(new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(Rate, 16, 2)));
            output.Play();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // no output device, or it is held exclusively: a missing cue is not worth surfacing
        }
    }

    /// <summary>The cue's samples, for tests and for its length.</summary>
    public static ReadOnlySpan<float> Samples(Cue cue) => Rendered[cue];

    private static float[] Render(IEnumerable<Note> notes)
    {
        var voices = notes.ToList();
        var length = Math.Min(LongestSamples, (int)(voices.Max(n => n.StartMs + n.DurationMs) * Rate / 1000));
        var samples = new float[length];
        foreach (var note in voices)
        {
            var start = (int)(note.StartMs * Rate / 1000);
            var count = Math.Min((int)(note.DurationMs * Rate / 1000), length - start);
            double phase = 0, modPhase = 0;
            for (var i = 0; i < count; i++)
            {
                var t = (double)i / count;
                var hz = note.Hz + (note.EndHz - note.Hz) * t;
                phase += 2 * Math.PI * hz / Rate;
                modPhase += 2 * Math.PI * hz * note.Ratio / Rate;
                // 4 ms attack, exponential decay, and a short release so the cut at the end makes no click
                var attack = Math.Min(1, i / (0.004 * Rate));
                var release = Math.Min(1, (count - i) / (0.008 * Rate));
                var envelope = attack * release * Math.Exp(-4 * t);
                var index = note.Index * Math.Exp(-6 * t);
                samples[start + i] += (float)(Math.Sin(phase + index * Math.Sin(modPhase)) * envelope * note.Gain);
            }
        }
        return samples;
    }
}
