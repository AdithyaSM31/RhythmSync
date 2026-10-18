using System.Net;
using RhythmSync.App.Controls;
using RhythmSync.App.Flyout;
using RhythmSync.App.Services;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Config;
using RhythmSync.Core.Host;
using RhythmSync.Core.Protocol;
using RhythmSync.Platform.Windows;

namespace RhythmSync.App.Tests;

/// <summary>The flyout projects the host tab and the client view model, and reuses their commands.</summary>
public class TrayFlyoutViewModelTests
{
    private static readonly Guid IdA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid IdB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    internal sealed class RecordingHost : IHostControl
    {
        public readonly List<(Guid Id, float Volume, double Offset)> Settings = new();

        public Task SetLatencyAsync(int ms) => Task.CompletedTask;

        public Task SetDeviceSettingsAsync(Guid deviceId, float volume, double offsetMs)
        {
            Settings.Add((deviceId, volume, offsetMs));
            return Task.CompletedTask;
        }
    }

    private sealed class Runtime : IHostRuntime
    {
        public bool IsHosting { get; private set; }
        public HostViewModel? Current { get; private set; }
        public string? LastError => null;
        public string? LocalSpeakerId => null;
        public int Stops;
        public event Action? Changed;

        public IReadOnlyList<AudioDeviceInfo> Speakers() => Array.Empty<AudioDeviceInfo>();
        public Task StartAsync(string sessionName) => Task.CompletedTask;
        public void SetLocalSpeaker(string deviceId) { }

        public Task StopAsync()
        {
            Stops++;
            IsHosting = false;
            Current = null;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public void Host(HostViewModel host)
        {
            IsHosting = true;
            Current = host;
            Changed?.Invoke();
        }
    }

    private sealed class Rig
    {
        public readonly Runtime Runtime = new();
        public readonly RecordingHost HostControl = new();
        public readonly ShellTests.FakeControl Control = new();
        public readonly List<(Guid Id, DeviceSettings Settings)> Saved = new();
        public readonly HostTabViewModel HostTab;
        public readonly ClientViewModel Client;
        public readonly TrayFlyoutViewModel Vm;
        public int Opened;
        public int Closes;

        public Rig()
        {
            HostTab = new HostTabViewModel(Runtime, "My PC");
            Client = ShellTests.Client(Control);
            Vm = new TrayFlyoutViewModel(HostTab, Client, () => Opened++);
            Vm.CloseRequested += () => Closes++;
        }

        public HostViewModel StartHosting(string sessionName = "Living Room", params ConnectedClient[] clients)
        {
            var host = new HostViewModel(HostControl, () => HostAudioStatus.Ok, _ => null,
                (id, s) => Saved.Add((id, s)), "4729", sessionName);
            host.UpdateClients(clients);
            Runtime.Host(host);
            return host;
        }

        public async Task JoinAsync()
        {
            Client.RefreshHosts();
            Client.SelectedHost = Client.Hosts[0];
            Client.Code = "4729";
            await Client.JoinCommand.ExecuteAsync(null);
            Control.Become(SyncState.InSync);
        }
    }

    private static ConnectedClient Pc(Guid id, string name, SyncState state = SyncState.InSync)
        => new(id, name, new IPEndPoint(IPAddress.Loopback, 5000), true, new HeartbeatStats(0, 0, 12, 0.05, 0.21, state), 0.8f, 10);

    [Fact]
    public void Copy_is_exact()
    {
        Assert.Equal("Host this PC's audio, or join a host from the app.", TrayFlyoutViewModel.IdleText);
        Assert.Equal("Waiting for PCs to join…", TrayFlyoutViewModel.WaitingText);
    }

    [Fact]
    public void Idle_mode_when_neither_hosting_nor_joined()
    {
        var rig = new Rig();

        Assert.Equal(FlyoutMode.Idle, rig.Vm.Mode);
        Assert.Equal("RhythmSync", rig.Vm.Title);
        Assert.Null(rig.Vm.PairingCode);
        Assert.Empty(rig.Vm.Clients);
        Assert.Equal((StatusKind.NotConnected, "Not connected"), (rig.Vm.ChipKind, rig.Vm.ChipText));
        Assert.True(rig.Vm.IsIdle);
        Assert.False(rig.Vm.IsHosting);
        Assert.False(rig.Vm.IsJoined);
    }

    [Fact]
    public void Hosting_mode_mirrors_session_code_and_rows()
    {
        var rig = new Rig();
        var changed = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var host = rig.StartHosting("Living Room", Pc(IdA, "Kitchen Laptop"));

        Assert.Equal(FlyoutMode.Hosting, rig.Vm.Mode);
        Assert.Contains(nameof(TrayFlyoutViewModel.Mode), changed);
        Assert.Equal("Living Room", rig.Vm.Title);
        Assert.Equal("4729", rig.Vm.PairingCode);
        Assert.Equal((StatusKind.Hosting, "Hosting"), (rig.Vm.ChipKind, rig.Vm.ChipText));
        Assert.Same(host.Clients[0], Assert.Single(rig.Vm.Clients));

        host.UpdateClients([Pc(IdA, "Kitchen Laptop"), Pc(IdB, "Study PC", SyncState.Degraded)]);
        Assert.Equal(host.Clients, rig.Vm.Clients);
        Assert.Same(host.Clients[1], rig.Vm.Clients[1]);

        host.UpdateClients([Pc(IdB, "Study PC")]);
        Assert.Same(host.Clients[0], Assert.Single(rig.Vm.Clients));

        host.SessionName = "Den";
        Assert.Equal("Den", rig.Vm.Title);
    }

    [Fact]
    public void A_new_host_replaces_the_rows_and_the_old_one_is_no_longer_followed()
    {
        var rig = new Rig();
        var first = rig.StartHosting("Living Room", Pc(IdA, "Kitchen Laptop"));
        rig.HostTab.StopCommand.Execute(null);

        Assert.Equal(FlyoutMode.Idle, rig.Vm.Mode);
        Assert.Empty(rig.Vm.Clients);
        Assert.Null(rig.Vm.PairingCode);

        var second = rig.StartHosting("Den", Pc(IdB, "Study PC"));
        first.UpdateClients([Pc(IdA, "Kitchen Laptop"), Pc(IdB, "Old")]);
        first.SessionName = "Old session";

        Assert.Equal("Den", rig.Vm.Title);
        Assert.Same(second.Clients[0], Assert.Single(rig.Vm.Clients));
    }

    [Fact]
    public void Hosting_mode_with_no_clients_shows_waiting()
    {
        var rig = new Rig();

        var host = rig.StartHosting();

        Assert.Empty(rig.Vm.Clients);
        Assert.True(rig.Vm.ShowWaiting);

        host.UpdateClients([Pc(IdA, "Kitchen Laptop")]);
        Assert.False(rig.Vm.ShowWaiting);
    }

    [Fact]
    public async Task Joined_mode_shows_host_name()
    {
        var rig = new Rig();

        await rig.JoinAsync();

        Assert.Equal(FlyoutMode.Joined, rig.Vm.Mode);
        Assert.Equal("Living Room", rig.Vm.Title);
        Assert.Null(rig.Vm.PairingCode);
        Assert.Equal((StatusKind.InSync, "In sync"), (rig.Vm.ChipKind, rig.Vm.ChipText));

        rig.Control.Become(SyncState.Degraded);
        Assert.Equal((StatusKind.Degraded, "Sync is degraded"), (rig.Vm.ChipKind, rig.Vm.ChipText));

        rig.Control.Become(SyncState.Disconnected);
        Assert.Equal(FlyoutMode.Idle, rig.Vm.Mode);
        Assert.Equal("RhythmSync", rig.Vm.Title);
    }

    [Fact]
    public void Joined_mode_follows_the_session_state_not_the_remembered_host_name()
    {
        var rig = new Rig();
        rig.Client.JoinedHostName = "Living Room";   // a name with no live session is not "joined"

        Assert.Equal(FlyoutMode.Idle, rig.Vm.Mode);
    }

    [Fact]
    public void Hosting_wins_over_a_client_state()
    {
        var rig = new Rig();
        rig.Control.Become(SyncState.Connecting);

        rig.StartHosting();

        Assert.Equal(FlyoutMode.Hosting, rig.Vm.Mode);
    }

    [Fact]
    public void Flyout_volume_change_reaches_the_host_path()
    {
        var rig = new Rig();
        rig.StartHosting("Living Room", Pc(IdA, "Kitchen Laptop"));
        rig.HostControl.Settings.Clear();

        rig.Vm.Clients[0].Volume = 0.5f;

        Assert.Equal((IdA, 0.5f, 10d), Assert.Single(rig.HostControl.Settings));
        Assert.Equal(new DeviceSettings(0.5f, 10), Assert.Single(rig.Saved).Settings);

        rig.Vm.Clients[0].Volume = float.NaN;
        Assert.Single(rig.HostControl.Settings);
        Assert.Equal(0.5f, rig.Vm.Clients[0].Volume);

        rig.Vm.Clients[0].Volume = 3f;
        Assert.Equal(1f, rig.Vm.Clients[0].Volume);
    }

    [Fact]
    public async Task Actions_invoke_commands_and_request_close()
    {
        var rig = new Rig();

        rig.Vm.OpenCommand.Execute(null);
        Assert.Equal(1, rig.Opened);
        Assert.Equal(1, rig.Closes);

        rig.StartHosting();
        Assert.True(rig.Vm.StopHostingCommand.CanExecute(null));
        rig.Vm.StopHostingCommand.Execute(null);
        Assert.Equal(1, rig.Runtime.Stops);
        Assert.Equal(2, rig.Closes);
        Assert.False(rig.Vm.StopHostingCommand.CanExecute(null));

        await rig.JoinAsync();
        Assert.True(rig.Vm.LeaveCommand.CanExecute(null));
        rig.Vm.LeaveCommand.Execute(null);
        Assert.Equal(1, rig.Control.Leaves);
        Assert.Equal(3, rig.Closes);
    }

    [Fact]
    public void Command_availability_follows_the_wrapped_commands()
    {
        var rig = new Rig();
        int stopChanges = 0, leaveChanges = 0;
        rig.Vm.StopHostingCommand.CanExecuteChanged += (_, _) => stopChanges++;
        rig.Vm.LeaveCommand.CanExecuteChanged += (_, _) => leaveChanges++;
        Assert.False(rig.Vm.LeaveCommand.CanExecute(null));

        rig.StartHosting();
        rig.Control.Become(SyncState.Connecting);

        Assert.True(stopChanges > 0);
        Assert.True(leaveChanges > 0);
        Assert.True(rig.Vm.LeaveCommand.CanExecute(null));
    }

    [Fact]
    public void A_failing_open_still_closes_the_flyout()
    {
        var rig = new Rig();
        var vm = new TrayFlyoutViewModel(rig.HostTab, rig.Client, () => throw new InvalidOperationException("no window"));
        int closes = 0;
        vm.CloseRequested += () => closes++;

        vm.OpenCommand.Execute(null);

        Assert.Equal(1, closes);
    }

    [Fact]
    public void Updates_go_through_post()
    {
        var rig = new Rig();
        var queued = new List<Action>();
        var vm = new TrayFlyoutViewModel(rig.HostTab, rig.Client, () => { }, queued.Add);
        queued.ForEach(a => a());
        queued.Clear();

        var host = rig.StartHosting("Living Room", Pc(IdA, "Kitchen Laptop"));

        Assert.Equal(FlyoutMode.Idle, vm.Mode);
        Assert.NotEmpty(queued);
        queued.ToList().ForEach(a => a());
        Assert.Equal(FlyoutMode.Hosting, vm.Mode);
        Assert.Same(host.Clients[0], Assert.Single(vm.Clients));
    }
}
