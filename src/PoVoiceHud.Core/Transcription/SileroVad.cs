using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PoVoiceHud.Core.Transcription;

/// <summary>
/// Streaming Silero VAD (v5/v6 ONNX): 512-sample windows at 16 kHz prefixed with 64 samples of the previous
/// window, recurrent state [2,1,128] carried between calls.
/// </summary>
public sealed class SileroVad : IDisposable
{
    private const int Window = 512;
    private const int Context = 64;

    private readonly InferenceSession _session;
    private readonly float[] _input = new float[Context + Window];
    private readonly float[] _pending = new float[Window];
    private readonly DenseTensor<long> _sampleRate;
    private DenseTensor<float> _state = new([2, 1, 128]);
    private int _pendingCount;

    public SileroVad(string modelPath)
    {
        _session = new InferenceSession(modelPath, new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 });
        // Older exports declare sr as int64[1], newer ones as a scalar.
        var srDims = _session.InputMetadata["sr"].Dimensions;
        _sampleRate = new DenseTensor<long>(new long[] { Audio.PcmFrame.SampleRate }, srDims.Length == 0 ? [] : [1]);
    }

    /// <summary>Highest speech probability among the 512-sample windows completed by these samples.</summary>
    public float Process(ReadOnlySpan<short> samples)
    {
        var max = 0f;
        foreach (var sample in samples)
        {
            _pending[_pendingCount++] = sample / 32768f;
            if (_pendingCount < Window) continue;
            max = Math.Max(max, RunWindow());
            _pendingCount = 0;
        }
        return max;
    }

    public void Reset()
    {
        _state = new DenseTensor<float>([2, 1, 128]);
        Array.Clear(_input);
        _pendingCount = 0;
    }

    private float RunWindow()
    {
        _pending.CopyTo(_input, Context);
        using var results = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(_input, [1, Context + Window])),
            NamedOnnxValue.CreateFromTensor("state", _state),
            NamedOnnxValue.CreateFromTensor("sr", _sampleRate),
        ]);
        var probability = results.First(r => r.Name == "output").AsTensor<float>().GetValue(0);
        _state = new DenseTensor<float>(results.First(r => r.Name == "stateN").AsTensor<float>().ToArray(), [2, 1, 128]);
        // Tail of this window becomes the context for the next one.
        Array.Copy(_input, Window, _input, 0, Context);
        return probability;
    }

    public void Dispose() => _session.Dispose();
}
