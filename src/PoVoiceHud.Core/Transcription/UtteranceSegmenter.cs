using PoVoiceHud.Core.Audio;



namespace PoVoiceHud.Core.Transcription;

public sealed record Utterance(short[] Samples, SourceHint Source, TimeSpan Start, TimeSpan End);

/// <summary>
/// Groups frames into utterances: speech ends after ≥650 ms of silence (7 frames), is hard-cut at 30 s
/// (Whisper's window), starts 300 ms early (pre-roll) and is dropped when shorter than 300 ms.
/// </summary>
public sealed class UtteranceSegmenter
{
    private const float SpeechThreshold = 0.5f;
    private const int SilenceFrames = 7;
    private const int MinSpeechFrames = 3;
    private const int PreRollSamples = 3 * PcmFrame.SamplesPerFrame;
    private const int MaxSpeechFrames = 300;

    private readonly RingBuffer _ring = new(TimeSpan.FromSeconds(60));
    private long _speechStart = -1;
    private long _speechEnd;
    private TimeSpan _speechStartTime;
    private TimeSpan _speechEndTime;
    private int _speechFrames;
    private int _silentFrames;
    private int _teams;
    private int _mic;

    public event Action<Utterance>? UtteranceReady;

    public void Push(PcmFrame frame, float speechProbability)
    {
        var position = _ring.TotalWritten;
        _ring.Write(frame.Samples.Span);

        if (speechProbability < SpeechThreshold)
        {
            if (_speechStart >= 0 && ++_silentFrames >= SilenceFrames) Emit();
            return;
        }

        if (_speechStart < 0)
        {
            _speechStart = position;
            _speechStartTime = frame.Timestamp;
        }
        _speechFrames++;
        _silentFrames = 0;
        _speechEnd = _ring.TotalWritten;
        _speechEndTime = frame.Timestamp + PcmFrame.Duration;
        if (frame.Source == SourceHint.Teams) _teams++;
        else if (frame.Source == SourceHint.Mic) _mic++;

        if (_speechFrames >= MaxSpeechFrames) Emit();
    }

    /// <summary>Drops the utterance in progress and all buffered audio (mute).</summary>
    public void Flush()
    {
        ResetState();
        _ring.Clear();
    }

    private void Emit()
    {
        if (_speechFrames >= MinSpeechFrames)
        {
            var oldest = Math.Max(0, _ring.TotalWritten - _ring.Capacity);
            var from = Math.Max(_speechStart - PreRollSamples, oldest);
            var preRollFrames = (int)((_speechStart - from) / PcmFrame.SamplesPerFrame);
            var source = _teams > _mic ? SourceHint.Teams : _mic > _teams ? SourceHint.Mic : SourceHint.Unknown;
            UtteranceReady?.Invoke(new Utterance(
                _ring.CopyRange(from, (int)(_speechEnd - from)),
                source,
                _speechStartTime - PcmFrame.Duration * preRollFrames,
                _speechEndTime));
        }
        ResetState();
    }

    private void ResetState()
    {
        _speechStart = -1;
        (_speechFrames, _silentFrames, _teams, _mic) = (0, 0, 0, 0);
    }
}
