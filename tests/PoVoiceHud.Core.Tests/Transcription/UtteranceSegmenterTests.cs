using PoVoiceHud.Core.Audio;
using PoVoiceHud.Core.Transcription;




namespace PoVoiceHud.Core.Tests.Transcription;

public class UtteranceSegmenterTests
{
    private const int N = PcmFrame.SamplesPerFrame;
    private readonly List<Utterance> _out = [];
    private readonly UtteranceSegmenter _seg;
    private int _frame;

    public UtteranceSegmenterTests()
    {
        _seg = new UtteranceSegmenter();
        _seg.UtteranceReady += _out.Add;
    }

    private void Push(int frames, float probability, SourceHint source = SourceHint.Teams)
    {
        for (var i = 0; i < frames; i++, _frame++)
        {
            var samples = new short[N];
            Array.Fill(samples, (short)(_frame + 1));
            _seg.Push(new PcmFrame(samples, source, PcmFrame.Duration * _frame), probability);
        }
    }

    [Fact]
    public void Seven_silent_frames_close_an_utterance()
    {
        Push(10, 0.9f); Push(7, 0.1f);
        Push(10, 0.9f); Push(7, 0.1f);

        Assert.Equal(2, _out.Count);
    }

    [Fact]
    public void Short_pause_keeps_one_utterance()
    {
        Push(10, 0.9f); Push(3, 0.1f); Push(10, 0.9f); Push(7, 0.1f);

        var u = Assert.Single(_out);
        Assert.Equal(23 * N, u.Samples.Length);
    }

    [Fact]
    public void Includes_300ms_pre_roll_and_no_trailing_silence()
    {
        Push(5, 0.1f); Push(10, 0.9f); Push(7, 0.1f);

        var u = Assert.Single(_out);
        Assert.Equal(TimeSpan.FromMilliseconds(200), u.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), u.End);
        Assert.Equal(13 * N, u.Samples.Length);
        Assert.Equal(3, u.Samples[0]);          // first pre-roll frame (frame index 2)
        Assert.Equal(15, u.Samples[^1]);        // last speech frame (frame index 14)
    }

    [Fact]
    public void Hard_cuts_at_30_seconds()
    {
        Push(350, 0.9f); Push(7, 0.1f);

        Assert.Equal(2, _out.Count);
        Assert.Equal(300 * N, _out[0].Samples.Length);
        Assert.Equal(TimeSpan.Zero, _out[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(30), _out[1].Start + TimeSpan.FromMilliseconds(300));
        Assert.Equal(53 * N, _out[1].Samples.Length);
    }

    [Fact]
    public void Blips_under_300ms_are_dropped()
    {
        Push(2, 0.9f); Push(7, 0.1f);

        Assert.Empty(_out);
    }

    [Fact]
    public void Flush_drops_the_utterance_in_progress()
    {
        Push(10, 0.9f);
        _seg.Flush();
        Push(7, 0.1f);

        Assert.Empty(_out);
    }

    [Fact]
    public void Source_is_the_majority_hint_over_speech_frames()
    {
        Push(4, 0.9f, SourceHint.Mic); Push(6, 0.9f, SourceHint.Teams); Push(7, 0.1f, SourceHint.Mic);

        Assert.Equal(SourceHint.Teams, Assert.Single(_out).Source);
    }
}
