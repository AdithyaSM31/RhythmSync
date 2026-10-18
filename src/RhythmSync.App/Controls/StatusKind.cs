namespace RhythmSync.App.Controls;

/// <summary>What a <see cref="StatusChip"/> shows: the dot colour, glow and pulse follow the kind.</summary>
public enum StatusKind
{
    InSync,
    Hosting,
    Syncing,
    Degraded,
    Connecting,
    NotConnected,
}
