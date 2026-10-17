using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RhythmSync.Core.Audio;

namespace RhythmSync.Platform.Windows;

/// <summary>
/// Captures what Windows plays to a render device (WASAPI shared-mode loopback, event driven) as interleaved stereo float
/// at the device mix rate. A dedicated MMCSS thread drains every packet; run a <see cref="SilenceKeeper"/> on the same
/// device so packets keep arriving while nothing plays.
/// An instance is single-use: Start once, then Stop/Dispose releases the device for good.
/// </summary>
public sealed class WasapiLoopbackSource : IAudioCapture
{
    private const int BufferDurationHns = 200_000;          // 20 ms, in 100 ns units
    private const int WaitTimeoutMs = 1000;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private readonly object _lifecycle = new();
    private readonly RenderFaultPolicy _policy = new();
    private readonly string _endpointId;
    private readonly bool _followsDefault;
    private readonly LoopbackPacketProcessor _processor;
    private readonly int _blockAlign;
    private readonly AutoResetEvent _bufferEvent = new(false);
    private readonly ManualResetEvent _stopEvent = new(false);

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioClient? _client;
    private AudioCaptureClient? _capture;
    private DeviceWatcher? _watcher;
    private CaptureHandler? _handler;
    private Thread? _thread;
    private bool _started;
    private bool _stopped;
    private int _released;

    /// <param name="renderDeviceId">Render endpoint id to loop back, or null to use (and follow) the system default device.</param>
    /// <exception cref="NotSupportedException">The device mix format is not 32-bit float or 16/24/32-bit integer PCM.</exception>
    public WasapiLoopbackSource(string? renderDeviceId)
    {
        _followsDefault = renderDeviceId is null;
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = renderDeviceId is null
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : _enumerator.GetDevice(renderDeviceId);
            _endpointId = _device.ID;
            _client = _device.AudioClient;

            WaveFormat mix = _client.MixFormat;
            SampleKind kind = MixFormat.Classify(mix);
            SampleRate = mix.SampleRate;
            _blockAlign = mix.BlockAlign;

            _client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                BufferDurationHns, 0, mix, Guid.Empty);
            _processor = new LoopbackPacketProcessor(kind, mix.Channels, mix.SampleRate, _client.BufferSize);
        }
        catch
        {
            ReleaseCore();
            throw;
        }
    }

    public int SampleRate { get; }

    /// <summary>Number of times the capture handler threw; the thread kept running and the chunk was dropped.</summary>
    public long HandlerFaults => _processor.HandlerFaults;

    /// <summary>
    /// Raised at most once per instance when the device is lost or (for a default-device source) the default changes.
    /// Threading: a lost device is reported on the capture thread (which then exits); a default-device change or
    /// endpoint removal is reported on a thread-pool thread, never on the Core Audio notification thread. No lock is held,
    /// so subscribers may call <see cref="Stop"/>/<see cref="Dispose"/> directly, but they should normally just set a flag.
    /// </summary>
    public event Action? DeviceInvalidated;

    /// <summary>True once the COM objects have been released (test hook).</summary>
    internal bool IsReleased => Volatile.Read(ref _released) == 1;

    /// <summary>
    /// Starts capturing. <paramref name="h"/> runs on the capture thread, must not block, and only sees the span for
    /// the duration of the call. An exception it throws is counted in <see cref="HandlerFaults"/> and otherwise ignored.
    /// </summary>
    public void Start(CaptureHandler h)
    {
        ArgumentNullException.ThrowIfNull(h);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_started) throw new InvalidOperationException("Capture already started.");
            _started = true;
            _handler = h;
            try
            {
                _client!.SetEventHandle(_bufferEvent.SafeWaitHandle.DangerousGetHandle());
                _capture = _client.AudioCaptureClient;

                _watcher = new DeviceWatcher();
                _watcher.DefaultRenderChanged += OnDefaultRenderChanged;
                _watcher.DeviceRemoved += OnDeviceRemoved;

                _client.Start();
                _thread = new Thread(CaptureLoop) { Name = "RhythmSync.WasapiLoopback", IsBackground = true };
                _thread.Start();
            }
            catch
            {
                _stopped = true;
                DisposeWatcher();
                ReleaseCore();
                throw;
            }
        }
    }

    /// <summary>Stops capturing and releases the device. Safe from any thread (including the handler), idempotent.</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_lifecycle)
        {
            if (_stopped) return;
            _stopped = true;
            thread = _thread;
        }

        DisposeWatcher();
        try { _stopEvent.Set(); } catch (ObjectDisposedException) { /* capture thread already released everything */ }

        if (thread is null)
        {
            ReleaseCore();
        }
        else if (thread != Thread.CurrentThread)
        {
            // The capture thread releases the COM objects itself on exit; if it is stuck in the handler we
            // must not pull the device out from under it, so on timeout it cleans up whenever it returns.
            if (!thread.Join(JoinTimeout))
                Debug.WriteLine("WasapiLoopbackSource: capture thread did not exit within the join timeout.");
        }
    }

    public void Dispose() => Stop();

    // These run on a Core Audio notification thread. A subscriber that calls Stop()/Dispose() would unregister the
    // endpoint callback from inside a notification, which Windows documents as a deadlock, so hop to the pool first.
    internal void OnDefaultRenderChanged()
    {
        if (_followsDefault) RaiseInvalidatedOffThread();
    }

    private void OnDeviceRemoved(string id)
    {
        if (string.Equals(id, _endpointId, StringComparison.OrdinalIgnoreCase)) RaiseInvalidatedOffThread();
    }

    private void RaiseInvalidatedOffThread() =>
        ThreadPool.UnsafeQueueUserWorkItem(static self => self.RaiseInvalidated(), this, preferLocal: false);

    private void DisposeWatcher()
    {
        DeviceWatcher? w = Interlocked.Exchange(ref _watcher, null);
        w?.Dispose();
    }

    /// <summary>Raises <see cref="DeviceInvalidated"/> at most once, never while holding a lock, and never after Stop.</summary>
    private void RaiseInvalidated()
    {
        if (Volatile.Read(ref _stopped)) return;
        if (!_policy.TryClaimInvalidation()) return;
        try { DeviceInvalidated?.Invoke(); } catch { /* a faulty subscriber must not kill the capture or COM thread */ }
    }

    private void CaptureLoop()
    {
        using IDisposable mmcss = Mmcss.JoinProAudio();
        try
        {
            WaitHandle[] handles = { _stopEvent, _bufferEvent };
            while (true)
            {
                int signalled = WaitHandle.WaitAny(handles, WaitTimeoutMs);
                if (signalled == 0) break;      // stop requested

                // A timeout still drains: asking for the packet size is how a vanished device is noticed.
                try
                {
                    DrainPackets();
                    _policy.Succeeded();
                }
                catch (Exception ex)
                {
                    // A lost device, or persistent failures that mean the stream is unusable, are reported as invalidation.
                    if (_policy.ShouldInvalidate(ex))
                    {
                        RaiseInvalidated();
                        break;
                    }
                }
            }
        }
        finally
        {
            ReleaseCore();
        }
    }

    /// <summary>Delivers every packet currently queued by the engine.</summary>
    private unsafe void DrainPackets()
    {
        AudioCaptureClient capture = _capture!;
        CaptureHandler handler = _handler!;

        while (!Volatile.Read(ref _stopped) && capture.GetNextPacketSize() > 0)
        {
            IntPtr buffer = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags, out long devicePosition, out long qpcPosition);
            try
            {
                if (frames <= 0) break;
                // DataDiscontinuity needs no handling here: the position is passed on as reported and the
                // consumer re-anchors when it sees a gap.
                bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
                var raw = new ReadOnlySpan<byte>((void*)buffer, frames * _blockAlign);
                _processor.Process(raw, frames, silent, devicePosition, qpcPosition, handler);   // qpcPosition is already 100 ns
            }
            finally
            {
                capture.ReleaseBuffer(frames);
            }
        }
    }

    /// <summary>Releases every COM object exactly once. Called by the capture thread on exit, or by Stop when it never ran.</summary>
    private void ReleaseCore()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1) return;
        DisposeWatcher();
        try { _client?.Stop(); } catch { /* device may already be gone */ }
        try { _client?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        try { _enumerator?.Dispose(); } catch { }
        _capture = null;
        _client = null;
        _device = null;
        _enumerator = null;
        _bufferEvent.Dispose();
        _stopEvent.Dispose();
    }
}
