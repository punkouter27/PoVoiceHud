using System.Numerics;

namespace PoVoiceHud.Core.Voices;

/// <summary>
/// Kaldi's log mel filterbank, as WeSpeaker computes it for its models (torchaudio.compliance.kaldi.fbank): 25 ms frames
/// every 10 ms, DC removed, pre-emphasis 0.97, a Hamming window, a 512-point power spectrum and 80 triangular mel bins from
/// 20 Hz to 8 kHz, logged; then each bin's mean over the utterance subtracted. Samples are on the int16 scale, unnormalised.
/// </summary>
public static class Fbank
{
    public const int Bins = 80;
    private const int FrameLength = 400;   // 25 ms at 16 kHz
    private const int FrameShift = 160;    // 10 ms
    private const int FftSize = 512;
    private const float Preemphasis = 0.97f;

    private static readonly float[] Window = [.. Enumerable.Range(0, FrameLength).Select(i => (float)(0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (FrameLength - 1))))];
    private static readonly (int First, float[] Weights)[] MelBanks = BuildMelBanks();

    /// <summary>The frames of <paramref name="pcm"/> (16 kHz mono), [frames × 80] row by row; empty when it is under one frame.</summary>
    public static float[] Compute(ReadOnlySpan<short> pcm, out int frames)
    {
        frames = pcm.Length < FrameLength ? 0 : 1 + (pcm.Length - FrameLength) / FrameShift;
        var features = new float[frames * Bins];
        var frame = new float[FrameLength];
        var spectrum = new Complex[FftSize];
        var power = new float[FftSize / 2 + 1];
        for (var f = 0; f < frames; f++)
        {
            var start = f * FrameShift;
            var mean = 0f;
            for (var i = 0; i < FrameLength; i++) mean += frame[i] = pcm[start + i];
            mean /= FrameLength;
            for (var i = 0; i < FrameLength; i++) frame[i] -= mean;
            for (var i = FrameLength - 1; i > 0; i--) frame[i] -= Preemphasis * frame[i - 1];
            frame[0] -= Preemphasis * frame[0];
            for (var i = 0; i < FftSize; i++) spectrum[i] = i < FrameLength ? new Complex(frame[i] * Window[i], 0) : Complex.Zero;
            Fft(spectrum);
            for (var i = 0; i < power.Length; i++) power[i] = (float)(spectrum[i].Real * spectrum[i].Real + spectrum[i].Imaginary * spectrum[i].Imaginary);
            for (var m = 0; m < Bins; m++)
            {
                var (first, weights) = MelBanks[m];
                var energy = 0f;
                for (var k = 0; k < weights.Length; k++) energy += weights[k] * power[first + k];
                features[f * Bins + m] = MathF.Log(Math.Max(energy, 1.1920929e-07f));   // Kaldi's floor: FLT_EPSILON
            }
        }

        // Cepstral mean normalisation: the channel (a headset, a codec) is a constant offset in the log domain, so it drops out.
        for (var m = 0; m < Bins && frames > 0; m++)
        {
            var sum = 0f;
            for (var f = 0; f < frames; f++) sum += features[f * Bins + m];
            var mean = sum / frames;
            for (var f = 0; f < frames; f++) features[f * Bins + m] -= mean;
        }
        return features;
    }

    private static double Mel(double hz) => 1127.0 * Math.Log(1 + hz / 700.0);

    /// <summary>Each bin's triangle over the FFT bins, stored from its first non-zero bin.</summary>
    private static (int, float[])[] BuildMelBanks()
    {
        const double sampleRate = 16_000, low = 20, high = sampleRate / 2;
        var width = sampleRate / FftSize;
        double melLow = Mel(low), delta = (Mel(high) - melLow) / (Bins + 1);
        var banks = new (int, float[])[Bins];
        for (var m = 0; m < Bins; m++)
        {
            double left = melLow + m * delta, center = left + delta, right = center + delta;
            var weights = new float[FftSize / 2];
            int first = -1, last = -1;
            for (var i = 0; i < FftSize / 2; i++)
            {
                var mel = Mel(i * width);
                if (mel <= left || mel >= right) continue;
                weights[i] = (float)(mel <= center ? (mel - left) / (center - left) : (right - mel) / (right - center));
                if (first < 0) first = i;
                last = i;
            }
            banks[m] = first < 0 ? (0, []) : (first, weights[first..(last + 1)]);
        }
        return banks;
    }

    /// <summary>In-place radix-2 FFT; the length is a power of two.</summary>
    private static void Fft(Complex[] x)
    {
        var n = x.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (x[i], x[j]) = (x[j], x[i]);
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var k = 0; k < len / 2; k++)
                {
                    var (a, b) = (x[i + k], x[i + k + len / 2] * w);
                    x[i + k] = a + b;
                    x[i + k + len / 2] = a - b;
                    w *= step;
                }
            }
        }
    }
}
