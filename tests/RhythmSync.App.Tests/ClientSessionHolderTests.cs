using System.Net;
using RhythmSync.App.Services;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Net;
using RhythmSync.Core.Audio;

namespace RhythmSync.App.Tests;

public class ClientSessionHolderTests
{
    private sealed class ThrowingChannels : IDatagramChannelFactory
    {
        public IDatagramChannel Create(IPEndPoint bind, bool broadcast = false) => throw new InvalidOperationException("no sockets in this test");
    }

    private sealed class NoOutputs : IAudioOutputFactory
    {
        public IAudioOutput Create() => throw new InvalidOperationException("no audio in this test");
    }

    private int _created;

    private ClientSessionHolder Make() => new(() =>
    {
        _created++;
        return new ClientSession(new ClientSessionOptions(Guid.NewGuid(), "Test"), new StopwatchClock(), new ThrowingChannels(), new NoOutputs());
    });

    [Fact]
    public void No_session_is_created_until_the_first_join_and_the_state_is_disconnected()
    {
        var holder = Make();

        Assert.Equal(SyncState.Disconnected, holder.State);
        Assert.Null(holder.Stats);
        Assert.Equal(0, holder.OutputFailures);
        holder.Tick();   // harmless without a session
        Assert.Equal(0, _created);
    }

    [Fact]
    public void Without_a_session_volume_and_offset_are_the_defaults()
    {
        var holder = Make();

        Assert.Equal(1f, holder.Volume);
        Assert.Equal(0, holder.OffsetMs);
    }

    [Fact]
    public async Task Join_is_refused_after_release_until_allowed()
    {
        var holder = Make();
        await holder.ReleaseAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => holder.JoinAsync(new IPEndPoint(IPAddress.Loopback, 1), "1234"));
        Assert.Equal(0, _created);

        holder.Allow();
        // The join now reaches a real session (which fails on the fake socket factory), proving the block is gone.
        await Assert.ThrowsAnyAsync<Exception>(() => holder.JoinAsync(new IPEndPoint(IPAddress.Loopback, 1), "1234"));
        Assert.Equal(1, _created);
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task Release_disposes_the_session_so_the_next_join_creates_a_fresh_one()
    {
        var holder = Make();
        await Assert.ThrowsAnyAsync<Exception>(() => holder.JoinAsync(new IPEndPoint(IPAddress.Loopback, 1), "1234"));
        Assert.Equal(1, _created);
        int changes = 0;
        holder.StateChanged += () => changes++;

        await holder.ReleaseAsync();
        holder.Allow();
        await Assert.ThrowsAnyAsync<Exception>(() => holder.JoinAsync(new IPEndPoint(IPAddress.Loopback, 1), "1234"));

        Assert.Equal(2, _created);
        Assert.True(changes >= 1);           // listeners learn the session went away
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_twice_and_release_twice_are_safe()
    {
        var holder = Make();
        await holder.ReleaseAsync();
        await holder.ReleaseAsync();
        await holder.DisposeAsync();
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task Join_after_dispose_throws()
    {
        var holder = Make();
        await holder.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => holder.JoinAsync(new IPEndPoint(IPAddress.Loopback, 1), "1234"));
    }
}
