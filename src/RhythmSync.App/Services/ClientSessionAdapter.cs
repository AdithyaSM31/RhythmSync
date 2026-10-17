using System.Net;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;

namespace RhythmSync.App.Services;

/// <summary>Exposes a <see cref="ClientSession"/> to the view model and joins hosts with it.</summary>
public sealed class ClientSessionAdapter(ClientSession session) : IClientSessionControl
{
    public SyncState State => session.State;

    public event Action? StateChanged
    {
        add => session.StateChanged += value;
        remove => session.StateChanged -= value;
    }

    public float Volume { get => session.Volume; set => session.Volume = value; }
    public double OffsetMs { get => session.OffsetMs; set => session.OffsetMs = value; }
    public PlaybackStats? Stats => session.Stats;
    public long OutputFailures => session.OutputFailures;

    public Task LeaveAsync() => session.LeaveAsync();

    public Task<JoinResult> JoinAsync(IPEndPoint control, string code) => session.JoinAsync(control, code, CancellationToken.None);
}
