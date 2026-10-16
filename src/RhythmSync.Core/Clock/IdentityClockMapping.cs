namespace RhythmSync.Core.Clock;

/// <summary>Mapping for the host itself: client and host clocks are the same.</summary>
public sealed class IdentityClockMapping : IClockMapping
{
    public bool IsConverged => true;
    public double DriftPpm => 0;
    public double ResidualMs => 0;
    public double StdErrorMs => 0;
    public long? LastSampleClientTicks => null;
    public bool EverConverged => true;
    public long ClientToHost(long clientTicks) => clientTicks;
}
