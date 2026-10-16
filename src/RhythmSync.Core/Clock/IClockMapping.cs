namespace RhythmSync.Core.Clock;

/// <summary>Maps the local (client) session clock onto the host session clock.</summary>
public interface IClockMapping
{
    bool IsConverged { get; }
    double DriftPpm { get; }
    double ResidualMs { get; }
    long? LastSampleClientTicks { get; }
    bool EverConverged { get; }
    long ClientToHost(long clientTicks);
}
