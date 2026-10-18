using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;

namespace RhythmSync.App.ViewModels;

/// <summary>What <see cref="ClientViewModel"/> needs from a client session (lets tests substitute a fake).</summary>
public interface IClientSessionControl
{
    SyncState State { get; }

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    event Action? StateChanged;

    /// <summary>This PC's volume as the host set it (the host is the single source of truth; the client cannot change it).</summary>
    float Volume { get; }

    /// <summary>This PC's offset in ms as the host set it.</summary>
    double OffsetMs { get; }

    PlaybackStats? Stats { get; }

    /// <summary>Times opening the audio device failed since the session was created.</summary>
    long OutputFailures { get; }

    Task LeaveAsync();
}
