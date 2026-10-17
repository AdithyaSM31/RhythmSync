using RhythmSync.Core.Clock;

namespace RhythmSync.Tests.Clock;

public class SyncStateEvaluatorTests
{
    private const long Second = 10_000_000;
    private const long Now = 1_000 * Second;

    private sealed record StubMapping : IClockMapping
    {
        public bool IsConverged { get; init; }
        public double DriftPpm => 0;
        public double ResidualMs => 0;
        public double StdErrorMs { get; init; }
        public long? LastSampleClientTicks { get; init; }
        public bool EverConverged { get; init; }
        public long ClientToHost(long clientTicks) => clientTicks;
    }

    public enum Case { NeverConverged, Converged, StdError1_5Ms, StdErrorExactly1Ms, LastSample11sAgo, LastSampleExactly10sAgo, NoSamplesYet, ConvergedOnceNowNot }

    [Theory]
    [InlineData(Case.NeverConverged, SyncState.Syncing)]
    [InlineData(Case.Converged, SyncState.InSync)]
    [InlineData(Case.StdError1_5Ms, SyncState.Degraded)]
    [InlineData(Case.StdErrorExactly1Ms, SyncState.InSync)]
    [InlineData(Case.LastSample11sAgo, SyncState.Degraded)]
    [InlineData(Case.LastSampleExactly10sAgo, SyncState.InSync)]
    [InlineData(Case.NoSamplesYet, SyncState.InSync)]
    [InlineData(Case.ConvergedOnceNowNot, SyncState.InSync)]
    public void Evaluates_state_from_mapping(Case c, SyncState expected)
    {
        var healthy = new StubMapping { IsConverged = true, EverConverged = true, StdErrorMs = 0.2, LastSampleClientTicks = Now - Second / 4 };
        IClockMapping m = c switch
        {
            Case.NeverConverged => new StubMapping { StdErrorMs = double.PositiveInfinity, LastSampleClientTicks = Now },
            Case.Converged => healthy,
            Case.StdError1_5Ms => healthy with { StdErrorMs = 1.5 },
            Case.StdErrorExactly1Ms => healthy with { StdErrorMs = 1.0 },
            Case.LastSample11sAgo => healthy with { LastSampleClientTicks = Now - 11 * Second },
            Case.LastSampleExactly10sAgo => healthy with { LastSampleClientTicks = Now - 10 * Second },
            Case.NoSamplesYet => healthy with { LastSampleClientTicks = null },
            Case.ConvergedOnceNowNot => healthy with { IsConverged = false, StdErrorMs = 0.8 },
            _ => throw new ArgumentOutOfRangeException(nameof(c)),
        };

        Assert.Equal(expected, SyncStateEvaluator.Evaluate(m, Now));
    }

    [Fact]
    public void Identity_mapping_is_in_sync()
    {
        Assert.Equal(SyncState.InSync, SyncStateEvaluator.Evaluate(new IdentityClockMapping(), Now));
    }
}
