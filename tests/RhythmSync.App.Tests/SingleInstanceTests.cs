using RhythmSync.App.Services;

namespace RhythmSync.App.Tests;

public class SingleInstanceTests
{
    private sealed class FakeGate(bool first) : ISingleInstanceGate
    {
        public Action? Listener;
        public int Signals, Claims;
        public bool Disposed;

        public bool TryClaim() { Claims++; return first; }
        public void SignalFirstInstance() => Signals++;
        public void ListenForSignals(Action onSignal) => Listener = onSignal;
        public void Dispose() => Disposed = true;
    }

    // ---- coordinator -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_first_instance_runs_and_activates_its_window_when_a_later_launch_signals()
    {
        var gate = new FakeGate(first: true);
        int activations = 0;
        using var coordinator = new SingleInstanceCoordinator(gate);

        Assert.True(coordinator.Claim(() => activations++));

        Assert.Equal(0, gate.Signals);
        Assert.NotNull(gate.Listener);
        gate.Listener!();
        gate.Listener!();
        Assert.Equal(2, activations);
    }

    [Fact]
    public void A_later_instance_signals_the_first_and_is_told_to_exit_without_listening()
    {
        var gate = new FakeGate(first: false);
        int activations = 0;
        var coordinator = new SingleInstanceCoordinator(gate);

        Assert.False(coordinator.Claim(() => activations++));

        Assert.Equal(1, gate.Signals);
        Assert.Null(gate.Listener);
        Assert.Equal(0, activations);
        coordinator.Dispose();
        Assert.True(gate.Disposed);
    }

    [Fact]
    public void A_failing_activation_does_not_escape_into_the_signalling_thread()
    {
        var gate = new FakeGate(first: true);
        using var coordinator = new SingleInstanceCoordinator(gate);
        coordinator.Claim(() => throw new InvalidOperationException("window gone"));

        gate.Listener!();   // must not throw
    }

    [Fact]
    public void Claim_twice_throws()
    {
        using var coordinator = new SingleInstanceCoordinator(new FakeGate(first: true));
        coordinator.Claim(() => { });
        Assert.Throws<InvalidOperationException>(() => coordinator.Claim(() => { }));
    }

    // ---- the named kernel objects (no window involved) ---------------------------------------------------------------

    [Fact]
    public async Task Named_gate_lets_one_instance_claim_and_relays_a_later_launch_to_it()
    {
        string name = "RhythmSync.Test." + Guid.NewGuid().ToString("N");
        using var first = new NamedSingleInstanceGate(name);
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(first.TryClaim());
        first.ListenForSignals(() => signalled.TrySetResult());

        using (var second = new NamedSingleInstanceGate(name))
        {
            Assert.False(second.TryClaim());
            second.SignalFirstInstance();
        }
        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        first.Dispose();
        using var afterExit = new NamedSingleInstanceGate(name);
        Assert.True(afterExit.TryClaim());   // the name is free once the first instance is gone
    }

    [Fact]
    public async Task A_signal_sent_before_the_first_instance_listens_is_not_lost()
    {
        string name = "RhythmSync.Test." + Guid.NewGuid().ToString("N");
        using var first = new NamedSingleInstanceGate(name);
        Assert.True(first.TryClaim());

        using (var second = new NamedSingleInstanceGate(name))
        {
            Assert.False(second.TryClaim());
            second.SignalFirstInstance();
        }

        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ListenForSignals(() => signalled.TrySetResult());
        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
