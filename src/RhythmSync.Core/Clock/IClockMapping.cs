namespace RhythmSync.Core.Clock;

/// <summary>Maps the local (client) session clock onto the host session clock.</summary>
public interface IClockMapping
{
    bool IsConverged { get; }
    double DriftPpm { get; }
    double ResidualMs { get; }

    /// <summary>
    /// Weighted standard error, in milliseconds, of the fitted host time at the latest sample
    /// (the quantity compared against the convergence threshold). <see cref="double.PositiveInfinity"/>
    /// until a fit with enough degrees of freedom exists; 0 for an identity mapping.
    /// </summary>
    double StdErrorMs { get; }
    long? LastSampleClientTicks { get; }
    bool EverConverged { get; }
    long ClientToHost(long clientTicks);
}
