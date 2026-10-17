using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RhythmSync.Core.Audio;

namespace RhythmSync.Platform.Windows;

/// <summary>
/// Event-driven shared-mode WASAPI renderer. A dedicated MMCSS thread wakes on every device period, asks the
/// <see cref="IRenderCallback"/> for stereo float audio and converts it to the device mix format.
/// An instance is single-use: Start once, then Stop/Dispose releases the device for good.
/// </summary>
public sealed class WasapiRenderOutput : IAudioOutput
{
    private const int BufferDurationHns = 100_000;          // 10 ms, in 100 ns units
    private const int WaitTimeoutMs = 1000;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private readonly object _lifecycle = new();
    private readonly RenderFaultPolicy _policy = new();
    private readonly string _endpointId;
    private readonly bool _followsDefault;
    private readonly SampleKind _kind;
    private readonly int _channels;
    private readonly int _bufferFrames;
    private readonly float[] _stereo;       // callback output, preallocated for the largest device buffer
    private readonly float[]? _mapped;      // channel-mapped scratch, only for integer formats
    private readonly AutoResetEvent _bufferEvent = new(false);
    private readonly ManualResetEvent _stopEvent = new(false);

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioClient? _client;
    private AudioRenderClient? _render;
    private AudioClockClient? _clock;
    private ulong _clockFrequency;
    private DeviceWatcher? _watcher;
    private IRenderCallback? _callback;
    private Thread? _thread;
    private bool _started;
    private bool _stopped;
    private WriteCursor? _cursor;
    private long _callbackFaults;
    private int _released;

    /// <param name="deviceId">Render endpoint id, or null to use (and follow) the system default device.</param>
    /// <exception cref="NotSupportedException">The device mix format is not 32-bit float or 16/24/32-bit integer PCM.</exception>
    public WasapiRenderOutput(string? deviceId)
    {
        _followsDefault = deviceId is null;
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = deviceId is null
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : _enumerator.GetDevice(deviceId);
            _endpointId = _device.ID;
            _client = _device.AudioClient;

            WaveFormat mix = _client.MixFormat;
            _kind = MixFormat.Classify(mix);
            _channels = mix.Channels;
            SampleRate = mix.SampleRate;

            _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
                BufferDurationHns, 0, mix, Guid.Empty);
            _bufferFrames = _client.BufferSize;
            _stereo = new float[_bufferFrames * 2];
            if (_kind != SampleKind.Float32) _mapped = new float[_bufferFrames * _channels];
        }
        catch
        {
            ReleaseCore();
            throw;
        }
    }

    public int SampleRate { get; }

    /// <summary>Number of render periods in which the callback threw and silence was written instead.</summary>
    public long CallbackFaults => Interlocked.Read(ref _callbackFaults);

    /// <summary>
    /// Raised at most once per instance when the device is lost or (for a default-device output) the default changes.
    /// Threading: a lost device is reported on the render thread (which then exits); a default-device change or
    /// endpoint removal is reported on a thread-pool thread, never on the Core Audio notification thread. No lock is held,
    /// so subscribers may call <see cref="Stop"/>/<see cref="Dispose"/> directly, but they should normally just set a flag.
    /// </summary>
    public event Action? DeviceInvalidated;

    /// <summary>True once the COM objects have been released (test hook).</summary>
    internal bool IsReleased => Volatile.Read(ref _released) == 1;

    public void Start(IRenderCallback cb)
    {
        ArgumentNullException.ThrowIfNull(cb);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_started) throw new InvalidOperationException("Output already started.");
            _started = true;
            _callback = cb;
            try
            {
                _client!.SetEventHandle(_bufferEvent.SafeWaitHandle.DangerousGetHandle());
                _render = _client.AudioRenderClient;
                _clock = _client.AudioClockClient;
                _clockFrequency = _clock.Frequency;

                // Event-driven streams need the buffer primed with silence before Start.
                int prime = _bufferFrames - _client.CurrentPadding;
                _render.GetBuffer(prime);
                _render.ReleaseBuffer(prime, AudioClientBufferFlags.Silent);
                _cursor = new WriteCursor(prime, prime);

                _watcher = new DeviceWatcher();
                _watcher.DefaultRenderChanged += OnDefaultRenderChanged;
                _watcher.DeviceRemoved += OnDeviceRemoved;

                _client.Start();
                _thread = new Thread(RenderLoop) { Name = "RhythmSync.WasapiRender", IsBackground = true };
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

    /// <summary>Stops rendering and releases the device. Safe from any thread, idempotent.</summary>
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
        try { _stopEvent.Set(); } catch (ObjectDisposedException) { /* render thread already released everything */ }

        if (thread is null)
        {
            ReleaseCore();
        }
        else if (thread != Thread.CurrentThread)
        {
            // The render thread releases the COM objects itself on exit; if it is stuck in the callback we
            // must not pull the device out from under it, so on timeout it cleans up whenever it returns.
            if (!thread.Join(JoinTimeout))
                Debug.WriteLine("WasapiRenderOutput: render thread did not exit within the join timeout.");
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
        try { DeviceInvalidated?.Invoke(); } catch { /* a faulty subscriber must not kill the audio or COM thread */ }
    }

    private void RenderLoop()
    {
        using IDisposable mmcss = Mmcss.JoinProAudio();
        try
        {
            WaitHandle[] handles = { _stopEvent, _bufferEvent };
            while (true)
            {
                int signalled = WaitHandle.WaitAny(handles, WaitTimeoutMs);
                if (signalled == 0) break;      // stop requested

                // A timeout still runs the period: querying the padding is how a vanished device is noticed.
                try
                {
                    RenderPeriod();
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

    private void RenderPeriod()
    {
        AudioClient client = _client!;
        int frames = _bufferFrames - client.CurrentPadding;
        if (frames <= 0) return;

        _clock!.GetPosition(out ulong position, out ulong qpcPosition);
        long devicePosition = ScaleToFrames(position, _clockFrequency, SampleRate);
        WriteCursor cursor = _cursor!;
        long writePosition = cursor.Begin(devicePosition);
        long qpcTicks = (long)qpcPosition;     // already 100 ns, same epoch as StopwatchClock

        IntPtr buffer = _render!.GetBuffer(frames);
        AudioClientBufferFlags flags = AudioClientBufferFlags.Silent;
        try
        {
            Span<float> stereo = _stereo.AsSpan(0, frames * 2);
            RenderCallback(stereo, devicePosition, qpcTicks, writePosition);
            WriteToDevice(buffer, stereo, frames);
            flags = AudioClientBufferFlags.None;
        }
        finally
        {
            // Once ReleaseBuffer succeeds the frames are committed (as silence if writing failed), so the cursor
            // advances even when an exception is propagating.
            _render.ReleaseBuffer(frames, flags);
            cursor.Commit(writePosition, frames);
        }
    }

    /// <summary>Fills <paramref name="stereo"/> via the callback in chunks of at most one second; silence if it throws.</summary>
    private void RenderCallback(Span<float> stereo, long devicePosition, long qpcTicks, long writePosition)
    {
        IRenderCallback cb = _callback!;
        try
        {
            RenderChunker.Render(cb, stereo, SampleRate, devicePosition, qpcTicks, writePosition);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _callbackFaults);
            stereo.Clear();
        }
    }

    private unsafe void WriteToDevice(IntPtr buffer, ReadOnlySpan<float> stereo, int frames)
    {
        int samples = frames * _channels;
        if (_kind == SampleKind.Float32)
        {
            var dst = new Span<float>((void*)buffer, samples);
            if (_channels == 2) stereo.CopyTo(dst);
            else ChannelMapper.StereoToDevice(stereo, dst, _channels);
            return;
        }

        ReadOnlySpan<float> mapped;
        if (_channels == 2)
        {
            mapped = stereo;
        }
        else
        {
            Span<float> scratch = _mapped.AsSpan(0, samples);
            ChannelMapper.StereoToDevice(stereo, scratch, _channels);
            mapped = scratch;
        }

        switch (_kind)
        {
            case SampleKind.Int16: PcmConverter.ToInt16(mapped, new Span<byte>((void*)buffer, samples * 2)); break;
            case SampleKind.Int24: PcmConverter.ToInt24(mapped, new Span<byte>((void*)buffer, samples * 3)); break;
            default: PcmConverter.ToInt32(mapped, new Span<byte>((void*)buffer, samples * 4)); break;
        }
    }

    /// <summary>Device frames for a clock position of <paramref name="position"/> at <paramref name="frequency"/> units per second.</summary>
    internal static long ScaleToFrames(ulong position, ulong frequency, int sampleRate)
    {
        if (frequency == 0) return 0;
        ulong whole = position / frequency;
        ulong rest = position % frequency;
        return (long)(whole * (ulong)sampleRate + rest * (ulong)sampleRate / frequency);
    }

    /// <summary>Releases every COM object exactly once. Called by the render thread on exit, or by Stop when it never ran.</summary>
    private void ReleaseCore()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1) return;
        DisposeWatcher();
        try { _client?.Stop(); } catch { /* device may already be gone */ }
        try { _client?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        try { _enumerator?.Dispose(); } catch { }
        _render = null;
        _clock = null;
        _client = null;
        _device = null;
        _enumerator = null;
        _bufferEvent.Dispose();
        _stopEvent.Dispose();
    }
}
