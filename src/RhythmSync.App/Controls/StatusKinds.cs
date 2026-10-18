using RhythmSync.Core.Clock;

namespace RhythmSync.App.Controls;

/// <summary>Maps the app's sync state to the chip kind it is shown with.</summary>
public static class StatusKinds
{
    /// <summary>Hosting wins over any client state; otherwise each <see cref="SyncState"/> has its own kind, and no state at all means not connected.</summary>
    public static StatusKind From(SyncState? state, bool hosting)
    {
        if (hosting)
            return StatusKind.Hosting;

        return state switch
        {
            SyncState.InSync => StatusKind.InSync,
            SyncState.Syncing => StatusKind.Syncing,
            SyncState.Degraded => StatusKind.Degraded,
            SyncState.Connecting => StatusKind.Connecting,
            _ => StatusKind.NotConnected,
        };
    }
}
