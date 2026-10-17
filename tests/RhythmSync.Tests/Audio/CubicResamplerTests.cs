using RhythmSync.Core.Audio;

namespace RhythmSync.Tests.Audio;

public class CubicResamplerTests
{
    [Fact]
    public void Unity_step_at_integer_position_is_identity()
    {
        var rng = new Random(1234);
        var input = new float[600 * 2];
        for (int i = 0; i < input.Length; i++) input[i] = (float)(rng.NextDouble() * 2 - 1);
        var output = new float[500 * 2];

        CubicResampler.Process(input, 1.0, 1.0, output);

        for (int k = 0; k < 500; k++)
        {
            Assert.Equal(input[(k + 1) * 2], output[k * 2], 1e-6);
            Assert.Equal(input[(k + 1) * 2 + 1], output[k * 2 + 1], 1e-6);
        }
    }

    [Fact]
    public void Returns_advanced_position()
    {
        var input = new float[1000 * 2];
        var output = new float[480 * 2];

        double next = CubicResampler.Process(input, 1.25, 1.0005, output);

        Assert.Equal(1.25 + 480 * 1.0005, next, 1e-9);
    }

    [Fact]
    public void Throws_when_input_too_short()
    {
        var output = new float[100 * 2];
        int need = CubicResampler.RequiredInputFrames(1.0, 1.0, 100);
        Assert.Equal(1 + 99 + 3, need);

        Assert.Throws<ArgumentException>(() => CubicResampler.Process(new float[(need - 1) * 2], 1.0, 1.0, output));
        CubicResampler.Process(new float[need * 2], 1.0, 1.0, output); // exactly enough is fine
    }

    [Fact]
    public void Throws_on_position_below_one_or_odd_output()
    {
        var input = new float[100 * 2];
        Assert.Throws<ArgumentException>(() => CubicResampler.Process(input, 0.5, 1.0, new float[20]));
        Assert.Throws<ArgumentException>(() => CubicResampler.Process(input, 1.0, 1.0, new float[21]));
    }

    [Theory]
    [InlineData(1.0005)]
    [InlineData(0.9995)]
    public void Tone_quality_at_500ppm(double step)
    {
        const int outFrames = 48_000;
        const double amp = 0.5;
        int inFrames = CubicResampler.RequiredInputFrames(1.0, step, outFrames);
        var input = new float[inFrames * 2];
        for (int n = 0; n < inFrames; n++)
        {
            float v = (float)(amp * Math.Sin(2 * Math.PI * 1000.0 * n / 48000.0));
            input[n * 2] = v;
            input[n * 2 + 1] = v;
        }
        var output = new float[outFrames * 2];

        CubicResampler.Process(input, 1.0, step, output);

        double omega = 2 * Math.PI * 1000.0 * step / 48000.0; // radians per output sample
        for (int ch = 0; ch < 2; ch++)
        {
            double thdn = ThdN(output, ch, omega, skip: 100);
            Assert.True(thdn < -80, $"channel {ch} step {step}: THD+N {thdn:F2} dB");
        }
    }

    // Least-squares fit of a*sin + b*cos + c at known omega; returns 20*log10(rms(residual)/rms(fit)).
    private static double ThdN(float[] interleaved, int ch, double omega, int skip)
    {
        int frames = interleaved.Length / 2;
        double[,] m = new double[3, 4];
        for (int n = skip; n < frames - skip; n++)
        {
            double[] basis = { Math.Sin(omega * n), Math.Cos(omega * n), 1.0 };
            double y = interleaved[n * 2 + ch];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++) m[r, c] += basis[r] * basis[c];
                m[r, 3] += basis[r] * y;
            }
        }
        // Gauss-Jordan elimination with partial pivoting.
        for (int col = 0; col < 3; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 3; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            for (int c = 0; c < 4; c++) (m[col, c], m[piv, c]) = (m[piv, c], m[col, c]);
            for (int r = 0; r < 3; r++)
            {
                if (r == col) continue;
                double f = m[r, col] / m[col, col];
                for (int c = col; c < 4; c++) m[r, c] -= f * m[col, c];
            }
        }
        double a = m[0, 3] / m[0, 0], b = m[1, 3] / m[1, 1], dc = m[2, 3] / m[2, 2];

        double fitPower = 0, resPower = 0;
        int count = 0;
        for (int n = skip; n < frames - skip; n++)
        {
            double fit = a * Math.Sin(omega * n) + b * Math.Cos(omega * n) + dc;
            double res = interleaved[n * 2 + ch] - fit;
            fitPower += fit * fit;
            resPower += res * res;
            count++;
        }
        return 20 * Math.Log10(Math.Sqrt(resPower / count) / Math.Sqrt(fitPower / count));
    }
}
