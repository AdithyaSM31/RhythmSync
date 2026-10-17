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
    private enum SampleKind { Float32, Int16, Int24, Int32 }

    private const int BufferDurationHns = 100_000;          // 10 ms, in 100 ns units
    private const int WaitTimeoutMs = 1000;
    private const int MaxConsecutiveWasapiErrors = 20;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    // AUDCLNT_E_DEVICE_INVALIDATED, _RESOURCES_INVALIDATED, _SERVICE_NOT_RUNNING, HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
    private static bool IsDeviceGone(int hr) =>
        hr == unchecked((int)0x88890004) || hr == unchecked((int)0x88890026) ||
        hr == unchecked((int)0x88890010) || hr == unchecked((int)0x80070490);

    private readonly object _lifecycle = new();
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
    private long _framesWritten;
    private long _callbackFaults;
    private int _invalidatedRaised;
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
            _kind = Classify(mix);
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

    public event Action? DeviceInvalidated;

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
                _framesWritten = prime;

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

    private void OnDefaultRenderChanged()
    {
        if (_followsDefault) RaiseInvalidated();
    }

    private void OnDeviceRemoved(string id)
    {
        if (string.Equals(id, _endpointId, StringComparison.OrdinalIgnoreCase)) RaiseInvalidated();
    }

    private void DisposeWatcher()
    {
        DeviceWatcher? w = Interlocked.Exchange(ref _watcher, null);
        w?.Dispose();
    }

    /// <summary>Raises <see cref="DeviceInvalidated"/> at most once, never while holding a lock, and never after Stop.</summary>
    private void RaiseInvalidated()
    {
        if (Volatile.Read(ref _stopped)) return;
        if (Interlocked.Exchange(ref _invalidatedRaised, 1) == 1) return;
        try { DeviceInvalidated?.Invoke(); } catch { /* a faulty subscriber must not kill the audio or COM thread */ }
    }

    private void RenderLoop()
    {
        using IDisposable mmcss = Mmcss.JoinProAudio();
        try
        {
            WaitHandle[] handles = { _stopEvent, _bufferEvent };
            int consecutiveErrors = 0;
            while (true)
            {
                int signalled = WaitHandle.WaitAny(handles, WaitTimeoutMs);
                if (signalled == 0) break;      // stop requested

                // A timeout still runs the period: querying the padding is how a vanished device is noticed.
                try
                {
                    RenderPeriod();
                    consecutiveErrors = 0;
                }
                catch (COMException ex) when (IsDeviceGone(ex.HResult))
                {
                    RaiseInvalidated();
                    break;
                }
                catch (Exception)
                {
                    // Persistent unexpected WASAPI failures mean the stream is unusable: report it like a lost device.
                    if (++consecutiveErrors >= MaxConsecutiveWasapiErrors)
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
        // Normally the write cursor is ahead of the playback cursor; after an underrun it can fall behind.
        long writePosition = Math.Max(_framesWritten, devicePosition);
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
            _render.ReleaseBuffer(frames, flags);
        }
        _framesWritten = writePosition + frames;
    }

    /// <summary>Fills <paramref name="stereo"/> via the callback in chunks of at most one second; silence if it throws.</summary>
    private void RenderCallback(Span<float> stereo, long devicePosition, long qpcTicks, long writePosition)
    {
        IRenderCallback cb = _callback!;
        int totalFrames = stereo.Length / 2;
        try
        {
            for (int done = 0; done < totalFrames; )
            {
                int n = Math.Min(totalFrames - done, SampleRate);
                cb.Render(stereo.Slice(done * 2, n * 2), new RenderTiming(devicePosition, qpcTicks, writePosition + done));
                done += n;
            }
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

    private static SampleKind Classify(WaveFormat format)
    {
        WaveFormatEncoding encoding = format.Encoding;
        if (format is WaveFormatExtensible ext)
            encoding = ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT ? WaveFormatEncoding.IeeeFloat
                     : ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_PCM ? WaveFormatEncoding.Pcm
                     : WaveFormatEncoding.Unknown;

        return (encoding, format.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => SampleKind.Float32,
            (WaveFormatEncoding.Pcm, 16) => SampleKind.Int16,
            (WaveFormatEncoding.Pcm, 24) => SampleKind.Int24,
            (WaveFormatEncoding.Pcm, 32) => SampleKind.Int32,
            _ => throw new NotSupportedException(
                $"Unsupported device mix format ({encoding}, {format.BitsPerSample}-bit); supported: 32-bit float and 16/24/32-bit PCM."),
        };
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
