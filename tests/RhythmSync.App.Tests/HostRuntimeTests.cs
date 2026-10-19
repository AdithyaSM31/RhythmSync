using System.Net;
using System.Net.Sockets;
using RhythmSync.App.Services;
using RhythmSync.Core;
using RhythmSync.Core.Audio;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Config;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;
using RhythmSync.Platform.Windows;

namespace RhythmSync.App.Tests;

public sealed class HostRuntimeTests : IAsyncDisposable
{
    private const string SpeakersId = "speakers";
    private const string HeadphonesId = "headphones";
    private const string CableDeviceId = "cable";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // ---- fakes -------------------------------------------------------------------------------------------------------

    private sealed class Log
    {
        private readonly List<string> _items = new();
        public void Add(string item) { lock (_items) _items.Add(item); }
        public string[] Items { get { lock (_items) return _items.ToArray(); } }
        public int Count(string item) => Items.Count(i => i == item);
        public int IndexOf(string item) => Array.IndexOf(Items, item);
    }

    private sealed class FakeCapture(Log log, string name, int rate = 48000) : IAudioCapture
    {
        public CaptureHandler? Handler;
        public bool ThrowOnStop;
        public int SampleRate { get; } = rate;
        public event Action? DeviceInvalidated;
        public void Raise() => DeviceInvalidated?.Invoke();
        public void Start(CaptureHandler h) { Handler = h; log.Add($"{name}.start"); }
        public void Stop() { log.Add($"{name}.stop"); if (ThrowOnStop) throw new InvalidOperationException("stop failed"); }
        public void Dispose() => log.Add($"{name}.dispose");
    }

    private sealed class FakeKeeper(Log log, string name) : ISilenceKeeper
    {
        public bool ThrowOnDispose;
        public event Action? DeviceInvalidated;
        public void Raise() => DeviceInvalidated?.Invoke();
        public void Start() => log.Add($"{name}.start");
        public void Dispose() { log.Add($"{name}.dispose"); if (ThrowOnDispose) throw new InvalidOperationException("dispose failed"); }
    }

    private sealed class FakeOutput(Log log, string name) : IAudioOutput
    {
        public bool ThrowOnStop;
        public IRenderCallback? Callback;
        public int SampleRate => 48000;
        public event Action? DeviceInvalidated;
        public void Raise() => DeviceInvalidated?.Invoke();
        public void Start(IRenderCallback cb) { Callback = cb; log.Add($"{name}.start"); }
        public void Stop() { log.Add($"{name}.stop"); if (ThrowOnStop) throw new InvalidOperationException("stop failed"); }
        public void Dispose() => log.Add($"{name}.dispose");
    }

    private sealed class RecordingChannels : IDatagramChannelFactory
    {
        public readonly List<IDatagramChannel> Created = new();
        public IDatagramChannel Create(IPEndPoint bind, bool broadcast = false)
        {
            var ch = new UdpDatagramChannelFactory().Create(bind, broadcast);
            lock (Created) Created.Add(ch);
            return ch;
        }
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public readonly Log Log = new();
        public readonly RecordingChannels RecordingChannels = new();
        public readonly List<FakeCapture> Captures = new();
        public readonly List<FakeKeeper> Keepers = new();
        public readonly List<FakeOutput> Outputs = new();
        public readonly List<string> OutputDevices = new();
        public readonly List<string?> CaptureDevices = new();   // null: "the default device"
        public readonly List<string?> KeeperDevices = new();
        public List<AudioDeviceInfo> SpeakerList = new() { new(SpeakersId, "Speakers"), new(HeadphonesId, "Headphones"), new(CableDeviceId, "CABLE Input") };
        public string? DefaultId = CableDeviceId;   // the happy path: VB-CABLE is the default device
        public string? CableId = CableDeviceId;
        public bool FailCapture, FailOutput;
        public int CaptureRate = 48000;
        public Action? DefaultChanged;
        public int WatchersDisposed;

        public IDatagramChannelFactory Channels => RecordingChannels;

        public IAudioCapture CreateCapture(string? renderDeviceId)
        {
            if (FailCapture) throw new InvalidOperationException("no device");
            CaptureDevices.Add(renderDeviceId);
            var c = new FakeCapture(Log, $"capture{Captures.Count}", CaptureRate);
            Captures.Add(c);
            return c;
        }

        public ISilenceKeeper CreateSilenceKeeper(string? renderDeviceId)
        {
            KeeperDevices.Add(renderDeviceId);
            var k = new FakeKeeper(Log, $"keeper{Keepers.Count}");
            Keepers.Add(k);
            return k;
        }

        public IAudioOutput CreateLocalOutput(string deviceId)
        {
            if (FailOutput) throw new InvalidOperationException("no output");
            var o = new FakeOutput(Log, $"output{Outputs.Count}");
            Outputs.Add(o);
            OutputDevices.Add(deviceId);
            return o;
        }

        public IReadOnlyList<AudioDeviceInfo> Speakers() => SpeakerList;
        public string? DefaultRenderId() => DefaultId;
        public string? VbCableId() => CableId;

        public IDisposable WatchDefaultRender(Action changed)
        {
            DefaultChanged = changed;
            return new Watch(this);
        }

        private sealed class Watch(FakeEnv env) : IDisposable
        {
            public void Dispose() { env.Log.Add("watcher.dispose"); Interlocked.Increment(ref env.WatchersDisposed); }
        }
    }

    // ---- rig ---------------------------------------------------------------------------------------------------------

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rhythmsync-host-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEnv _env = new();
    private readonly Queue<Action> _ui = new();
    private readonly SettingsPersister _settings;
    private readonly UdpDatagramChannel _beaconListener = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly List<BeaconPacket> _beacons = new();
    private readonly List<IDisposable> _disposables = new();
    private readonly HostRuntime _runtime;
    private int _beforeStart, _afterStop;

    public HostRuntimeTests()
    {
        var store = new SettingsStore(_dir);
        _settings = new SettingsPersister(store, store.Load(), TimeSpan.FromHours(1));
        _beaconListener.Start((data, _) =>
        {
            if (PacketSerializer.TryReadBeacon(data, out var b) && b is not null) lock (_beacons) _beacons.Add(b);
        });
        _runtime = Make();
    }

    private HostRuntime Make(HostRuntimeOptions? options = null, IHostEnvironment? env = null)
        => new(env ?? _env, new StopwatchClock(), _settings, a => { lock (_ui) _ui.Enqueue(a); },
            () => { Interlocked.Increment(ref _beforeStart); return Task.CompletedTask; },
            () => Interlocked.Increment(ref _afterStop),
            options ?? new HostRuntimeOptions(ControlPort: 0, AudioPort: 0, ClockPort: 0, DiscoveryTarget: _beaconListener.LocalEndPoint, RetryDelayTicks: 0));

    public async ValueTask DisposeAsync()
    {
        await _runtime.StopAsync();
        foreach (var d in _disposables) d.Dispose();
        _beaconListener.Dispose();
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Pump()
    {
        while (true)
        {
            Action? next;
            lock (_ui) next = _ui.Count > 0 ? _ui.Dequeue() : null;
            if (next is null) return;
            next();
        }
    }

    private int Queued() { lock (_ui) return _ui.Count; }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 5 s.");
            await Task.Delay(5);
        }
    }

    private async Task<BeaconPacket> NextBeaconAsync()
    {
        _runtime.Tick();
        await WaitUntil(() => { lock (_beacons) return _beacons.Count > 0; });
        lock (_beacons) return _beacons[^1];
    }

    private sealed class TestClient : IDisposable
    {
        private readonly TcpClient _tcp = new();
        private NetworkStream _stream = null!;
        public static async Task<TestClient> ConnectAsync(int port)
        {
            var c = new TestClient();
            await c._tcp.ConnectAsync(IPAddress.Loopback, port).WaitAsync(Timeout);
            c._stream = c._tcp.GetStream();
            return c;
        }

        public Task SendAsync(ControlMessage m) => ControlCodec.WriteAsync(_stream, m, CancellationToken.None).WaitAsync(Timeout);
        public async Task<ControlMessage?> ReadAsync() => await ControlCodec.ReadAsync(_stream, CancellationToken.None).WaitAsync(Timeout);
        public void Dispose() => _tcp.Dispose();
    }

    private async Task<(TestClient Client, Welcome Welcome)> JoinAsync(Guid id, int audioPort = 5000, string name = "Laptop")
    {
        var beacon = await NextBeaconAsync();
        var client = await TestClient.ConnectAsync(beacon.ControlPort);
        _disposables.Add(client);
        await client.SendAsync(new Hello(id, name, _runtime.Current!.PairingCode, RhythmSync.Core.Host.HostSession.ProtocolVersion, audioPort));
        return (client, Assert.IsType<Welcome>(await client.ReadAsync()));
    }

    // ---- starting ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_brings_up_capture_keeper_and_local_playback_and_reports_hosting()
    {
        int changed = 0;
        _runtime.Changed += () => changed++;

        await _runtime.StartAsync("Living room");

        Assert.True(_runtime.IsHosting);
        Assert.NotNull(_runtime.Current);
        Assert.Equal("Living room", _runtime.Current!.SessionName);
        Assert.Matches("^[0-9]{4}$", _runtime.Current.PairingCode);
        Assert.Equal(1, changed);
        Assert.Equal(1, _env.Log.Count("capture0.start"));
        Assert.Equal(1, _env.Log.Count("keeper0.start"));
        Assert.Equal(new[] { SpeakersId }, _env.OutputDevices);
        Assert.Equal(1, _env.Log.Count("output0.start"));
        Assert.Null(_runtime.Current.AudioBanner);
        Assert.Equal(1, _beforeStart);
    }

    [Fact]
    public async Task Start_twice_is_a_no_op()
    {
        await _runtime.StartAsync("A");
        var first = _runtime.Current;

        await _runtime.StartAsync("B");

        Assert.Same(first, _runtime.Current);
        Assert.Single(_env.Captures);
        Assert.Equal(1, _beforeStart);
    }

    [Fact]
    public async Task Welcome_carries_the_ports_the_channels_really_bound()
    {
        await _runtime.StartAsync("Host");

        var (_, welcome) = await JoinAsync(Guid.NewGuid());

        var bound = _env.RecordingChannels.Created.Select(c => c.LocalEndPoint.Port).ToArray();
        Assert.Contains(welcome.AudioPort, bound);
        Assert.Contains(welcome.ClockPort, bound);
        Assert.NotEqual(0, welcome.AudioPort);
        Assert.NotEqual(0, welcome.ClockPort);
        Assert.NotEqual(welcome.AudioPort, welcome.ClockPort);
    }

    [Fact]
    public async Task Default_ports_are_tried_first_and_an_ephemeral_port_is_used_when_taken()
    {
        using var squatter = new UdpDatagramChannel(new IPEndPoint(IPAddress.Any, 0));
        int taken = squatter.LocalEndPoint.Port;
        var runtime = Make(new HostRuntimeOptions(ControlPort: 0, AudioPort: taken, ClockPort: 0,
            DiscoveryTarget: _beaconListener.LocalEndPoint));

        await runtime.StartAsync("Host");

        Assert.True(runtime.IsHosting);
        Assert.DoesNotContain(taken, _env.RecordingChannels.Created.Select(c => c.LocalEndPoint.Port));
        await runtime.StopAsync();
    }

    [Fact]
    public async Task A_control_port_in_use_fails_with_a_message_and_releases_everything()
    {
        var squatter = new TcpListener(IPAddress.Any, 0);
        squatter.Start();
        try
        {
            int port = ((IPEndPoint)squatter.LocalEndpoint).Port;
            var runtime = Make(new HostRuntimeOptions(ControlPort: port, AudioPort: 0, ClockPort: 0, DiscoveryTarget: _beaconListener.LocalEndPoint));

            var ex = await Assert.ThrowsAsync<HostStartException>(() => runtime.StartAsync("Host"));

            Assert.Contains(port.ToString(), ex.Message);
            Assert.False(runtime.IsHosting);
            Assert.Equal(1, _afterStop);
            Assert.Equal(1, _env.Log.Count("capture0.dispose"));
            Assert.Equal(1, _env.Log.Count("keeper0.dispose"));
            foreach (var ch in _env.RecordingChannels.Created)
            {
                // A closed channel's port can be bound again.
                using var rebound = new UdpDatagramChannel(new IPEndPoint(IPAddress.Any, ch.LocalEndPoint.Port));
            }
        }
        finally
        {
            squatter.Stop();
        }
    }

    [Fact]
    public async Task A_capture_failure_fails_with_the_capture_message_and_restores_the_client()
    {
        _env.FailCapture = true;

        var ex = await Assert.ThrowsAsync<HostStartException>(() => _runtime.StartAsync("Host"));

        Assert.Equal(HostRuntime.CaptureFailedText, ex.Message);
        Assert.False(_runtime.IsHosting);
        Assert.Equal(1, _afterStop);
        foreach (var ch in _env.RecordingChannels.Created)
        {
            using var rebound = new UdpDatagramChannel(new IPEndPoint(IPAddress.Any, ch.LocalEndPoint.Port));
        }
    }

    [Fact]
    public async Task A_failing_local_output_does_not_stop_hosting_and_is_retried()
    {
        _env.FailOutput = true;
        await _runtime.StartAsync("Host");
        Assert.True(_runtime.IsHosting);
        Assert.Empty(_env.Outputs);

        _env.FailOutput = false;
        _runtime.Tick();
        Pump();

        Assert.Single(_env.Outputs);
        Assert.Equal(1, _env.Log.Count("output0.start"));
    }

    // ---- local playback ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Local_playback_is_not_started_when_vb_cable_is_missing()
    {
        _env.CableId = null;

        await _runtime.StartAsync("Host");

        Assert.Empty(_env.Outputs);
        Assert.Equal(HostViewModel_Banner(HostAudioStatus.VbCableMissing), _runtime.Current!.AudioBanner);
    }

    [Fact]
    public async Task Local_playback_is_not_started_when_the_chosen_speakers_are_the_cable()
    {
        _settings.Update(s => s with { LocalSpeakerDeviceId = CableDeviceId });

        await _runtime.StartAsync("Host");

        Assert.Empty(_env.Outputs);
        Assert.Equal(HostAudioStatus.LocalSpeakerIsCaptured, _runtime.Current!.AudioStatus);
    }

    [Fact]
    public async Task With_the_cable_installed_but_not_default_the_cable_is_captured_by_id_and_nothing_plays_locally()
    {
        _env.DefaultId = SpeakersId;   // VB-CABLE exists but is not the default: the speakers are what this PC plays on

        await _runtime.StartAsync("Host");

        Assert.Equal(new string?[] { CableDeviceId }, _env.CaptureDevices);
        Assert.Equal(new string?[] { CableDeviceId }, _env.KeeperDevices);
        Assert.Empty(_env.Outputs);   // playing on the speakers would double up with what this PC already plays there
        Assert.Equal(HostAudioStatus.VbCableNotDefault, _runtime.Current!.AudioStatus);
        Assert.Equal(HostViewModel_Banner(HostAudioStatus.VbCableNotDefault), _runtime.Current.AudioBanner);
    }

    [Fact]
    public async Task Without_the_cable_the_default_device_is_captured_and_the_host_only_sends()
    {
        _env.CableId = null;
        _env.DefaultId = SpeakersId;

        await _runtime.StartAsync("Host");

        Assert.Equal(new string?[] { null }, _env.CaptureDevices);
        Assert.Equal(new string?[] { null }, _env.KeeperDevices);
        Assert.Empty(_env.Outputs);
    }

    [Fact]
    public async Task Local_playback_is_refused_on_the_captured_device_whatever_it_is()
    {
        await _runtime.StartAsync("Host");
        Assert.Single(_env.Outputs);

        _runtime.SetLocalSpeaker(CableDeviceId);   // the captured endpoint

        Assert.Single(_env.Outputs);
        Assert.Equal(1, _env.Log.Count("output0.stop"));
        Assert.Equal(HostAudioStatus.LocalSpeakerIsCaptured, _runtime.Current!.AudioStatus);
    }

    [Fact]
    public async Task With_the_cable_as_default_the_first_real_speakers_are_used_and_remembered()
    {
        _env.DefaultId = CableDeviceId;

        await _runtime.StartAsync("Host");

        Assert.Equal(new[] { SpeakersId }, _env.OutputDevices);
        Assert.Equal(SpeakersId, _settings.Current.LocalSpeakerDeviceId);
        Assert.Null(_runtime.Current!.AudioBanner);
    }

    [Fact]
    public async Task The_saved_speakers_win_over_the_default_device()
    {
        _settings.Update(s => s with { LocalSpeakerDeviceId = HeadphonesId });

        await _runtime.StartAsync("Host");

        Assert.Equal(new[] { HeadphonesId }, _env.OutputDevices);
    }

    [Fact]
    public async Task The_speaker_list_leaves_out_the_captured_cable_before_and_while_hosting()
    {
        Assert.Equal(new[] { SpeakersId, HeadphonesId }, _runtime.Speakers().Select(d => d.Id));

        await _runtime.StartAsync("Host");

        Assert.Equal(new[] { SpeakersId, HeadphonesId }, _runtime.Speakers().Select(d => d.Id));
    }

    [Fact]
    public async Task Choosing_other_speakers_replaces_the_output_and_saves_the_choice()
    {
        await _runtime.StartAsync("Host");

        _runtime.SetLocalSpeaker(HeadphonesId);

        Assert.Equal(new[] { SpeakersId, HeadphonesId }, _env.OutputDevices);
        Assert.True(_env.Log.IndexOf("output0.stop") < _env.Log.IndexOf("output1.start"));   // old one is stopped first
        Assert.Equal(1, _env.Log.Count("output0.dispose"));
        Assert.Equal(HeadphonesId, _settings.Current.LocalSpeakerDeviceId);
        Assert.Equal(HeadphonesId, _runtime.LocalSpeakerId);
    }

    [Fact]
    public async Task Choosing_the_cable_as_speakers_stops_local_playback_and_shows_the_banner()
    {
        await _runtime.StartAsync("Host");

        _runtime.SetLocalSpeaker(CableDeviceId);

        Assert.Equal(1, _env.Log.Count("output0.stop"));
        Assert.Equal(1, _env.Log.Count("output0.dispose"));
        Assert.Equal(HostViewModel_Banner(HostAudioStatus.LocalSpeakerIsCaptured), _runtime.Current!.AudioBanner);

        _runtime.SetLocalSpeaker(SpeakersId);   // and back
        Assert.Equal(2, _env.Outputs.Count);
        Assert.Null(_runtime.Current.AudioBanner);
    }

    [Fact]
    public async Task An_invalidated_local_output_is_rebuilt_via_the_ticker_not_inline()
    {
        await _runtime.StartAsync("Host");

        _env.Outputs[0].Raise();
        Assert.Single(_env.Outputs);          // nothing happens on the raising thread
        Assert.Equal(0, Queued());

        _runtime.Tick();
        Assert.Equal(1, Queued());
        Assert.Single(_env.Outputs);          // still nothing until the UI thread runs it

        Pump();
        Assert.Equal(2, _env.Outputs.Count);
        Assert.True(_env.Log.IndexOf("output0.stop") < _env.Log.IndexOf("output1.start"));
        Assert.Equal(1, _env.Log.Count("output0.dispose"));
    }

    [Fact]
    public async Task Many_invalidation_events_queue_one_rebuild()
    {
        await _runtime.StartAsync("Host");
        _env.Outputs[0].Raise();
        _env.Keepers[0].Raise();

        for (int i = 0; i < 5; i++) _runtime.Tick();

        Assert.Equal(1, Queued());
    }

    // ---- device changes ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_default_device_change_refreshes_the_banner_and_starts_local_playback_without_a_restart()
    {
        _env.DefaultId = SpeakersId;
        await _runtime.StartAsync("Host");
        var before = _runtime.Current!;
        Assert.Empty(_env.Outputs);
        int changed = 0;
        _runtime.Changed += () => changed++;

        _env.DefaultId = CableDeviceId;   // the user follows the banner's advice
        _env.DefaultChanged!();
        Assert.Empty(_env.Outputs);       // nothing happens on the raising thread
        _runtime.Tick();
        Pump();

        Assert.Single(_env.Captures);     // no restart: the cable is still what is captured
        Assert.Single(_env.Keepers);
        Assert.Same(before, _runtime.Current);
        Assert.Equal(0, changed);
        Assert.Null(before.AudioBanner);
        Assert.Equal(new[] { SpeakersId }, _env.OutputDevices);
        Assert.Equal(1, _env.Log.Count("output0.start"));
    }

    [Fact]
    public async Task A_default_device_change_away_from_the_cable_stops_local_playback_without_a_restart()
    {
        await _runtime.StartAsync("Host");
        Assert.Single(_env.Outputs);

        _env.DefaultId = SpeakersId;
        _env.DefaultChanged!();
        _runtime.Tick();
        Pump();

        Assert.Single(_env.Captures);
        Assert.Equal(1, _env.Log.Count("output0.stop"));
        Assert.Equal(1, _env.Log.Count("output0.dispose"));
        Assert.Equal(HostViewModel_Banner(HostAudioStatus.VbCableNotDefault), _runtime.Current!.AudioBanner);
    }

    [Fact]
    public async Task Without_the_cable_a_default_device_change_restarts_the_host_because_the_default_is_what_is_captured()
    {
        _env.CableId = null;
        await _runtime.StartAsync("Host");

        _env.DefaultId = HeadphonesId;
        _env.DefaultChanged!();
        _runtime.Tick();
        Pump();
        await WaitUntil(() => _env.Captures.Count == 2);

        Assert.True(_runtime.IsHosting);
    }

    [Fact]
    public async Task A_lost_capture_device_restarts_the_host_keeping_name_code_and_stats_ui_state()
    {
        await _runtime.StartAsync("Host");
        var before = _runtime.Current!;
        before.SessionName = "Renamed";
        string code = before.PairingCode;
        int changed = 0;
        _runtime.Changed += () => changed++;

        _env.Captures[0].Raise();
        Assert.Single(_env.Captures);   // not inline
        _runtime.Tick();
        Pump();
        await WaitUntil(() => _env.Captures.Count == 2);

        Assert.True(_runtime.IsHosting);
        Assert.NotSame(before, _runtime.Current);
        Assert.Equal("Renamed", _runtime.Current!.SessionName);
        Assert.Equal(code, _runtime.Current.PairingCode);
        Assert.Equal(1, _env.Log.Count("capture0.stop"));
        Assert.Equal(1, _env.Log.Count("keeper0.dispose"));
        Assert.Equal(1, _env.Log.Count("capture1.start"));
        Assert.Equal(new string?[] { CableDeviceId, CableDeviceId }, _env.CaptureDevices);
        Assert.Equal(1, changed);
        Assert.Null(_runtime.Current.AudioBanner);
        Assert.Equal(0, _afterStop);                 // the client stays released across a restart
        Assert.Equal(1, _beforeStart);
    }

    [Fact]
    public async Task An_invalidated_silence_keeper_on_the_captured_device_restarts_the_host()
    {
        await _runtime.StartAsync("Host");
        string code = _runtime.Current!.PairingCode;

        _env.Keepers[0].Raise();
        Assert.Single(_env.Keepers);   // not inline
        _runtime.Tick();
        Pump();
        await WaitUntil(() => _env.Captures.Count == 2);

        Assert.True(_runtime.IsHosting);
        Assert.Equal(2, _env.Keepers.Count);
        Assert.Equal(1, _env.Log.Count("keeper0.dispose"));
        Assert.Equal(1, _env.Log.Count("keeper1.start"));
        Assert.Equal(code, _runtime.Current!.PairingCode);
    }

    [Fact]
    public async Task Losing_the_cable_restarts_the_host_capturing_the_default_as_a_sender_only()
    {
        await _runtime.StartAsync("Host");
        Assert.Single(_env.Outputs);

        _env.CableId = null;
        _env.SpeakerList.RemoveAll(d => d.Id == CableDeviceId);
        _env.DefaultId = SpeakersId;
        _env.Captures[0].Raise();
        _runtime.Tick();
        Pump();
        await WaitUntil(() => _env.Captures.Count == 2);

        Assert.Equal(new string?[] { CableDeviceId, null }, _env.CaptureDevices);
        Assert.Single(_env.Outputs);   // the old local output was stopped and none was started
        Assert.Equal(1, _env.Log.Count("output0.dispose"));
        Assert.Equal(HostViewModel_Banner(HostAudioStatus.VbCableMissing), _runtime.Current!.AudioBanner);
    }

    [Fact]
    public async Task A_restart_that_fails_ends_hosting_with_an_error_and_restores_the_client()
    {
        await _runtime.StartAsync("Host");
        _env.FailCapture = true;

        _env.Captures[0].Raise();
        _runtime.Tick();
        Pump();
        await WaitUntil(() => Volatile.Read(ref _afterStop) == 1);

        Assert.False(_runtime.IsHosting);
        Assert.NotNull(_runtime.LastError);
        Assert.Equal(1, _afterStop);
    }

    // ---- ticking, beacon, clients ------------------------------------------------------------------------------------

    [Fact]
    public void Tick_without_a_host_does_nothing()
    {
        _runtime.Tick();
        _runtime.Poll();
        Assert.Equal(0, Queued());
    }

    [Fact]
    public async Task Tick_sends_the_discovery_beacon_with_the_name_and_control_port()
    {
        await _runtime.StartAsync("Living room");

        var beacon = await NextBeaconAsync();

        Assert.Equal("Living room", beacon.Name);
        Assert.NotEqual(0, beacon.ControlPort);
        Assert.Equal(2, beacon.ProtocolVersion);
    }

    [Fact]
    public async Task A_long_multibyte_name_is_truncated_in_the_beacon_and_the_beacon_still_arrives()
    {
        await _runtime.StartAsync(new string('日', 40));   // 120 UTF-8 bytes

        var beacon = await NextBeaconAsync();

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(beacon.Name) <= 64);
        Assert.Equal(new string('日', 21), beacon.Name);
    }

    [Fact]
    public async Task Renaming_the_session_changes_the_next_beacon()
    {
        await _runtime.StartAsync("Old");
        await NextBeaconAsync();
        _runtime.Current!.SessionName = "New";

        // The broadcaster sends once a second; wait for one that carries the new name.
        await WaitUntil(() =>
        {
            _runtime.Tick();
            lock (_beacons) return _beacons[^1].Name == "New";
        });
    }

    [Fact]
    public async Task A_joining_client_shows_up_as_a_row_via_the_marshalled_clients_changed_event()
    {
        await _runtime.StartAsync("Host");
        var id = Guid.NewGuid();

        await JoinAsync(id, name: "Kitchen");
        await WaitUntil(() => Queued() > 0);
        Pump();

        var row = Assert.Single(_runtime.Current!.Clients);
        Assert.Equal(id, row.DeviceId);
        Assert.Equal("Kitchen", row.Name);
    }

    [Fact]
    public async Task Poll_refreshes_rows_without_a_clients_changed_event()
    {
        await _runtime.StartAsync("Host");
        var (client, _) = await JoinAsync(Guid.NewGuid());
        await WaitUntil(() => Queued() > 0);
        Pump();
        Assert.Equal(RhythmSync.Core.Clock.SyncState.Connecting, _runtime.Current!.Clients[0].State);

        await client.SendAsync(new Ready());
        await client.SendAsync(new Heartbeat(new HeartbeatStats(0, 7.5, 3, 0.1, 0.2, RhythmSync.Core.Clock.SyncState.InSync)));
        await WaitUntil(() => { Pump(); _runtime.Poll(); return _runtime.Current.Clients[0].LatePct == 7.5; });

        Assert.True(_runtime.Current.Clients[0].Congested);
        Assert.Equal(RhythmSync.Core.Clock.SyncState.InSync, _runtime.Current.Clients[0].State);
    }

    [Fact]
    public async Task Captured_audio_is_streamed_to_a_ready_client()
    {
        await _runtime.StartAsync("Host");
        using var audio = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var (client, _) = await JoinAsync(Guid.NewGuid(), audioPort: ((IPEndPoint)audio.Client.LocalEndPoint!).Port);
        await client.SendAsync(new Ready());
        await WaitUntil(() => _runtime.Current!.Clients.Count >= 0 && AnyReady());

        // 100 ms of stereo silence at 48 kHz in 10 ms chunks.
        var chunk = new float[480 * 2];
        long position = 0;
        var clock = new StopwatchClock();
        for (int i = 0; i < 20; i++)
        {
            _env.Captures[0].Handler!(chunk, position, clock.NowTicks);
            position += 480;
        }

        var received = await audio.ReceiveAsync().WaitAsync(Timeout);
        Assert.True(PacketSerializer.TryReadHeader(received.Buffer, out var type, out _));
        Assert.Equal(PacketType.Audio, type);
        Assert.Equal(PacketSerializer.AudioPacketBytes, received.Buffer.Length);

        bool AnyReady()
        {
            _runtime.Poll();
            Pump();
            return _runtime.Current!.Clients.Count == 1 && _runtime.Current.Clients[0].State == RhythmSync.Core.Clock.SyncState.Syncing;
        }
    }

    [Fact]
    public async Task A_latency_change_is_saved_and_reaches_clients()
    {
        await _runtime.StartAsync("Host");
        var (client, welcome) = await JoinAsync(Guid.NewGuid());
        Assert.Equal(_settings.Current.TargetLatencyMs, welcome.TargetLatencyMs);

        _runtime.Current!.LatencyMs = 300;

        Assert.Equal(300, _settings.Current.TargetLatencyMs);
        var message = await client.ReadAsync();
        while (message is not SetLatency) message = await client.ReadAsync();   // skip heartbeats
        Assert.Equal(300, ((SetLatency)message).TargetLatencyMs);
    }

    [Fact]
    public async Task A_row_change_is_saved_to_the_settings()
    {
        await _runtime.StartAsync("Host");
        var id = Guid.NewGuid();
        await JoinAsync(id);
        await WaitUntil(() => Queued() > 0);
        Pump();

        _runtime.Current!.Clients[0].OffsetMs = 12.5;

        Assert.Equal(12.5, _settings.Current.Devices[id].OffsetMs);
    }

    [Fact]
    public async Task A_client_with_stored_settings_is_sent_them_when_it_joins()
    {
        var id = Guid.NewGuid();
        _settings.Update(s => s with { Devices = new Dictionary<Guid, DeviceSettings> { [id] = new DeviceSettings(0.6f, -7) } });
        await _runtime.StartAsync("Host");

        var (client, _) = await JoinAsync(id);
        await WaitUntil(() => Queued() > 0);
        Pump();

        var message = await client.ReadAsync();
        while (message is not SetDeviceSettings) message = await client.ReadAsync();
        var settings = Assert.IsType<SetDeviceSettings>(message);
        Assert.Equal(0.6f, settings.Volume);
        Assert.Equal(-7, settings.OffsetMs);
        Assert.Equal(new float[10], settings.EqGainsDb);   // flat until the host UI sends an EQ
    }

    // ---- stopping ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_releases_in_order_and_restores_the_client()
    {
        await _runtime.StartAsync("Host");
        var (_, welcome) = await JoinAsync(Guid.NewGuid());
        int changed = 0;
        _runtime.Changed += () => changed++;

        await _runtime.StopAsync();

        var log = _env.Log;
        Assert.False(_runtime.IsHosting);
        Assert.Null(_runtime.Current);
        Assert.Equal(1, changed);
        Assert.Equal(1, _afterStop);
        Assert.True(log.IndexOf("watcher.dispose") < log.IndexOf("capture0.stop"));
        Assert.True(log.IndexOf("capture0.stop") < log.IndexOf("keeper0.dispose"));
        Assert.True(log.IndexOf("keeper0.dispose") < log.IndexOf("output0.stop"));
        Assert.True(log.IndexOf("output0.stop") < log.IndexOf("output0.dispose"));
        // The pipeline went last: its channels are closed, so their ports can be bound again.
        using var rebound = new UdpDatagramChannel(new IPEndPoint(IPAddress.Any, welcome.AudioPort));
    }

    [Fact]
    public async Task Stop_closes_the_control_port_and_connected_clients()
    {
        await _runtime.StartAsync("Host");
        var beacon = await NextBeaconAsync();
        var (client, _) = await JoinAsync(Guid.NewGuid());

        await _runtime.StopAsync();

        Assert.Null(await ReadUntilClosedAsync(client));
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, beacon.ControlPort).WaitAsync(Timeout);
        });
    }

    private static async Task<ControlMessage?> ReadUntilClosedAsync(TestClient client)
    {
        try
        {
            while (true)
            {
                var m = await client.ReadAsync();
                if (m is null) return null;
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    [Fact]
    public async Task A_failing_step_does_not_skip_the_rest_of_the_shutdown()
    {
        await _runtime.StartAsync("Host");
        var beacon = await NextBeaconAsync();
        _env.Captures[0].ThrowOnStop = true;
        _env.Keepers[0].ThrowOnDispose = true;
        _env.Outputs[0].ThrowOnStop = true;

        await _runtime.StopAsync();   // must not throw

        Assert.Equal(1, _env.Log.Count("capture0.stop"));
        Assert.Equal(1, _env.Log.Count("capture0.dispose"));
        Assert.Equal(1, _env.Log.Count("keeper0.dispose"));
        Assert.Equal(1, _env.Log.Count("output0.dispose"));
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, beacon.ControlPort).WaitAsync(Timeout);
        });
    }

    [Fact]
    public async Task Stop_when_not_hosting_does_nothing_and_a_host_can_start_again_after_stopping()
    {
        await _runtime.StopAsync();
        Assert.Equal(0, _afterStop);

        await _runtime.StartAsync("First");
        await _runtime.StopAsync();
        await _runtime.StartAsync("Second");

        Assert.True(_runtime.IsHosting);
        Assert.Equal("Second", _runtime.Current!.SessionName);
        Assert.Equal(2, _beforeStart);
    }

    [Fact]
    public async Task Queued_ui_work_after_stop_is_ignored()
    {
        await _runtime.StartAsync("Host");
        _env.Outputs[0].Raise();
        _runtime.Tick();

        await _runtime.StopAsync();
        Pump();   // the rebuild that was queued before the stop must find the host gone

        Assert.Single(_env.Outputs);
    }

    private static string HostViewModel_Banner(HostAudioStatus status) => status switch
    {
        HostAudioStatus.VbCableMissing => RhythmSync.App.ViewModels.HostViewModel.VbCableMissingBanner,
        HostAudioStatus.LocalSpeakerIsCaptured => RhythmSync.App.ViewModels.HostViewModel.LocalSpeakerIsCapturedBanner,
        HostAudioStatus.VbCableNotDefault => RhythmSync.App.ViewModels.HostViewModel.VbCableNotDefaultBanner,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
