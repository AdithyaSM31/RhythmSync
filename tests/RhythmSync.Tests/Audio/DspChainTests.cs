using RhythmSync.Core;
using RhythmSync.Core.Audio;

namespace RhythmSync.Tests.Audio;

public class DspChainTests
{
    [Fact]
    public void Gain_ramps_then_holds()
    {
        var gain = new GainProcessor(1f);
        var warmup = new float[AudioFormat.FrameFloats];
        Array.Fill(warmup, 1f);
        gain.Process(warmup, AudioFormat.Channels);

        gain.Gain = 0.5f;
        var buf = new float[AudioFormat.FrameFloats];
        Array.Fill(buf, 1f);
        gain.Process(buf, AudioFormat.Channels);

        Assert.Equal(1.0f, buf[0], 0.01f);
        Assert.Equal(0.5f, buf[AudioFormat.FrameFloats - 2]);
        Assert.Equal(0.5f, buf[AudioFormat.FrameFloats - 1]);

        var next = new float[AudioFormat.FrameFloats];
        Array.Fill(next, 1f);
        gain.Process(next, AudioFormat.Channels);
        Assert.All(next, s => Assert.Equal(0.5f, s));
    }

    [Fact]
    public void Chain_applies_in_order()
    {
        var chain = new DspChain();
        chain.Add(new GainProcessor(0.5f));
        chain.Add(new GainProcessor(0.5f));
        var buf = new float[AudioFormat.FrameFloats];
        Array.Fill(buf, 1f);

        chain.Process(buf, AudioFormat.Channels);

        Assert.All(buf, s => Assert.Equal(0.25f, s, 1e-6f));
    }

    [Fact]
    public void Chain_runs_processors_in_insertion_order()
    {
        var log = new List<int>();
        var chain = new DspChain();
        chain.Add(new Recorder(1, log));
        chain.Add(new Recorder(2, log));
        chain.Add(new Recorder(3, log));

        chain.Process(new float[8], 2);

        Assert.Equal(new[] { 1, 2, 3 }, log);
    }

    private sealed class Recorder(int id, List<int> log) : IAudioProcessor
    {
        public void Process(Span<float> interleaved, int channels) => log.Add(id);
    }
}
