using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RhythmSync.App.ViewModels;
using RhythmSync.Core;
using RhythmSync.Core.Audio;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Config;
using RhythmSync.Core.Host;
using RhythmSync.Core.Net;
using RhythmSync.Core.Protocol;
using RhythmSync.Platform.Windows;
using Serilog;

namespace RhythmSync.App.Services;

/// <param name="ControlPort">TCP port clients connect to.</param>
/// <param name="AudioPort">Preferred UDP port for audio; if taken, an ephemeral one is used (the Welcome carries the real port).</param>
/// <param name="ClockPort">Preferred UDP port for clock sync; same fallback.</param>
/// <param name="DiscoveryTarget">Where beacons go (the LAN broadcast address and discovery port).</param>
/// <param name="RetryDelayTicks">Wait before retrying a failed keeper or local-output rebuild.</param>
public sealed record HostRuntimeOptions(int ControlPort = 47801, int AudioPort = 47802, int ClockPort = 47803,
    IPEndPoint? DiscoveryTarget = null, long RetryDelayTicks = AudioFormat.TicksPerSecond)
{
    public IPEndPoint Target => DiscoveryTarget ?? new IPEndPoint(IPAddress.Broadcast, 47800);
}

/// <summary>
/// Host mode: captures what this PC plays (WASAPI loopback, with a silence keeper so packets keep coming), runs the host
/// pipeline, session and discovery beacon, and plays locally in sync on real speakers. Owns the lifetime of all of it.
/// </summary>
/// <remarks>
/// What is captured: with VB-CABLE installed, the cable's playback endpoint, by id (whatever the default device is);
/// without it, the default playback device, and the host only sends (no local playback). Local playback runs only while
/// <see cref="HostAudioCheck"/> says <see cref="HostAudioStatus.Ok"/>: the cable is the default device (so this PC's own
/// sound goes only through the cable) and the chosen speakers are not the captured endpoint. Anything else would play
/// the sound twice here, or feed the delayed local output back into the capture and around again forever.
///
/// Threading: <see cref="StartAsync"/>, <see cref="StopAsync"/>, <see cref="Poll"/> and everything posted through the
/// injected <c>post</c> run on the UI thread; <see cref="Tick"/> runs on the ticker thread and only reads the published run,
/// calls <see cref="HostSession.Tick"/> and <see cref="DiscoveryBroadcaster.Tick"/>, and queues work. Device events arrive on
/// arbitrary threads and only set flags that <see cref="Tick"/> turns into posted work, so nothing is rebuilt on the
/// raising thread.
///
/// Device changes: a change of the default playback device only re-evaluates the audio status (the banner) and starts or
/// stops local playback to match. Loss or invalidation of the captured endpoint (reported by the capture or by the
/// silence keeper on it), or a default-device change while the default itself is what is captured, restarts the whole
/// host (same name, pairing code and ports; clients reconnect on their own): capture positions restart at zero on a new
/// stream and the pipeline's timestamp fit (a 30 s window) cannot be reset, so continuing in place would mis-time audio
/// for up to 30 s. A failed local output is rebuilt in place, retried about once a second.
///
/// The discovery listener (UDP 47800) is not touched by host mode: it keeps running and simply hears this host's own beacon.
/// </remarks>
public sealed class HostRuntime : IHostRuntime
{
    public const string CaptureFailedText = "Can't capture this PC's audio – check your sound settings.";

    private sealed class Run
    {
        public required string Name;
        public required string PairingCode;
        public uint SessionId;
        public volatile int LatencyMs;
        public volatile bool Stopping;

        public IDatagramChannel? AudioChannel, ClockChannel;
        public HostPipeline? Pipeline;
        public HostSession? Session;
        public DiscoveryBroadcaster? Broadcaster;
        public HostViewModel? Vm;
        public string? CapturedId;                           // the captured endpoint; null: the default device
        public IAudioCapture? Capture;
        public ISilenceKeeper? Keeper;
        public IAudioOutput? Output;
        public Action? OutputHandler;
        public ClientPipeline? Local;
        public IDisposable? Watcher;
        public Action? ClientsHandler;

        public int RestartDirty, StatusDirty, OutputDirty;   // set from any thread
        public int RebuildQueued;                            // 1 while a posted rebuild is pending
        public long RetryAfterTicks;                         // ticker thread and UI thread; a stale read only delays a retry
    }

    /// <summary>Forwards to the session, applying a latency change to the local playback first.</summary>
    private sealed class Control(HostRuntime owner, Run run, HostSession session) : IHostControl
    {
        public Task SetLatencyAsync(int ms)
        {
            int latency = Math.Clamp(ms, HostSession.MinLatencyMs, HostSession.MaxLatencyMs);
            owner.ApplyLocalLatency(run, latency);
            return session.SetLatencyAsync(latency);
        }

        public Task SetDeviceSettingsAsync(Guid deviceId, float volume, double offsetMs)
            => session.SetDeviceSettingsAsync(deviceId, volume, offsetMs);
    }

    private readonly IHostEnvironment _env;
    private readonly ISessionClock _clock;
    private readonly SettingsPersister _settings;
    private readonly Action<Action> _post;
    private readonly Func<Task> _beforeStart;
    private readonly Action _afterStop;
    private readonly HostRuntimeOptions _options;
    private readonly Action? _openSoundSettings;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private Run? _run;
    private string? _lastError;

    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="beforeStart">Runs before hosting starts: releases the client session (host and client modes are exclusive).</param>
    /// <param name="afterStop">Runs after hosting has stopped or failed to start: lets the client session be used again.</param>
    public HostRuntime(IHostEnvironment env, ISessionClock clock, SettingsPersister settings, Action<Action> post,
        Func<Task> beforeStart, Action afterStop, HostRuntimeOptions? options = null, Action? openSoundSettings = null)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _beforeStart = beforeStart ?? throw new ArgumentNullException(nameof(beforeStart));
        _afterStop = afterStop ?? throw new ArgumentNullException(nameof(afterStop));
        _options = options ?? new HostRuntimeOptions();
        _openSoundSettings = openSoundSettings;
    }

    public bool IsHosting => Volatile.Read(ref _run) is not null;

    public HostViewModel? Current => Volatile.Read(ref _run)?.Vm;

    public string? LastError => _lastError;

    public event Action? Changed;

    /// <summary>Playback devices to choose for local playback: every active one except the captured endpoint (the VB-CABLE).</summary>
    public IReadOnlyList<AudioDeviceInfo> Speakers()
    {
        var run = Volatile.Read(ref _run);
        string? captured = run is not null ? run.CapturedId : SafeCable();
        return _env.Speakers().Where(d => !Same(d.Id, captured)).ToList();
    }

    public string? LocalSpeakerId => ResolveSpeaker();

    // ---- starting and stopping ---------------------------------------------------------------------------------------

    public async Task StartAsync(string sessionName)
    {
        ArgumentNullException.ThrowIfNull(sessionName);
        await _lifecycle.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_run is not null) return;
            _lastError = null;
            try { await _beforeStart().ConfigureAwait(true); }
            catch (Exception e) { Log.Warning(e, "Releasing the client session failed"); }

            try
            {
                await StartCoreAsync(sessionName, pairingCode: null).ConfigureAwait(true);
            }
            catch
            {
                SafeAfterStop();
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
        RaiseChanged();
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(true);
        Run? run;
        try
        {
            run = Interlocked.Exchange(ref _run, null);   // first: the ticker sees "not hosting" and stops touching the session
            if (run is null) return;
            await TearDownAsync(run).ConfigureAwait(true);
            SafeAfterStop();
        }
        finally
        {
            _lifecycle.Release();
        }
        RaiseChanged();
    }

    private async Task StartCoreAsync(string name, string? pairingCode)
    {
        var settings = _settings.Current;
        var run = new Run { Name = name, PairingCode = pairingCode ?? "", LatencyMs = settings.TargetLatencyMs };
        try
        {
            run.SessionId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
            run.AudioChannel = BindChannel(_options.AudioPort, "audio");
            run.ClockChannel = BindChannel(_options.ClockPort, "clock");

            try
            {
                run.CapturedId = _env.VbCableId();
                run.Capture = _env.CreateCapture(run.CapturedId);
                run.Keeper = _env.CreateSilenceKeeper(run.CapturedId);
            }
            catch (Exception e)
            {
                throw new HostStartException(CaptureFailedText, e);
            }

            run.Pipeline = new HostPipeline(run.SessionId, run.AudioChannel, run.ClockChannel, _clock, run.Capture.SampleRate, run.LatencyMs);
            // The Welcome echoes these options, so they must be the ports the channels really bound.
            var options = new HostSessionOptions(name, _options.ControlPort, run.AudioChannel.LocalEndPoint.Port,
                run.ClockChannel.LocalEndPoint.Port, run.LatencyMs, PairingCode: pairingCode);
            run.Session = new HostSession(options, _clock, run.Pipeline);
            run.PairingCode = run.Session.PairingCode;
            try
            {
                await run.Session.StartAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (SocketException e)
            {
                throw new HostStartException($"Can't start hosting – port {_options.ControlPort} is already in use. Is RhythmSync already hosting?", e);
            }

            run.Vm = CreateViewModel(run);
            run.Broadcaster = new DiscoveryBroadcaster(_env.Channels.Create(new IPEndPoint(IPAddress.Any, 0), broadcast: true),
                _options.Target, _clock, () => Beacon(run));

            run.ClientsHandler = () => Post(() => PumpClients(run));
            run.Session.ClientsChanged += run.ClientsHandler;
            run.Watcher = _env.WatchDefaultRender(() =>
            {
                if (run.CapturedId is null) Volatile.Write(ref run.RestartDirty, 1);   // the captured device itself changed
                else Volatile.Write(ref run.StatusDirty, 1);
            });
            RememberLocalSpeaker();
            ReconcileLocal(run, recreate: false);

            run.Capture.DeviceInvalidated += () => Volatile.Write(ref run.RestartDirty, 1);
            run.Keeper.DeviceInvalidated += () => Volatile.Write(ref run.RestartDirty, 1);
            run.Keeper.Start();
            run.Capture.Start(run.Pipeline.OnCaptured);

            Volatile.Write(ref _run, run);   // from here the ticker drives it
        }
        catch (Exception e)
        {
            Log.Error(e, "Hosting failed to start");
            await TearDownAsync(run).ConfigureAwait(true);
            throw e as HostStartException ?? new HostStartException($"Can't start hosting: {e.Message}", e);
        }
    }

    /// <summary>Releases everything of <paramref name="run"/> in the required order; one failing step never skips the next.</summary>
    private async Task TearDownAsync(Run run)
    {
        run.Stopping = true;
        Step("device watcher", () => run.Watcher?.Dispose());
        Step("clients subscription", () =>
        {
            if (run.Session is not null && run.ClientsHandler is not null) run.Session.ClientsChanged -= run.ClientsHandler;
        });
        Step("capture stop", () => run.Capture?.Stop());
        Step("silence keeper", () => run.Keeper?.Dispose());
        Step("capture dispose", () => run.Capture?.Dispose());
        if (run.Session is not null)
        {
            try { await run.Session.DisposeAsync().ConfigureAwait(true); }
            catch (Exception e) { Log.Warning(e, "Host teardown step failed: session"); }
        }
        Step("discovery broadcaster", () => run.Broadcaster?.Dispose());
        Step("local output", () => StopLocal(run));
        Step("host pipeline", () =>
        {
            if (run.Pipeline is not null)
            {
                run.Pipeline.Dispose();   // also disposes both channels
            }
            else
            {
                run.AudioChannel?.Dispose();
                run.ClockChannel?.Dispose();
            }
        });
    }

    private static void Step(string what, Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Warning(e, "Host teardown step failed: {What}", what); }
    }

    private IDatagramChannel BindChannel(int preferredPort, string what)
    {
        try
        {
            return _env.Channels.Create(new IPEndPoint(IPAddress.Any, preferredPort));
        }
        catch (SocketException e) when (preferredPort != 0)
        {
            Log.Warning(e, "UDP port {Port} for {What} is taken; using an ephemeral port", preferredPort, what);
            return _env.Channels.Create(new IPEndPoint(IPAddress.Any, 0));
        }
    }

    private HostViewModel CreateViewModel(Run run)
    {
        var vm = new HostViewModel(new Control(this, run, run.Session!),
            audioStatus: () => EvaluateAudio(run),
            storedSettings: id => _settings.Current.Devices.TryGetValue(id, out var d) ? d : null,
            saveSettings: (id, d) => _settings.Update(s => s with { Devices = new Dictionary<Guid, DeviceSettings>(s.Devices) { [id] = d } }),
            pairingCode: run.PairingCode, sessionName: run.Name, latencyMs: run.LatencyMs, openSoundSettings: _openSoundSettings);
        vm.LatencyChanged += ms => _settings.Update(s => s with { TargetLatencyMs = ms });
        return vm;
    }

    private BeaconPacket Beacon(Run run)
        => new(run.SessionId, (ushort)run.Session!.ControlPort, (byte)HostSession.ProtocolVersion, BeaconName.Fit(run.Vm!.SessionName));

    // ---- ticker thread -----------------------------------------------------------------------------------------------

    /// <summary>Ticker thread, every active tick: drives the session and the beacon and queues the work device events asked for.</summary>
    public void Tick()
    {
        var run = Volatile.Read(ref _run);
        if (run is null || run.Stopping) return;

        run.Session!.Tick();
        run.Broadcaster!.Tick();   // sends at most once a second

        bool restart = Volatile.Read(ref run.RestartDirty) != 0;
        bool retryDue = _clock.NowTicks >= Volatile.Read(ref run.RetryAfterTicks);
        bool rebuild = restart || Volatile.Read(ref run.StatusDirty) != 0 || (retryDue && Volatile.Read(ref run.OutputDirty) != 0);
        if (rebuild && Interlocked.Exchange(ref run.RebuildQueued, 1) == 0)
            Post(() => _ = RebuildAsync(run));
    }

    // ---- UI thread ---------------------------------------------------------------------------------------------------

    /// <summary>UI thread, about once a second: client heartbeats do not raise ClientsChanged, so re-read the client list.</summary>
    public void Poll()
    {
        var run = Volatile.Read(ref _run);
        if (run is not null) PumpClients(run);
    }

    private void PumpClients(Run run)
    {
        if (run.Stopping || !ReferenceEquals(Volatile.Read(ref _run), run) || run.Vm is null || run.Session is null) return;
        try { run.Vm.UpdateClients(run.Session.Clients); }
        catch (Exception e) { Log.Warning(e, "Refreshing the client list failed"); }
    }

    private async Task RebuildAsync(Run run)
    {
        try
        {
            if (run.Stopping || !ReferenceEquals(Volatile.Read(ref _run), run)) return;

            if (Interlocked.Exchange(ref run.RestartDirty, 0) != 0)
            {
                await RestartAsync(run).ConfigureAwait(true);
                return;
            }

            if (Interlocked.Exchange(ref run.StatusDirty, 0) != 0)
            {
                run.Vm?.RefreshAudioStatus();
                ReconcileLocal(run, recreate: false);
            }

            if (_clock.NowTicks < Volatile.Read(ref run.RetryAfterTicks)) return;
            if (Interlocked.Exchange(ref run.OutputDirty, 0) != 0)
            {
                run.Vm?.RefreshAudioStatus();
                ReconcileLocal(run, recreate: true);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Rebuilding the host audio failed");
        }
        finally
        {
            Volatile.Write(ref run.RebuildQueued, 0);
        }
    }

    /// <summary>The captured endpoint was lost or invalidated: rebuild the whole host (capturing the cable again if it is still there).</summary>
    private async Task RestartAsync(Run old)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref _run), old)) return;   // stopped in the meantime
            Volatile.Write(ref _run, null);
            await TearDownAsync(old).ConfigureAwait(true);
            try
            {
                await StartCoreAsync(old.Vm?.SessionName ?? old.Name, old.PairingCode).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                Log.Error(e, "Restarting the host after an audio device change failed");
                _lastError = $"Hosting stopped: {e.Message}";
                SafeAfterStop();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
        RaiseChanged();
    }

    // ---- local playback ----------------------------------------------------------------------------------------------

    /// <summary>Chooses the speakers the host plays on itself and saves the choice.</summary>
    public void SetLocalSpeaker(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        _settings.Update(s => s with { LocalSpeakerDeviceId = deviceId });
        var run = Volatile.Read(ref _run);
        if (run is null || run.Stopping) return;
        run.Vm?.RefreshAudioStatus();
        ReconcileLocal(run, recreate: true);
    }

    /// <summary>
    /// Brings local playback in line with the audio status: running on the chosen speakers only while the status is
    /// <see cref="HostAudioStatus.Ok"/>, off otherwise (it would double up with the sound of this PC, or feed back into the
    /// capture). Failures are logged and retried; they never stop hosting.
    /// </summary>
    private void ReconcileLocal(Run run, bool recreate)
    {
        if (run.Stopping || run.Vm is null || run.Pipeline is null) return;
        try
        {
            var status = run.Vm.AudioStatus;
            string? speaker = status == HostAudioStatus.Ok ? ResolveSpeaker() : null;

            if (speaker is null || recreate) StopLocal(run);
            if (speaker is not null && run.Output is null) StartLocal(run, speaker);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Local playback could not start; retrying");
            Volatile.Write(ref run.OutputDirty, 1);
            Volatile.Write(ref run.RetryAfterTicks, _clock.NowTicks + _options.RetryDelayTicks);
        }
    }

    private void StartLocal(Run run, string speakerId)
    {
        var output = _env.CreateLocalOutput(speakerId);
        try
        {
            var local = new ClientPipeline(run.SessionId, new IdentityClockMapping(), output.SampleRate);
            local.Scheduler.ApplyLatency(0, run.LatencyMs);
            Action handler = () =>
            {
                if (ReferenceEquals(run.Output, output)) Volatile.Write(ref run.OutputDirty, 1);
            };
            output.DeviceInvalidated += handler;
            run.Output = output;
            run.OutputHandler = handler;
            run.Local = local;
            output.Start(local.Scheduler);
            run.Pipeline!.Streamer.LocalSink = local;   // frames flow once the output is running
        }
        catch
        {
            StopLocal(run);
            Step("failed local output", output.Dispose);
            throw;
        }
    }

    private static void StopLocal(Run run)
    {
        if (run.Pipeline is not null) run.Pipeline.Streamer.LocalSink = null;
        var output = run.Output;
        if (output is null) return;
        run.Output = null;
        if (run.OutputHandler is { } handler) Step("local output handler", () => output.DeviceInvalidated -= handler);
        run.OutputHandler = null;
        run.Local = null;
        Step("local output stop", output.Stop);
        Step("local output dispose", output.Dispose);
    }

    private void ApplyLocalLatency(Run run, int latencyMs)
    {
        run.LatencyMs = latencyMs;
        try { run.Local?.Scheduler.ApplyLatency(0, latencyMs); }
        catch (Exception e) { Log.Warning(e, "Applying the new latency to local playback failed"); }
    }

    // ---- audio status and speakers -----------------------------------------------------------------------------------

    private HostAudioStatus EvaluateAudio(Run run)
        => HostAudioCheck.Evaluate(_env.DefaultRenderId(), _env.VbCableId(), run.CapturedId, ResolveSpeaker());

    /// <summary>
    /// The saved speakers if still present; else the current default device unless it is the VB-CABLE; else the first
    /// device that is not the VB-CABLE. A saved choice of the VB-CABLE itself is kept (the status then says it is captured).
    /// </summary>
    private string? ResolveSpeaker()
    {
        try
        {
            var devices = _env.Speakers();
            string? cable = _env.VbCableId();
            string? configured = _settings.Current.LocalSpeakerDeviceId;

            var saved = configured is null ? null : devices.FirstOrDefault(d => Same(d.Id, configured));
            if (saved is not null) return saved.Id;

            string? current = _env.DefaultRenderId();
            var def = current is null || Same(current, cable) ? null : devices.FirstOrDefault(d => Same(d.Id, current));
            if (def is not null) return def.Id;

            return devices.FirstOrDefault(d => !Same(d.Id, cable))?.Id;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Finding the local speakers failed");
            return null;
        }
    }

    /// <summary>Saves the speakers in use when none was chosen yet, so they stay the choice after the default device becomes the VB-CABLE.</summary>
    private void RememberLocalSpeaker()
    {
        if (_settings.Current.LocalSpeakerDeviceId is not null) return;
        string? resolved = ResolveSpeaker();
        if (resolved is not null && !Same(resolved, SafeCable()))
            _settings.Update(s => s with { LocalSpeakerDeviceId = resolved });
    }

    private string? SafeCable()
    {
        try { return _env.VbCableId(); }
        catch (Exception) { return null; }
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private void Post(Action action)
    {
        try
        {
            _post(() =>
            {
                try { action(); }
                catch (Exception e) { Log.Error(e, "Host UI update failed"); }
            });
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not post a host UI update");
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Log.Warning(e, "A host state subscriber failed"); }
    }

    private void SafeAfterStop()
    {
        try { _afterStop(); }
        catch (Exception e) { Log.Warning(e, "Restoring the client session failed"); }
    }
}
