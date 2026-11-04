using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PoVoiceHud.Core.Voices;

/// <summary>Which of the call's voices said an utterance: 1, 2, 3… in the order they were first heard, or 0 for can't tell.</summary>
public interface IVoiceTagger
{
    int Identify(short[] pcm, DateTimeOffset at);
}

/// <summary>
/// A voice print of an utterance: WeSpeaker's ResNet34 (run with ONNX Runtime, on the CPU) turns its filterbank into 256
/// numbers, scaled to length 1. Two prints of one voice have a cosine of 0.5–0.9 (lower for short ones) and of two voices about 0.2.
/// </summary>
public sealed class VoicePrinter : IDisposable
{
    /// <summary>Under a second, a print is too unsure to tell voices apart by.</summary>
    public const int MinSamples = Audio.PcmFrame.SampleRate;
    private const int MaxSamples = 10 * Audio.PcmFrame.SampleRate;   // the last 10 s say who it is as well as 30 would, faster

    private readonly InferenceSession _session;
    private readonly Lock _gate = new();

    public VoicePrinter(string modelPath) =>
        _session = new InferenceSession(modelPath, new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 });

    /// <summary>The print of <paramref name="pcm"/> (16 kHz mono), or null when it is under <see cref="MinSamples"/>.</summary>
    public float[]? Print(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length < MinSamples) return null;
        if (pcm.Length > MaxSamples) pcm = pcm[^MaxSamples..];
        var features = Fbank.Compute(pcm, out var frames);
        float[] print;
        lock (_gate)
        {
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor("feats", new DenseTensor<float>(features, [1, frames, Fbank.Bins]))]);
            print = results[0].AsTensor<float>().ToArray();
        }
        return Normalised(print);
    }

    /// <summary>Scaled to length 1, in place.</summary>
    public static float[] Normalised(float[] v)
    {
        var length = MathF.Sqrt(v.Sum(x => x * x));
        if (length > 0) for (var i = 0; i < v.Length; i++) v[i] /= length;
        return v;
    }

    /// <summary>After a print still running: the session must not go while it is in use.</summary>
    public void Dispose()
    {
        lock (_gate) _session.Dispose();
    }
}

/// <summary>
/// Groups voice prints as they arrive into voices: a print joins the voice whose average it is closest to when the cosine is
/// at least <see cref="SameVoice"/>, and otherwise, if it is long enough to be sure, starts a new one. A long quiet spell
/// (another meeting) starts the numbering again.
/// </summary>
public sealed class VoiceClusters
{
    /// <summary>The cosine to a voice's average at or above which a print is that voice.</summary>
    public const float SameVoice = 0.4f;
    public const int MaxVoices = 8;
    private static readonly TimeSpan NewMeeting = TimeSpan.FromMinutes(10);

    private readonly List<(float[] Sum, int Count)> _voices = [];
    private readonly Lock _gate = new();
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    public int Count { get { lock (_gate) return _voices.Count; } }

    /// <param name="sure">Long enough to start a new voice; a short print only joins one it matches.</param>
    public int Assign(float[] print, DateTimeOffset at, bool sure = true)
    {
        lock (_gate)
        {
            if (at - _last > NewMeeting) _voices.Clear();
            _last = at;
            var (best, score) = (-1, float.MinValue);
            for (var v = 0; v < _voices.Count; v++)
            {
                var cosine = Cosine(print, _voices[v].Sum);
                if (cosine > score) (best, score) = (v, cosine);
            }
            if (best >= 0 && (score >= SameVoice || _voices.Count == MaxVoices))
            {
                var (sum, count) = _voices[best];
                for (var i = 0; i < sum.Length; i++) sum[i] += print[i];
                _voices[best] = (sum, count + 1);
                return best + 1;
            }
            if (!sure) return 0;
            _voices.Add(((float[])print.Clone(), 1));
            return _voices.Count;
        }
    }

    /// <summary>The cosine of a print (length 1) and a voice's sum of prints.</summary>
    private static float Cosine(float[] print, float[] sum)
    {
        float dot = 0, norm = 0;
        for (var i = 0; i < print.Length; i++)
        {
            dot += print[i] * sum[i];
            norm += sum[i] * sum[i];
        }
        return norm == 0 ? 0 : dot / MathF.Sqrt(norm);
    }
}

/// <summary>The call's voices told apart: a print of each utterance from the call, grouped into voices.</summary>
public sealed class CallVoices(VoicePrinter printer) : IVoiceTagger, IDisposable
{
    private const int SureSamples = 3 * Audio.PcmFrame.SampleRate / 2;   // 1.5 s of speech before a voice is called new

    private readonly VoiceClusters _clusters = new();

    public int Identify(short[] pcm, DateTimeOffset at) =>
        printer.Print(pcm) is { } print ? _clusters.Assign(print, at, sure: pcm.Length >= SureSamples) : 0;

    public void Dispose() => printer.Dispose();
}
