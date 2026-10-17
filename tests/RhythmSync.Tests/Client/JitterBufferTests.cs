using RhythmSync.Core.Client;

namespace RhythmSync.Tests.Client;

public class JitterBufferTests
{
    private static short[] Frame(uint seq)
    {
        var f = new short[480];
        Array.Fill(f, (short)(seq * 100));
        return f;
    }

    private static long PlayAt(uint seq) => 1_000_000 + seq * 50_000L;

    private static bool Put(JitterBuffer b, uint seq) => b.Insert(seq, PlayAt(seq), Frame(seq));

    private static JitterBuffer Filled(uint count)
    {
        var b = new JitterBuffer();
        for (uint s = 0; s < count; s++) Assert.True(Put(b, s));
        return b;
    }

    private static void AssertReadable(JitterBuffer b, uint seq)
    {
        var dst = new float[480];
        Assert.True(b.TryReadFrame(seq, dst), $"seq {seq} should be readable");
        float expected = (short)(seq * 100) / 32768f;
        Assert.All(dst, v => Assert.Equal(expected, v));
    }

    [Fact]
    public void In_order_insert_and_read()
    {
        var b = Filled(10);
        AssertReadable(b, 5);
        Assert.Equal(10, b.Received);
    }

    [Fact]
    public void Out_of_order_frames_all_readable()
    {
        var b = new JitterBuffer();
        Assert.True(Put(b, 3));
        Assert.True(Put(b, 1));
        Assert.True(Put(b, 2));
        AssertReadable(b, 1);
        AssertReadable(b, 2);
        AssertReadable(b, 3);
        Assert.Equal(3, b.Received);
    }

    [Fact]
    public void Duplicate_is_counted_and_refused()
    {
        var b = new JitterBuffer();
        Assert.True(Put(b, 4));
        Assert.False(Put(b, 4));
        Assert.Equal(1, b.Duplicates);
        Assert.Equal(1, b.Received);
        Assert.Equal(0, b.Late);
        Assert.Equal(0, b.Rejected);
    }

    [Fact]
    public void Late_frame_is_refused()
    {
        var b = Filled(12);
        b.MarkConsumedThrough(10);
        Assert.False(Put(b, 8));
        Assert.Equal(1, b.Late);
        Assert.False(b.TryReadFrame(8, new float[480]));
        Assert.False(Put(b, 10));
        Assert.Equal(2, b.Late);
        Assert.Equal(0, b.Duplicates);
    }

    [Fact]
    public void Far_future_seq_is_rejected_without_flushing()
    {
        var b = Filled(10);
        Assert.False(Put(b, 100_000));
        Assert.Equal(1, b.Rejected);
        for (uint s = 0; s < 10; s++) AssertReadable(b, s);
    }

    [Fact]
    public void Far_past_seq_below_oldest_is_rejected_without_flushing()
    {
        var b = new JitterBuffer(256);
        Assert.True(Put(b, 1000));
        Assert.True(Put(b, 1010));
        Assert.False(Put(b, 1010 - 256));   // would span 257 frames
        Assert.Equal(1, b.Rejected);
        AssertReadable(b, 1000);
        AssertReadable(b, 1010);
        Assert.True(Put(b, 1010 - 255));    // exactly fits
    }

    [Fact]
    public void Seq_at_anchor_plus_capacity_is_rejected_and_below_is_accepted()
    {
        var b = new JitterBuffer(16);
        Assert.True(Put(b, 100));
        Assert.False(Put(b, 116));
        Assert.Equal(1, b.Rejected);
        Assert.True(Put(b, 115));
    }

    [Fact]
    public void Consuming_frees_slots_for_reuse()
    {
        var b = new JitterBuffer(16);
        for (uint s = 0; s < 16; s++) Assert.True(Put(b, s));
        Assert.False(Put(b, 16));
        b.MarkConsumedThrough(7);
        Assert.True(Put(b, 16));
        Assert.True(Put(b, 23));
        Assert.False(Put(b, 24));
        Assert.False(b.TryReadFrame(3, new float[480]));
        AssertReadable(b, 8);
        AssertReadable(b, 16);
    }

    [Fact]
    public void Missing_frame_read_returns_false()
    {
        var b = new JitterBuffer();
        Assert.False(b.TryReadFrame(0, new float[480]));
        Assert.True(Put(b, 0));
        Assert.True(Put(b, 2));
        Assert.False(b.TryReadFrame(1, new float[480]));
        Assert.False(b.TryReadFrame(256 + 2, new float[480]));  // same slot, different seq
    }

    [Fact]
    public void Wrong_buffer_sizes_throw()
    {
        var b = new JitterBuffer();
        Assert.Throws<ArgumentException>(() => b.Insert(0, 0, new short[479]));
        Assert.Throws<ArgumentException>(() => b.TryReadFrame(0, new float[481]));
    }

    [Fact]
    public void Maps_host_time_inside_frames()
    {
        var b = Filled(10);
        Assert.True(b.TryMapHostTimeToPosition(PlayAt(4) + 25_000, out double pos));
        Assert.Equal(1080.0, pos, 1e-6);
    }

    [Fact]
    public void Maps_host_time_across_a_gap_by_interpolation()
    {
        var b = new JitterBuffer();
        Put(b, 2);
        Put(b, 6);
        Assert.True(b.TryMapHostTimeToPosition(PlayAt(4), out double pos));
        Assert.Equal(4 * 240.0, pos, 1e-6);
    }

    [Fact]
    public void Extrapolates_past_newest_and_before_oldest()
    {
        var b = Filled(10);
        Assert.True(b.TryMapHostTimeToPosition(PlayAt(9) + 100_000, out double after));
        Assert.Equal(9 * 240 + 480.0, after, 1e-6);
        Assert.True(b.TryMapHostTimeToPosition(PlayAt(0) - 50_000, out double before));
        Assert.Equal(-240.0, before, 1e-6);
    }

    [Fact]
    public void Maps_using_consumed_reference()
    {
        var b = Filled(10);
        b.MarkConsumedThrough(9);
        Assert.True(b.TryMapHostTimeToPosition(PlayAt(9) + 50_000, out double pos));
        Assert.Equal(2400.0, pos, 1e-6);
    }

    [Fact]
    public void Mapping_fails_only_when_no_reference_ever_seen()
    {
        var b = new JitterBuffer();
        Assert.False(b.TryMapHostTimeToPosition(123, out _));
        Put(b, 0);
        Assert.True(b.TryMapHostTimeToPosition(123, out _));
    }

    [Fact]
    public void Maps_between_consumed_reference_and_next_present_frame()
    {
        var b = Filled(6);
        b.MarkConsumedThrough(3);
        Assert.True(b.TryMapHostTimeToPosition(PlayAt(3) + 25_000, out double pos));
        Assert.Equal(3 * 240 + 120.0, pos, 1e-6);
    }

    [Fact]
    public void Mapping_uses_exact_long_arithmetic_for_large_seq()
    {
        var b = new JitterBuffer();
        uint seq = 100_000_000;
        long playAt = 5_000_000_000_000_000L;
        b.Insert(seq, playAt, Frame(1));
        Assert.True(b.TryMapHostTimeToPosition(playAt + 25_000, out double pos));
        Assert.Equal((long)seq * 240 + 120.0, pos, 1e-3);
    }

    [Fact]
    public void Contains_reports_present_frames_only()
    {
        var b = new JitterBuffer(16);
        Assert.False(b.Contains(0));
        Assert.True(Put(b, 3));
        Assert.True(b.Contains(3));
        Assert.False(b.Contains(2));
        Assert.False(b.Contains(3 + 16));   // same slot, different seq
        b.MarkConsumedThrough(3);
        Assert.False(b.Contains(3));
    }

    [Fact]
    public void PlayAt_jump_is_a_discontinuity_played_nominally_then_held()
    {
        const long jump = 1_000_000;
        var b = new JitterBuffer();
        for (uint s = 0; s < 100; s++) Assert.True(Put(b, s));
        for (uint s = 100; s <= 120; s++) Assert.True(b.Insert(s, PlayAt(s) + jump, Frame(s)));

        Assert.True(b.TryMapHostTimeToPosition(PlayAt(99) + 25_000, out double inFrame));
        Assert.Equal(99 * 240 + 120.0, inFrame, 1e-6);
        Assert.True(b.TryMapHostTimeToPosition(PlayAt(99) + 500_000, out double held));
        Assert.Equal(100 * 240.0, held, 1e-6);

        double previous = double.NegativeInfinity;
        for (long t = PlayAt(98); t <= PlayAt(102) + jump; t += 10_000)
        {
            Assert.True(b.TryMapHostTimeToPosition(t, out double pos));
            Assert.True(pos >= previous, $"mapping decreased at t={t}: {previous} -> {pos}");
            previous = pos;
        }
        Assert.True(b.TryMapHostTimeToPosition(PlayAt(101) + jump, out double after));
        Assert.Equal(101 * 240.0, after, 1e-6);
    }

    [Fact]
    public async Task Concurrent_insert_and_read_does_not_corrupt()
    {
        var b = new JitterBuffer(64);
        var cts = new CancellationTokenSource();
        int bad = 0;
        var reader = Task.Run(() =>
        {
            var dst = new float[480];
            while (!cts.IsCancellationRequested)
                for (uint s = 0; s < 2000; s++)
                    if (b.TryReadFrame(s, dst) && dst[0] != dst[479]) Interlocked.Increment(ref bad);
        });
        for (uint s = 0; s < 2000; s++)
        {
            Assert.True(Put(b, s));
            if (s > 40) b.MarkConsumedThrough(s - 40);
        }
        cts.Cancel();
        await reader;
        Assert.Equal(0, bad);
    }
}
