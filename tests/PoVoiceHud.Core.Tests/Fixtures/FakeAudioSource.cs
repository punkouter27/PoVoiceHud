using PoVoiceHud.Core.Audio;


namespace PoVoiceHud.Core.Tests.Fixtures;

/// <summary>Replays PCM as 100 ms frames (then silence) as fast as the pipeline will take them.</summary>
public sealed class FakeAudioSource(short[] pcm, TimeSpan trailingSilence, SourceHint source = SourceHint.Teams) : IAudioSource
{
    public event Action<PcmFrame>? FrameAvailable;
    public event Action<string>? Status;

    public bool IsMuted { get; set; }
    public int FramesPushed { get; private set; }

    public void Start()
    {
        Status?.Invoke("Replaying");
        var silence = new short[PcmFrame.SamplesPerFrame];
        var total = pcm.Length / PcmFrame.SamplesPerFrame + (int)(trailingSilence / PcmFrame.Duration);
        for (var i = 0; i < total && !IsMuted; i++)
        {
            var samples = i * PcmFrame.SamplesPerFrame + PcmFrame.SamplesPerFrame <= pcm.Length
                ? pcm.AsMemory(i * PcmFrame.SamplesPerFrame, PcmFrame.SamplesPerFrame)
                : silence;
            FrameAvailable?.Invoke(new PcmFrame(samples, source, PcmFrame.Duration * i));
            FramesPushed++;
        }
    }

    public void Stop() { }

    public void Dispose() { }
}
