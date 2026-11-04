using PoVoiceHud.Core.Audio;


namespace PoVoiceHud.Core.Tests.Audio;

public class RingBufferTests
{
    [Fact]
    public void Sixty_seconds_at_16kHz_is_960000_samples()
    {
        var ring = new RingBuffer(TimeSpan.FromSeconds(60));
        Assert.Equal(960_000, ring.Capacity);
    }

    [Fact]
    public void Overwrites_oldest_samples_and_counts_total_written()
    {
        var ring = new RingBuffer(capacity: 4);
        ring.Write([1, 2, 3]);
        ring.Write([4, 5]);

        Assert.Equal(5, ring.TotalWritten);
        Assert.Equal(new short[] { 2, 3, 4, 5 }, ring.CopyRange(from: 1, count: 4));
    }

    [Fact]
    public void CopyRange_addresses_by_absolute_sample_position()
    {
        var ring = new RingBuffer(capacity: 8);
        ring.Write(Enumerable.Range(0, 10).Select(i => (short)i).ToArray());

        Assert.Equal(new short[] { 4, 5, 6 }, ring.CopyRange(from: 4, count: 3));
        Assert.Equal(new short[] { 9 }, ring.CopyRange(from: 9, count: 1));
    }

    [Fact]
    public void CopyRange_rejects_evicted_or_future_ranges()
    {
        var ring = new RingBuffer(capacity: 8);
        ring.Write(new short[10]);

        Assert.Throws<ArgumentOutOfRangeException>(() => ring.CopyRange(from: 1, count: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.CopyRange(from: 9, count: 2));
    }

    [Fact]
    public void Write_larger_than_capacity_keeps_the_tail()
    {
        var ring = new RingBuffer(capacity: 3);
        ring.Write([1, 2, 3, 4, 5, 6, 7]);

        Assert.Equal(7, ring.TotalWritten);
        Assert.Equal(new short[] { 5, 6, 7 }, ring.CopyRange(from: 4, count: 3));
    }

    [Fact]
    public void Clear_resets_position()
    {
        var ring = new RingBuffer(capacity: 4);
        ring.Write([1, 2]);
        ring.Clear();

        Assert.Equal(0, ring.TotalWritten);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.CopyRange(from: 0, count: 1));
    }
}
