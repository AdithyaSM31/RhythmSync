using RhythmSync.App.Services;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Client;
using RhythmSync.Platform.Windows;

namespace RhythmSync.App.Tests;

public class HostTabViewModelTests
{
    private sealed class FakeRuntime : IHostRuntime
    {
        public bool IsHosting { get; set; }
        public HostViewModel? Current { get; set; }
        public string? LastError { get; set; }
        public string? LocalSpeakerId { get; set; } = "b";
        public Exception? StartThrows;
        public readonly List<string> Started = new();
        public readonly List<string> Chosen = new();
        public int Stops;
        public TaskCompletionSource? Gate;
        public event Action? Changed;

        public IReadOnlyList<AudioDeviceInfo> Speakers() => new[] { new AudioDeviceInfo("a", "Speakers"), new AudioDeviceInfo("b", "Headphones") };

        public async Task StartAsync(string sessionName)
        {
            if (Gate is not null) await Gate.Task;
            Started.Add(sessionName);
            if (StartThrows is not null) throw StartThrows;
            IsHosting = true;
            Changed?.Invoke();
        }

        public Task StopAsync()
        {
            Stops++;
            IsHosting = false;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public void SetLocalSpeaker(string deviceId) => Chosen.Add(deviceId);

        public void RaiseChanged() => Changed?.Invoke();
    }

    private static HostTabViewModel Make(FakeRuntime runtime, string name = "My PC") => new(runtime, name);

    [Fact]
    public void Starts_not_hosting_with_the_speakers_listed_and_the_current_one_selected()
    {
        var vm = Make(new FakeRuntime());

        Assert.False(vm.IsHosting);
        Assert.True(vm.IsNotHosting);
        Assert.Null(vm.Current);
        Assert.Equal(2, vm.Speakers.Count);
        Assert.Equal("b", vm.SelectedSpeaker?.Id);
    }

    [Fact]
    public void Start_needs_a_name()
    {
        var vm = Make(new FakeRuntime(), "");
        Assert.False(vm.StartCommand.CanExecute(null));

        vm.SessionName = "   ";
        Assert.False(vm.StartCommand.CanExecute(null));

        vm.SessionName = "Den";
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_passes_the_trimmed_name_and_shows_the_running_host()
    {
        var runtime = new FakeRuntime();
        var vm = Make(runtime);
        vm.SessionName = "  Den  ";
        runtime.Current = new HostViewModel(new NoHost(), () => HostAudioStatus.Ok, _ => null, (_, _) => { }, "1234", "Den");

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Den" }, runtime.Started);
        Assert.True(vm.IsHosting);
        Assert.False(vm.IsNotHosting);
        Assert.Same(runtime.Current, vm.Current);
        Assert.Null(vm.ErrorText);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_start_failure_shows_its_message_and_stays_stopped()
    {
        var runtime = new FakeRuntime { StartThrows = new HostStartException("Can't start hosting – port 47801 is already in use. Is RhythmSync already hosting?") };
        var vm = Make(runtime);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsHosting);
        Assert.Equal("Can't start hosting – port 47801 is already in use. Is RhythmSync already hosting?", vm.ErrorText);
        Assert.True(vm.HasError);
        Assert.True(vm.StartCommand.CanExecute(null));   // can try again
    }

    [Fact]
    public async Task An_unexpected_start_failure_is_shown_not_thrown()
    {
        var runtime = new FakeRuntime { StartThrows = new InvalidOperationException("boom") };
        var vm = Make(runtime);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("Can't start hosting: boom", vm.ErrorText);
    }

    [Fact]
    public async Task The_error_is_cleared_by_the_next_start()
    {
        var runtime = new FakeRuntime { StartThrows = new HostStartException("nope") };
        var vm = Make(runtime);
        await vm.StartCommand.ExecuteAsync(null);

        runtime.StartThrows = null;
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task Start_is_disabled_while_starting()
    {
        var runtime = new FakeRuntime { Gate = new TaskCompletionSource() };
        var vm = Make(runtime);

        var starting = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.StartCommand.CanExecute(null));

        runtime.Gate.SetResult();
        await starting;
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Stop_stops_the_runtime()
    {
        var runtime = new FakeRuntime();
        var vm = Make(runtime);
        await vm.StartCommand.ExecuteAsync(null);

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Equal(1, runtime.Stops);
        Assert.False(vm.IsHosting);
        Assert.Null(vm.Current);
    }

    [Fact]
    public void A_host_that_ended_by_itself_shows_why()
    {
        var runtime = new FakeRuntime { IsHosting = true };
        var vm = Make(runtime);
        Assert.True(vm.IsHosting);

        runtime.IsHosting = false;
        runtime.LastError = "Hosting stopped: no device";
        runtime.RaiseChanged();

        Assert.False(vm.IsHosting);
        Assert.Equal("Hosting stopped: no device", vm.ErrorText);
    }

    [Fact]
    public void Choosing_speakers_tells_the_runtime_but_listing_them_does_not()
    {
        var runtime = new FakeRuntime();
        var vm = Make(runtime);
        Assert.Empty(runtime.Chosen);   // the initial selection is not a choice

        vm.SelectedSpeaker = vm.Speakers[0];

        Assert.Equal(new[] { "a" }, runtime.Chosen);

        vm.ReloadSpeakers();
        Assert.Equal(new[] { "a" }, runtime.Chosen);   // reloading re-selects "b" silently
    }

    [Fact]
    public void The_client_tab_is_disabled_exactly_while_hosting()
    {
        var runtime = new FakeRuntime();
        var host = Make(runtime);
        var main = new MainViewModel(new ClientViewModel(() => Array.Empty<RhythmSync.Core.Net.DiscoveredHost>(),
            (_, _) => Task.FromResult(new JoinResult(JoinOutcome.Joined))), host: host);
        var changed = new List<string?>();
        main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(main.ClientEnabled);

        runtime.IsHosting = true;
        runtime.RaiseChanged();
        Assert.False(main.ClientEnabled);
        Assert.Contains(nameof(MainViewModel.ClientEnabled), changed);

        runtime.IsHosting = false;
        runtime.RaiseChanged();
        Assert.True(main.ClientEnabled);
    }

    [Fact]
    public void The_client_tab_is_enabled_when_there_is_no_host_tab()
    {
        var main = new MainViewModel(new ClientViewModel(() => Array.Empty<RhythmSync.Core.Net.DiscoveredHost>(),
            (_, _) => Task.FromResult(new JoinResult(JoinOutcome.Joined))));
        Assert.True(main.ClientEnabled);
        Assert.Null(main.Host);
    }

    private sealed class NoHost : RhythmSync.Core.Host.IHostControl
    {
        public Task SetLatencyAsync(int ms) => Task.CompletedTask;
        public Task SetDeviceSettingsAsync(Guid deviceId, float volume, double offsetMs) => Task.CompletedTask;
    }
}
