using System.Net;
using System.Windows;
using RhythmSync.App.Services;
using RhythmSync.App.Shell;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Config;
using RhythmSync.Core.Host;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;
using RhythmSync.Platform.Windows;

namespace RhythmSync.App.Tests;

/// <summary>The shell's view-model side: the start page, the sidebar footer text and the joined host's name.</summary>
public class ShellTests
{
    private static readonly DiscoveredHost LivingRoom = new(1, "Living Room", new IPEndPoint(IPAddress.Parse("192.168.1.24"), 47801), 0);
    private static readonly DiscoveredHost Office = new(2, "Office PC", new IPEndPoint(IPAddress.Parse("192.168.1.31"), 47801), 0);
    private static readonly Guid IdA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid IdB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid IdC = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    internal sealed class FakeControl : IClientSessionControl
    {
        public SyncState State { get; set; } = SyncState.Disconnected;
        public event Action? StateChanged;
        public float Volume => 1f;
        public double OffsetMs => 0;
        public PlaybackStats? Stats => null;
        public long OutputFailures => 0;
        public int Leaves;

        public Task LeaveAsync() { Leaves++; return Task.CompletedTask; }

        public void Become(SyncState state)
        {
            State = state;
            StateChanged?.Invoke();
        }
    }

    internal sealed class FakeHost : IHostControl
    {
        public Task SetLatencyAsync(int ms) => Task.CompletedTask;
        public Task SetDeviceSettingsAsync(Guid deviceId, float volume, double offsetMs) => Task.CompletedTask;
    }

    internal sealed class FakeRuntime : IHostRuntime
    {
        public bool IsHosting { get; set; }
        public HostViewModel? Current { get; set; }
        public string? LastError => null;
        public string? LocalSpeakerId => "a";
        public event Action? Changed;

        public IReadOnlyList<AudioDeviceInfo> Speakers() => new[] { new AudioDeviceInfo("a", "Speakers") };

        public Task StartAsync(string sessionName)
        {
            Host(new HostViewModel(new FakeHost(), () => HostAudioStatus.Ok, _ => null, (_, _) => { }, "4729", sessionName));
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsHosting = false;
            Current = null;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public void SetLocalSpeaker(string deviceId) { }

        public HostViewModel Host(HostViewModel? host = null)
        {
            host ??= new HostViewModel(new FakeHost(), () => HostAudioStatus.Ok, _ => null, (_, _) => { }, "4729", "Living Room");
            IsHosting = true;
            Current = host;
            Changed?.Invoke();
            return host;
        }
    }

    internal static ClientViewModel Client(FakeControl? control = null, Action<Action>? post = null)
        => new(() => new[] { LivingRoom, Office }, (_, _) => Task.FromResult(new JoinResult(JoinOutcome.Joined)),
            control ?? new FakeControl(), post);

    private static ConnectedClient Row(Guid id, SyncState state)
        => new(id, $"PC {id.ToString()[^1]}", new IPEndPoint(IPAddress.Loopback, 5000), true,
            new HeartbeatStats(0, 0, 12, 0.05, 0.3, state), 1f, 0);

    private static async Task Join(ClientViewModel client, DiscoveredHost host)
    {
        client.RefreshHosts();
        client.SelectedHost = host;
        client.Code = "4729";
        await client.JoinCommand.ExecuteAsync(null);
    }

    // ---- start page --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, Page.Host)]
    [InlineData("Living Room", Page.Join)]
    public void Initial_page_follows_last_host(string? last, Page expected)
        => Assert.Equal(expected, MainViewModel.InitialPage(last));

    [Fact]
    public void Selected_page_is_observable()
    {
        var vm = new MainViewModel(Client());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedPage = Page.Settings;

        Assert.Equal(Page.Settings, vm.SelectedPage);
        Assert.Contains(nameof(MainViewModel.SelectedPage), changed);
    }

    // ---- footer ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Footer_copy_is_exact()
    {
        Assert.Equal("Not connected", MainViewModel.FooterNotConnected);
        Assert.Equal("Hosting · waiting for PCs", MainViewModel.FooterHostingWaiting);
    }

    [Fact]
    public void Footer_not_connected_by_default()
    {
        var vm = new MainViewModel(Client(), host: new HostTabViewModel(new FakeRuntime(), "My PC"));
        Assert.Equal("Not connected", vm.FooterText);
    }

    [Fact]
    public void Footer_while_hosting_without_clients()
    {
        var runtime = new FakeRuntime();
        var vm = new MainViewModel(Client(), host: new HostTabViewModel(runtime, "My PC"));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        runtime.Host();

        Assert.Equal("Hosting · waiting for PCs", vm.FooterText);
        Assert.Contains(nameof(MainViewModel.FooterText), changed);

        runtime.StopAsync();
        Assert.Equal("Not connected", vm.FooterText);
    }

    [Fact]
    public void Footer_while_hosting_counts_in_sync_rows()
    {
        var runtime = new FakeRuntime();
        var vm = new MainViewModel(Client(), host: new HostTabViewModel(runtime, "My PC"));
        var host = runtime.Host();

        host.UpdateClients(new[] { Row(IdA, SyncState.InSync), Row(IdB, SyncState.InSync), Row(IdC, SyncState.Syncing) });
        Assert.Equal("Hosting · 2 of 3 in sync", vm.FooterText);

        host.UpdateClients(new[] { Row(IdA, SyncState.InSync), Row(IdB, SyncState.Degraded), Row(IdC, SyncState.Syncing) });
        Assert.Equal("Hosting · 1 of 3 in sync", vm.FooterText);

        host.UpdateClients(new[] { Row(IdA, SyncState.InSync), Row(IdC, SyncState.InSync) });
        Assert.Equal("Hosting · 2 of 2 in sync", vm.FooterText);

        host.UpdateClients(Array.Empty<ConnectedClient>());
        Assert.Equal("Hosting · waiting for PCs", vm.FooterText);
    }

    [Fact]
    public void Footer_counts_rows_of_a_host_that_was_already_running()
    {
        var runtime = new FakeRuntime();
        var host = runtime.Host();
        host.UpdateClients(new[] { Row(IdA, SyncState.InSync), Row(IdB, SyncState.Syncing) });

        var vm = new MainViewModel(Client(), host: new HostTabViewModel(runtime, "My PC"));
        Assert.Equal("Hosting · 1 of 2 in sync", vm.FooterText);

        host.UpdateClients(new[] { Row(IdA, SyncState.InSync), Row(IdB, SyncState.InSync) });
        Assert.Equal("Hosting · 2 of 2 in sync", vm.FooterText);
    }

    [Fact]
    public void Footer_follows_a_restarted_host_and_ignores_the_old_one()
    {
        var runtime = new FakeRuntime();
        var vm = new MainViewModel(Client(), host: new HostTabViewModel(runtime, "My PC"));
        var first = runtime.Host();
        first.UpdateClients(new[] { Row(IdA, SyncState.InSync) });

        var second = runtime.Host();
        Assert.Equal("Hosting · waiting for PCs", vm.FooterText);

        // The old host's rows and collection no longer drive the footer.
        first.UpdateClients(new[] { Row(IdA, SyncState.Degraded), Row(IdB, SyncState.InSync) });
        Assert.Equal("Hosting · waiting for PCs", vm.FooterText);

        second.UpdateClients(new[] { Row(IdC, SyncState.Syncing) });
        Assert.Equal("Hosting · 0 of 1 in sync", vm.FooterText);
    }

    [Fact]
    public async Task Footer_when_joined()
    {
        var control = new FakeControl();
        var client = Client(control);
        var vm = new MainViewModel(client);

        await Join(client, LivingRoom);
        control.Become(SyncState.InSync);

        Assert.Equal("In sync · Living Room", vm.FooterText);

        control.Become(SyncState.Degraded);
        Assert.Equal("Sync is degraded · Living Room", vm.FooterText);

        await client.LeaveCommand.ExecuteAsync(null);
        Assert.Equal(1, control.Leaves);
        Assert.Equal("Not connected", vm.FooterText);
    }

    [Fact]
    public async Task Hosting_wins_over_a_stale_join()
    {
        var control = new FakeControl();
        var client = Client(control);
        var runtime = new FakeRuntime();
        var vm = new MainViewModel(client, host: new HostTabViewModel(runtime, "My PC"));
        await Join(client, LivingRoom);
        control.Become(SyncState.InSync);

        runtime.Host();

        Assert.Equal("Hosting · waiting for PCs", vm.FooterText);
    }

    [Fact]
    public void Footer_updates_go_through_post()
    {
        var queue = new Queue<Action>();
        var runtime = new FakeRuntime();
        var vm = new MainViewModel(Client(), queue.Enqueue, new HostTabViewModel(runtime, "My PC"));

        runtime.Host();
        Assert.Equal("Not connected", vm.FooterText);

        while (queue.Count > 0) queue.Dequeue()();
        Assert.Equal("Hosting · waiting for PCs", vm.FooterText);
    }

    // ---- joined host name --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Joined_host_name_is_set_on_a_successful_join_and_raised()
    {
        var client = Client();
        var changed = new List<string?>();
        client.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Null(client.JoinedHostName);

        await Join(client, Office);

        Assert.Equal("Office PC", client.JoinedHostName);
        Assert.Contains(nameof(ClientViewModel.JoinedHostName), changed);
    }

    [Fact]
    public async Task Joined_host_name_stays_unset_when_the_join_fails()
    {
        var client = new ClientViewModel(() => new[] { LivingRoom },
            (_, _) => Task.FromResult(new JoinResult(JoinOutcome.Rejected, RejectReason.BadCode)), new FakeControl());

        await Join(client, LivingRoom);

        Assert.Null(client.JoinedHostName);
    }

    [Fact]
    public async Task Joined_host_name_clears_when_the_session_drops_to_disconnected()
    {
        var control = new FakeControl();
        var client = Client(control);
        await Join(client, LivingRoom);
        control.Become(SyncState.Syncing);
        Assert.Equal("Living Room", client.JoinedHostName);

        control.Become(SyncState.Disconnected);   // e.g. a reconnect gave up

        Assert.Null(client.JoinedHostName);
    }

    [Fact]
    public async Task Joined_host_name_clears_on_leave()
    {
        var control = new FakeControl();
        var client = Client(control);
        await Join(client, LivingRoom);
        control.Become(SyncState.InSync);

        await client.LeaveCommand.ExecuteAsync(null);

        Assert.Null(client.JoinedHostName);
    }

    // ---- title bar hit testing ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(100, 0, true)]      // top-left corner
    [InlineData(145.9, 31.9, true)] // just inside the bottom-right corner
    [InlineData(146, 10, false)]    // right edge belongs to the next button
    [InlineData(120, 32, false)]    // bottom edge belongs to the client area
    [InlineData(99.9, 10, false)]
    [InlineData(120, -1, false)]
    public void Maximize_hit_test_is_half_open(double x, double y, bool expected)
        => Assert.Equal(expected, WindowChromeHelper.HitTest(new Rect(100, 0, 46, 32), new Point(x, y)));

    [Fact]
    public void Maximize_hit_test_misses_an_empty_rect()
        => Assert.False(WindowChromeHelper.HitTest(Rect.Empty, new Point(0, 0)));

    [Theory]
    [InlineData(0x0020_0010, 16, 32)]
    [InlineData(unchecked((int)0xFFF6_FFEC), -20, -10)]   // a monitor left of and above the primary one
    public void Cursor_position_is_read_as_signed_words(int lParam, double x, double y)
        => Assert.Equal(new Point(x, y), WindowChromeHelper.CursorFromLParam(new IntPtr(lParam)));
}
