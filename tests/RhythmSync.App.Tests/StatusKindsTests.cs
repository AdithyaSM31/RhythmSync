using RhythmSync.App.Controls;
using RhythmSync.Core.Clock;

namespace RhythmSync.App.Tests;

public class StatusKindsTests
{
    [Theory]
    [InlineData(SyncState.InSync, false, StatusKind.InSync)]
    [InlineData(SyncState.Syncing, false, StatusKind.Syncing)]
    [InlineData(SyncState.Degraded, false, StatusKind.Degraded)]
    [InlineData(SyncState.Connecting, false, StatusKind.Connecting)]
    [InlineData(SyncState.Disconnected, false, StatusKind.NotConnected)]
    [InlineData(null, false, StatusKind.NotConnected)]
    [InlineData(SyncState.Disconnected, true, StatusKind.Hosting)]
    [InlineData(SyncState.InSync, true, StatusKind.Hosting)]
    [InlineData(null, true, StatusKind.Hosting)]
    public void Maps_state_to_kind(SyncState? state, bool hosting, StatusKind expected)
    {
        Assert.Equal(expected, StatusKinds.From(state, hosting));
    }
}
