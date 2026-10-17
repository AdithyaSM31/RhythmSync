using RhythmSync.Core;
using RhythmSync.Core.Audio;

namespace RhythmSync.Tests.Audio;

public class ConcealerTests
{
    private const int FrameFloats = AudioFormat.FrameFloats;
    private const int FrameSamples = AudioFormat.FrameSamples;

    /// <summary>440 Hz sine, amplitude 0.5, 48 kHz, identical on both channels, cut into 5 ms frames.</summary>
    private static float[][] SineFrames(int count)
    {
        var frames = new float[count][];
        for (int f = 0; f < count; f++)
        {
            frames[f] = new float[FrameFloats];
            for (int i = 0; i < FrameSamples; i++)
            {
                long n = (long)f * FrameSamples + i;
                var v = (float)(0.5 * Math.Sin(2 * Math.PI * 440.0 * n / AudioFormat.SampleRate));
                frames[f][i * 2] = v;
                frames[f][i * 2 + 1] = v;
            }
        }
        return frames;
    }

    /// <summary>Feeds every frame through the concealer; frames whose index is in <paramref name="dropped"/> are reported missing.</summary>
    private static float[][] Run(Concealer concealer, float[][] input, Func<int, bool> dropped)
    {
        var output = new float[input.Length][];
        for (int f = 0; f < input.Length; f++)
        {
            var buf = new float[FrameFloats];
            bool present = !dropped(f);
            if (present) input[f].CopyTo(buf, 0);
            concealer.Process(buf, present);
            output[f] = buf;
        }
        return output;
    }

    private static double MaxDelta(float[][] frames)
    {
        double max = 0;
        for (int ch = 0; ch < 2; ch++)
        {
            bool have = false;
            float prev = 0;
            foreach (var frame in frames)
            {
                for (int i = ch; i < frame.Length; i += 2)
                {
                    if (have) max = Math.Max(max, Math.Abs(frame[i] - prev));
                    prev = frame[i];
                    have = true;
                }
            }
        }
        return max;
    }

    private static double Rms(float[] frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += (double)s * s;
        return Math.Sqrt(sum / frame.Length);
    }

    [Fact]
    public void Present_frames_pass_through_unchanged()
    {
        var input = SineFrames(10);
        var output = Run(new Concealer(), input, _ => false);

        for (int f = 0; f < input.Length; f++)
            Assert.Equal(input[f], output[f]);
    }

    [Fact]
    public void Single_loss_has_no_click()
    {
        var concealer = new Concealer();
        var input = SineFrames(20);
        var output = Run(concealer, input, f => f == 5);

        Assert.True(MaxDelta(output) < 0.06, $"MaxDelta was {MaxDelta(output)}");
        Assert.True(Rms(output[5]) > 0.5 * Rms(input[5]));
        Assert.Equal(1, concealer.ConcealedFrames);
    }

    [Fact]
    public void Long_gap_fades_to_silence()
    {
        var concealer = new Concealer();
        var input = SineFrames(30);
        var output = Run(concealer, input, f => f >= 5 && f <= 20);

        for (int f = 15; f <= 20; f++)
            Assert.All(output[f], s => Assert.Equal(0f, s));

        Assert.True(MaxDelta(output) < 0.06, $"MaxDelta was {MaxDelta(output)}");
        Assert.Equal(16, concealer.ConcealedFrames);
        // Resumed signal is audible again by the end of the first good frame.
        Assert.True(Math.Abs(output[21][FrameFloats - 2] - input[21][FrameFloats - 2]) < 1e-6);
    }

    [Fact]
    public void Reset_fades_in()
    {
        var concealer = new Concealer();
        concealer.Reset();
        var frame = new float[FrameFloats];
        Array.Fill(frame, 0.5f);

        concealer.Process(frame, true);

        Assert.Equal(0f, frame[0]);
        Assert.Equal(0f, frame[1]);
        Assert.True(frame[FrameFloats - 2] >= 0.49f);
        for (int i = 2; i < FrameFloats; i++)
            Assert.True(frame[i] >= frame[i - 2], $"decreased at {i}");

        // The frame after the fade-in passes through untouched.
        var next = new float[FrameFloats];
        Array.Fill(next, 0.5f);
        concealer.Process(next, true);
        Assert.All(next, s => Assert.Equal(0.5f, s));
    }

    [Fact]
    public void Missing_frames_before_any_good_frame_are_silent_and_counted()
    {
        var concealer = new Concealer();
        var frame = new float[FrameFloats];
        Array.Fill(frame, 0.7f);

        concealer.Process(frame, false);

        Assert.All(frame, s => Assert.Equal(0f, s));
        Assert.Equal(1, concealer.ConcealedFrames);
    }

    [Fact]
    public void Gap_of_ten_frames_resumes_with_splice_not_fade()
    {
        var concealer = new Concealer();
        var input = SineFrames(20);
        var output = Run(concealer, input, f => f >= 3 && f < 13); // m = 1..10

        Assert.True(MaxDelta(output) < 0.06, $"MaxDelta was {MaxDelta(output)}");
        Assert.Equal(10, concealer.ConcealedFrames);
        // Splice, not full-frame fade: sample 100 of the resumed frame is already untouched.
        Assert.Equal(input[13][100 * 2], output[13][100 * 2]);
        // Last concealed frame ends in silence.
        Assert.True(Math.Abs(output[12][FrameFloats - 2]) < 1e-6);
    }

    [Fact]
    public void Ramp_is_linear_from_first_to_last_sample_frame()
    {
        var buf = new float[FrameFloats];
        Array.Fill(buf, 1f);

        Concealer.Ramp(buf, 0.2f, 0.8f);

        Assert.Equal(0.2f, buf[0], 1e-6);
        Assert.Equal(0.2f, buf[1], 1e-6);
        Assert.Equal(0.8f, buf[FrameFloats - 2], 1e-6);
        Assert.Equal(0.8f, buf[FrameFloats - 1], 1e-6);
        Assert.InRange(buf[FrameSamples], 0.49f, 0.51f);
    }
}
