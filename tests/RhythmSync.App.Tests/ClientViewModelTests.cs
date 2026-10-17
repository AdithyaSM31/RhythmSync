using System.Net;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;

namespace RhythmSync.App.Tests;

public class ClientViewModelTests
{
    private static readonly DiscoveredHost HostA = new(1, "Living room", new IPEndPoint(IPAddress.Parse("192.168.1.10"), 47801), 0);
    private static readonly DiscoveredHost HostB = new(2, "Bedroom", new IPEndPoint(IPAddress.Parse("192.168.1.11"), 47801), 0);

    private sealed class FakeControl : IClientSessionControl
    {
        public SyncState State { get; set; } = SyncState.Disconnected;
        public event Action? StateChanged;
        public float Volume { get; set; } = 1f;
        public double OffsetMs { get; set; }
        public PlaybackStats? Stats { get; set; }
        public long OutputFailures { get; set; }
        public int Leaves;

        public Task LeaveAsync() { Leaves++; return Task.CompletedTask; }
        public void Raise() => StateChanged?.Invoke();
    }

    /// <summary>Records every write so tests can check that only finite, in-range values reach the session.</summary>
    private sealed class RecordingControl : IClientSessionControl
    {
        private float _volume = 1f;
        private double _offset;
        public readonly List<float> VolumeWrites = new();
        public readonly List<double> OffsetWrites = new();

        public SyncState State => SyncState.Disconnected;
        public event Action? StateChanged { add { } remove { } }
        public float Volume { get => _volume; set { VolumeWrites.Add(value); _volume = value; } }
        public double OffsetMs { get => _offset; set { OffsetWrites.Add(value); _offset = value; } }
        public PlaybackStats? Stats => null;
        public long OutputFailures => 0;
        public Task LeaveAsync() => Task.CompletedTask;

        /// <summary>Simulates the host overwriting the settings (no write recorded).</summary>
        public void HostSets(float volume, double offset) { _volume = volume; _offset = offset; }
    }

    private static ClientViewModel Make(Func<IPEndPoint, string, Task<JoinResult>>? join = null,
        IReadOnlyList<DiscoveredHost>? hosts = null, IClientSessionControl? control = null, Action<Action>? post = null)
        => new(() => hosts ?? new[] { HostA, HostB }, join ?? ((_, _) => Task.FromResult(new JoinResult(JoinOutcome.Joined))), control, post);

    // ---- join command ------------------------------------------------------------------------------------------------

    [Fact]
    public void Join_disabled_until_host_and_four_digits()
    {
        var vm = Make();
        vm.RefreshHosts();

        vm.Code = "1234";
        Assert.False(vm.JoinCommand.CanExecute(null));   // no host yet

        vm.SelectedHost = HostA;
        Assert.True(vm.JoinCommand.CanExecute(null));

        foreach (var bad in new[] { "12a4", "123", "12345", "", " 123", "١٢٣٤" })
        {
            vm.Code = bad;
            Assert.False(vm.JoinCommand.CanExecute(null), $"'{bad}'");
        }
        vm.Code = "0007";
        Assert.True(vm.JoinCommand.CanExecute(null));
    }

    [Fact]
    public void Join_command_raises_CanExecuteChanged_when_inputs_change()
    {
        var vm = Make();
        int raised = 0;
        vm.JoinCommand.CanExecuteChanged += (_, _) => raised++;
        vm.RefreshHosts();
        vm.SelectedHost = HostA;
        vm.Code = "1234";
        Assert.True(raised >= 2);
    }

    [Theory]
    [InlineData(RejectReason.BadCode, "Wrong code")]
    [InlineData(RejectReason.LockedOut, "Too many attempts – wait 30 s")]
    [InlineData(RejectReason.VersionMismatch, "Host runs a different RhythmSync version")]
    [InlineData(RejectReason.SessionFull, "Session is full")]
    public void RejectText_uses_exact_copy(RejectReason reason, string expected)
        => Assert.Equal(expected, ClientViewModel.RejectText(reason));

    [Fact]
    public void RejectText_covers_every_reason()
    {
        foreach (var r in Enum.GetValues<RejectReason>())
            Assert.False(string.IsNullOrWhiteSpace(ClientViewModel.RejectText(r)), r.ToString());
    }

    [Fact]
    public async Task Rejected_join_sets_error_text()
    {
        var vm = Make((_, _) => Task.FromResult(new JoinResult(JoinOutcome.Rejected, RejectReason.BadCode)));
        vm.RefreshHosts();
        vm.SelectedHost = HostA;
        vm.Code = "1234";

        await vm.JoinCommand.ExecuteAsync(null);

        Assert.Equal("Wrong code", vm.ErrorText);
    }

    [Fact]
    public async Task Unreachable_sets_error_text()
    {
        var vm = Make((_, _) => Task.FromResult(new JoinResult(JoinOutcome.Unreachable)));
        vm.RefreshHosts();
        vm.SelectedHost = HostA;
        vm.Code = "1234";

        await vm.JoinCommand.ExecuteAsync(null);

        Assert.Equal("Can't reach host", vm.ErrorText);
    }

    [Fact]
    public async Task Join_passes_endpoint_and_code_and_success_clears_error_and_raises_Joined()
    {
        IPEndPoint? ep = null; string? code = null; int calls = 0;
        var reject = true;
        var vm = Make((e, c) =>
        {
            calls++; ep = e; code = c;
            return Task.FromResult(reject ? new JoinResult(JoinOutcome.Rejected, RejectReason.BadCode) : new JoinResult(JoinOutcome.Joined));
        });
        (DiscoveredHost, string)? joined = null;
        vm.Joined += (h, c) => joined = (h, c);
        vm.RefreshHosts();
        vm.SelectedHost = HostB;
        vm.Code = "4321";

        await vm.JoinCommand.ExecuteAsync(null);
        Assert.Equal("Wrong code", vm.ErrorText);
        Assert.Null(joined);

        reject = false;
        await vm.JoinCommand.ExecuteAsync(null);

        Assert.Equal(2, calls);
        Assert.Equal(HostB.ControlEndPoint, ep);
        Assert.Equal("4321", code);
        Assert.Null(vm.ErrorText);
        Assert.Equal((HostB, "4321"), joined);
    }

    [Fact]
    public async Task Rejected_without_reason_still_shows_an_error()
    {
        var vm = Make((_, _) => Task.FromResult(new JoinResult(JoinOutcome.Rejected)));
        vm.RefreshHosts();
        vm.SelectedHost = HostA;
        vm.Code = "1234";
        await vm.JoinCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrEmpty(vm.ErrorText));
    }

    [Fact]
    public async Task Join_that_throws_shows_cant_reach_host_and_does_not_propagate()
    {
        var vm = Make((_, _) => throw new InvalidOperationException("boom"));
        vm.RefreshHosts();
        vm.SelectedHost = HostA;
        vm.Code = "1234";

        await vm.JoinCommand.ExecuteAsync(null);

        Assert.Equal("Can't reach host", vm.ErrorText);
        Assert.True(vm.JoinCommand.CanExecute(null));
    }

    [Fact]
    public async Task Join_is_disabled_while_a_join_is_in_flight()
    {
        var gate = new TaskCompletionSource<JoinResult>();
        var vm = Make((_, _) => gate.Task);
        vm.RefreshHosts();
        vm.SelectedHost = HostA;
        vm.Code = "1234";

        var running = vm.JoinCommand.ExecuteAsync(null);
        Assert.False(vm.JoinCommand.CanExecute(null));
        Assert.True(vm.IsJoining);

        gate.SetResult(new JoinResult(JoinOutcome.Joined));
        await running;
        Assert.True(vm.JoinCommand.CanExecute(null));
        Assert.False(vm.IsJoining);
    }

    // ---- hosts -------------------------------------------------------------------------------------------------------

    [Fact]
    public void RefreshHosts_fills_list_and_keeps_selection_while_host_is_listed()
    {
        var current = new List<DiscoveredHost> { HostA, HostB };
        var vm = new ClientViewModel(() => current.ToArray(), (_, _) => Task.FromResult(new JoinResult(JoinOutcome.Joined)));
        vm.RefreshHosts();
        Assert.Equal(new[] { HostA, HostB }, vm.Hosts);

        vm.SelectedHost = HostB;
        current[1] = HostB with { LastSeenTicks = 99 };   // same host, new instance
        vm.RefreshHosts();
        Assert.Equal(2, vm.Hosts.Count);
        Assert.Equal(2u, vm.SelectedHost?.SessionId);

        current.RemoveAt(1);
        vm.RefreshHosts();
        Assert.Single(vm.Hosts);
        Assert.Null(vm.SelectedHost);
    }

    [Fact]
    public void RefreshHosts_does_not_throw_when_the_source_throws()
    {
        var vm = new ClientViewModel(() => throw new InvalidOperationException("x"), (_, _) => Task.FromResult(new JoinResult(JoinOutcome.Joined)));
        vm.RefreshHosts();
        Assert.Empty(vm.Hosts);
    }

    [Fact]
    public void RefreshHosts_is_marshalled_through_post()
    {
        var queue = new Queue<Action>();
        var vm = Make(post: queue.Enqueue);

        vm.RefreshHosts();
        Assert.Empty(vm.Hosts);   // not applied until the dispatcher runs it
        Assert.Single(queue);

        queue.Dequeue()();
        Assert.Equal(2, vm.Hosts.Count);
    }

    [Fact]
    public void A_failing_post_does_not_propagate_to_the_network_thread()
    {
        var vm = Make(post: _ => throw new InvalidOperationException("dispatcher gone"));
        vm.RefreshHosts();   // must not throw
    }

    [Fact]
    public void Host_list_message_reflects_listener_availability()
    {
        var vm = Make(hosts: Array.Empty<DiscoveredHost>());
        vm.RefreshHosts();
        Assert.False(string.IsNullOrEmpty(vm.HostListMessage));   // empty-state hint

        vm.HostsUnavailable = true;
        Assert.Equal("Can't listen for hosts on this PC – is RhythmSync already running?", vm.HostListMessage);

        var withHosts = Make();
        withHosts.RefreshHosts();
        Assert.Null(withHosts.HostListMessage);
    }

    // ---- state and status --------------------------------------------------------------------------------------------

    [Fact]
    public void State_changes_are_marshalled_and_update_status_text()
    {
        var control = new FakeControl();
        var queue = new Queue<Action>();
        var vm = Make(control: control, post: queue.Enqueue);

        control.State = SyncState.InSync;
        control.Raise();
        Assert.Equal(SyncState.Disconnected, vm.State);   // still waiting for the dispatcher
        queue.Dequeue()();

        Assert.Equal(SyncState.InSync, vm.State);
        Assert.Equal("In sync", vm.StatusText);
        Assert.True(vm.IsConnected);
    }

    [Fact]
    public void A_failing_post_for_a_state_change_does_not_propagate()
    {
        var control = new FakeControl();
        _ = Make(control: control, post: _ => throw new InvalidOperationException("dispatcher gone"));
        control.Raise();   // must not throw into the session's thread
    }

    [Theory]
    [InlineData(SyncState.Connecting, "Connecting…")]
    [InlineData(SyncState.Syncing, "Syncing…")]
    [InlineData(SyncState.InSync, "In sync")]
    [InlineData(SyncState.Degraded, "Sync is degraded")]
    [InlineData(SyncState.Disconnected, "Not connected")]
    public void Status_text_per_state(SyncState state, string text)
    {
        var vm = Make();
        vm.State = state;
        Assert.Equal(text, vm.StatusText);
    }

    [Fact]
    public void Output_failures_while_syncing_show_the_audio_device_message()
    {
        var control = new FakeControl { State = SyncState.Syncing };
        var vm = Make(control: control);
        vm.Refresh();
        Assert.Null(vm.StatusMessage);

        control.OutputFailures = 2;
        vm.Refresh();
        Assert.Equal("Can't open the audio device – check your sound settings.", vm.StatusMessage);

        // Once playback works the message goes away, and the old failure count does not come back on a later reconnect.
        control.State = SyncState.InSync;
        vm.Refresh();
        Assert.Null(vm.StatusMessage);

        control.State = SyncState.Syncing;
        vm.Refresh();
        Assert.Null(vm.StatusMessage);

        control.OutputFailures = 3;
        vm.Refresh();
        Assert.Equal("Can't open the audio device – check your sound settings.", vm.StatusMessage);
    }

    [Fact]
    public void Output_failures_in_other_states_do_not_show_a_message()
    {
        var control = new FakeControl { State = SyncState.Disconnected, OutputFailures = 5 };
        var vm = Make(control: control);
        vm.Refresh();
        Assert.Null(vm.StatusMessage);
    }

    // ---- stats -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Unknown_stats_show_a_dash_never_NaN()
    {
        var control = new FakeControl { State = SyncState.Syncing, Stats = new PlaybackStats(double.NaN, double.NaN, 0, 0, 0) };
        var vm = Make(control: control);
        vm.Refresh();

        Assert.Contains("—", vm.SyncErrorText);
        Assert.Contains("—", vm.DriftText);
        Assert.DoesNotContain("NaN", vm.SyncErrorText + vm.DriftText + vm.CountersText);

        control.Stats = null;
        vm.Refresh();
        Assert.Contains("—", vm.SyncErrorText);
        Assert.Contains("—", vm.DriftText);
    }

    [Fact]
    public void Known_stats_are_formatted()
    {
        var control = new FakeControl { State = SyncState.InSync, Stats = new PlaybackStats(0.25, -12.4, 3, 7, 11) };
        var vm = Make(control: control);
        vm.Refresh();

        Assert.Equal("Sync error: 0.25 ms", vm.SyncErrorText);
        Assert.Equal("Drift correction: -12 ppm", vm.DriftText);
        Assert.Equal("Resyncs: 3 · Concealed frames: 7 · Late frames: 11", vm.CountersText);
    }

    // ---- volume and offset -------------------------------------------------------------------------------------------

    [Fact]
    public void Volume_and_offset_are_clamped_and_never_pass_non_finite_values()
    {
        var control = new RecordingControl();
        var vm = Make(control: control);

        vm.Volume = 1.5f; Assert.Equal(1f, vm.Volume);
        vm.Volume = -3f; Assert.Equal(0f, vm.Volume);
        vm.Volume = 0.4f; Assert.Equal(0.4f, vm.Volume);
        vm.Volume = float.NaN; Assert.Equal(0.4f, vm.Volume);
        vm.Volume = float.PositiveInfinity; Assert.Equal(1f, vm.Volume);

        vm.OffsetMs = 250; Assert.Equal(100, vm.OffsetMs);
        vm.OffsetMs = -250; Assert.Equal(-100, vm.OffsetMs);
        vm.OffsetMs = 12.5; Assert.Equal(12.5, vm.OffsetMs);
        vm.OffsetMs = double.NaN; Assert.Equal(12.5, vm.OffsetMs);
        vm.OffsetMs = double.NegativeInfinity; Assert.Equal(-100, vm.OffsetMs);

        Assert.NotEmpty(control.VolumeWrites);
        Assert.NotEmpty(control.OffsetWrites);
        Assert.All(control.VolumeWrites, v => Assert.True(float.IsFinite(v) && v is >= 0f and <= 1f));
        Assert.All(control.OffsetWrites, o => Assert.True(double.IsFinite(o) && o is >= -100 and <= 100));
    }

    [Fact]
    public void Volume_and_offset_are_pushed_to_the_session()
    {
        var control = new RecordingControl();
        var vm = Make(control: control);
        vm.Volume = 0.25f;
        vm.OffsetMs = -40;
        Assert.Equal(0.25f, control.Volume);
        Assert.Equal(-40, control.OffsetMs);
    }

    [Fact]
    public void Refresh_adopts_host_changed_values_without_writing_them_back()
    {
        var control = new RecordingControl();
        var vm = Make(control: control);

        control.HostSets(0.3f, 17);
        vm.Refresh();

        Assert.Equal(0.3f, vm.Volume);
        Assert.Equal(17, vm.OffsetMs);
        Assert.Empty(control.VolumeWrites);
        Assert.Empty(control.OffsetWrites);
    }

    [Fact]
    public void Volume_and_offset_text()
    {
        var vm = Make();
        vm.Volume = 0.8f;
        vm.OffsetMs = 12.4;
        Assert.Equal("Volume: 80%", vm.VolumeText);
        Assert.Equal("Offset: +12 ms", vm.OffsetText);
        vm.OffsetMs = -5;
        Assert.Equal("Offset: -5 ms", vm.OffsetText);
        vm.OffsetMs = 0;
        Assert.Equal("Offset: 0 ms", vm.OffsetText);
    }

    // ---- leave -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Leave_calls_the_session_and_is_only_enabled_while_connected()
    {
        var control = new FakeControl();
        var vm = Make(control: control);
        Assert.False(vm.LeaveCommand.CanExecute(null));

        vm.State = SyncState.InSync;
        Assert.True(vm.LeaveCommand.CanExecute(null));

        await vm.LeaveCommand.ExecuteAsync(null);
        Assert.Equal(1, control.Leaves);
    }
}
