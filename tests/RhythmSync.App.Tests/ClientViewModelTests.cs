using RhythmSync.Core.Audio.Eq;
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
        public EqGains Eq { get; set; } = EqGains.Flat;
        public PlaybackStats? Stats { get; set; }
        public long OutputFailures { get; set; }
        public int Leaves;

        public Task LeaveAsync() { Leaves++; return Task.CompletedTask; }
        public void Raise() => StateChanged?.Invoke();
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

    // ---- volume and offset (set on the host, shown read-only) ---------------------------------------------------------

    [Fact]
    public void Volume_and_offset_cannot_be_written_from_the_client_tab()
    {
        Assert.False(typeof(ClientViewModel).GetProperty(nameof(ClientViewModel.Volume))!.SetMethod?.IsPublic ?? false);
        Assert.False(typeof(ClientViewModel).GetProperty(nameof(ClientViewModel.OffsetMs))!.SetMethod?.IsPublic ?? false);
        Assert.Null(typeof(IClientSessionControl).GetProperty(nameof(IClientSessionControl.Volume))!.SetMethod);
        Assert.Null(typeof(IClientSessionControl).GetProperty(nameof(IClientSessionControl.OffsetMs))!.SetMethod);
        Assert.Equal("Set on the host", ClientViewModel.SetOnHostText);
    }

    [Fact]
    public void Refresh_shows_the_values_the_host_set()
    {
        var control = new FakeControl();
        var vm = Make(control: control);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        control.Volume = 0.3f;
        control.OffsetMs = 17;
        vm.Refresh();

        Assert.Equal(0.3f, vm.Volume);
        Assert.Equal(17, vm.OffsetMs);
        Assert.Equal("Volume: 30%", vm.VolumeText);
        Assert.Equal("Offset: +17 ms", vm.OffsetText);
        Assert.Contains(nameof(ClientViewModel.VolumeText), changed);
        Assert.Contains(nameof(ClientViewModel.OffsetText), changed);
    }

    [Fact]
    public void Shown_values_are_clamped_and_never_non_finite()
    {
        var control = new FakeControl { Volume = 0.4f, OffsetMs = 12.5 };
        var vm = Make(control: control);
        Assert.Equal(0.4f, vm.Volume);
        Assert.Equal(12.5, vm.OffsetMs);

        control.Volume = float.NaN;
        control.OffsetMs = double.NaN;
        vm.Refresh();
        Assert.Equal(0.4f, vm.Volume);
        Assert.Equal(12.5, vm.OffsetMs);

        control.Volume = 1.5f;
        control.OffsetMs = -250;
        vm.Refresh();
        Assert.Equal(1f, vm.Volume);
        Assert.Equal(-100, vm.OffsetMs);
    }

    [Fact]
    public void Volume_and_offset_text()
    {
        var control = new FakeControl { Volume = 0.8f, OffsetMs = 12.4 };
        var vm = Make(control: control);
        Assert.Equal("Volume: 80%", vm.VolumeText);
        Assert.Equal("Offset: +12 ms", vm.OffsetText);
        control.OffsetMs = -5;
        vm.Refresh();
        Assert.Equal("Offset: -5 ms", vm.OffsetText);
        control.OffsetMs = 0;
        vm.Refresh();
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
