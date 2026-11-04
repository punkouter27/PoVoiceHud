using PoVoiceHud.Core.Audio;


namespace PoVoiceHud.Core.Tests.Audio;

public class SampleConvertTests
{
    [Theory]
    [InlineData(0.5f, 16383)]
    [InlineData(1.5f, 32767)]
    [InlineData(-2f, -32768)]
    public void Float_to_pcm16_scales_and_clamps(float input, short expected)
    {
        Span<short> dst = stackalloc short[1];
        SampleConvert.ToPcm16([input], dst);
        Assert.Equal(expected, dst[0]);
    }

    [Fact]
    public void Pcm16_to_float_round_trips_within_one_lsb()
    {
        short[] pcm = [-32768, -1234, 0, 1234, 32767];
        var floats = SampleConvert.ToFloat(pcm);
        Span<short> back = stackalloc short[pcm.Length];
        SampleConvert.ToPcm16(floats, back);

        for (var i = 0; i < pcm.Length; i++)
            Assert.InRange(back[i], pcm[i] - 1, pcm[i] + 1);
    }
}
