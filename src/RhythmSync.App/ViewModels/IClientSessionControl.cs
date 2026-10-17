using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;

namespace RhythmSync.App.ViewModels;

/// <summary>What <see cref="ClientViewModel"/> needs from a client session (lets tests substitute a fake).</summary>
public interface IClientSessionControl
{
    SyncState State { get; }

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    event Action? StateChanged;

    float Volume { get; set; }
    double OffsetMs { get; set; }
    PlaybackStats? Stats { get; }

    /// <summary>Times opening the audio device failed since the session was created.</summary>
    long OutputFailures { get; }

    Task LeaveAsync();
}
