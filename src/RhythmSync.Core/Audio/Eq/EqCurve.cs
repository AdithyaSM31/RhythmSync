namespace RhythmSync.Core.Audio.Eq;

/// <summary>Biquad coefficients already divided by a0, for <c>y = b0 x + b1 x1 + b2 x2 - a1 y1 - a2 y2</c>.</summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2);

/// <summary>Pure maths for the 10-band EQ: peaking-filter coefficients, the magnitude response and the preamp that keeps the curve at or below 0 dB.</summary>
public static class EqCurve
{
    private const double ActiveFraction = 0.45;
    private const double SweepFromHz = 20.0;
    private const double SweepToHz = 20000.0;
    private const int SweepPoints = 601;

    /// <summary>True when the band's centre is below 0.45 x <paramref name="sampleRate"/>; other bands are treated as 0 dB.</summary>
    public static bool IsActive(int band, double sampleRate) => EqBands.CentersHz[band] < ActiveFraction * sampleRate;

    /// <summary>RBJ audio-EQ-cookbook peaking filter, normalised by a0.</summary>
    public static Biquad PeakingCoefficients(double f0Hz, double q, double gainDb, double sampleRate)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * f0Hz / sampleRate;
        double cosW0 = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * q);
        double a0 = 1 + alpha / a;
        return new Biquad(
            (1 + alpha * a) / a0,
            -2 * cosW0 / a0,
            (1 - alpha * a) / a0,
            -2 * cosW0 / a0,
            (1 - alpha / a) / a0);
    }

    /// <summary>Magnitude of the whole cascade at <paramref name="hz"/>, in dB. Inactive and 0 dB bands contribute nothing.</summary>
    public static double ResponseDb(EqGains gains, double sampleRate, double hz)
    {
        double w = 2 * Math.PI * hz / sampleRate;
        double cos1 = Math.Cos(w), sin1 = Math.Sin(w);
        double cos2 = Math.Cos(2 * w), sin2 = Math.Sin(2 * w);
        double total = 0;
        for (int band = 0; band < EqBands.Count; band++)
        {
            if (gains[band] == 0f || !IsActive(band, sampleRate)) continue;
            var c = PeakingCoefficients(EqBands.CentersHz[band], EqBands.Q, gains[band], sampleRate);
            // H(e^jw) = (b0 + b1 e^-jw + b2 e^-2jw) / (1 + a1 e^-jw + a2 e^-2jw)
            double nRe = c.B0 + c.B1 * cos1 + c.B2 * cos2, nIm = -(c.B1 * sin1 + c.B2 * sin2);
            double dRe = 1 + c.A1 * cos1 + c.A2 * cos2, dIm = -(c.A1 * sin1 + c.A2 * sin2);
            total += 10 * Math.Log10((nRe * nRe + nIm * nIm) / (dRe * dRe + dIm * dIm));
        }
        return total;
    }

    /// <summary>
    /// Highest point of the curve: 601 log-spaced frequencies from 20 Hz to min(20 kHz, 0.45 x rate), plus every
    /// active band centre. Each local maximum on that grid is then refined between its neighbours, so a peak that
    /// falls between two grid points is not under-reported (the grid alone can miss it by a few 0.0001 dB).
    /// </summary>
    public static double PeakDb(EqGains gains, double sampleRate)
    {
        double toHz = Math.Min(SweepToHz, ActiveFraction * sampleRate);
        double logSpan = Math.Log(toHz / SweepFromHz);
        double peak = double.NegativeInfinity;
        var grid = new double[SweepPoints];
        for (int i = 0; i < SweepPoints; i++)
        {
            grid[i] = ResponseDb(gains, sampleRate, GridHz(i));
            peak = Math.Max(peak, grid[i]);
        }
        for (int i = 1; i < SweepPoints - 1; i++)
        {
            if (grid[i] > grid[i - 1] && grid[i] >= grid[i + 1])
                peak = Math.Max(peak, RefinedMaximum(gains, sampleRate, GridHz(i - 1), GridHz(i + 1)));
        }
        for (int band = 0; band < EqBands.Count; band++)
        {
            if (IsActive(band, sampleRate))
                peak = Math.Max(peak, ResponseDb(gains, sampleRate, EqBands.CentersHz[band]));
        }
        return peak;

        double GridHz(int i) => SweepFromHz * Math.Exp(logSpan * i / (SweepPoints - 1));
    }

    // Golden-section search for the maximum of the response between two frequencies. Every value it returns is a
    // real evaluation of the curve, so it never reports more than the true peak.
    private static double RefinedMaximum(EqGains gains, double sampleRate, double loHz, double hiHz)
    {
        const double invPhi = 0.6180339887498949;
        double lo = Math.Log(loHz), hi = Math.Log(hiHz);
        double c = hi - invPhi * (hi - lo), d = lo + invPhi * (hi - lo);
        double fc = ResponseDb(gains, sampleRate, Math.Exp(c)), fd = ResponseDb(gains, sampleRate, Math.Exp(d));
        double best = Math.Max(fc, fd);
        for (int i = 0; i < 40; i++)
        {
            if (fc > fd)
            {
                hi = d; d = c; fd = fc;
                c = hi - invPhi * (hi - lo);
                fc = ResponseDb(gains, sampleRate, Math.Exp(c));
            }
            else
            {
                lo = c; c = d; fc = fd;
                d = lo + invPhi * (hi - lo);
                fd = ResponseDb(gains, sampleRate, Math.Exp(d));
            }
            best = Math.Max(best, Math.Max(fc, fd));
        }
        return best;
    }

    /// <summary>Gain, at most 0 dB, that keeps the curve from boosting past 0 dB. Deterministic for equal inputs.</summary>
    public static double PreampDb(EqGains gains, double sampleRate)
    {
        double peak = PeakDb(gains, sampleRate);
        return peak > 0 ? -peak : 0.0;
    }
}
