using System.Runtime.InteropServices;
using RhythmSync.Core.Audio;

namespace RhythmSync.Platform.Windows;

/// <summary>
/// Turns one raw capture packet (device mix format) into interleaved stereo float and hands it to a <see cref="CaptureHandler"/>.
/// Preallocates every buffer for <c>capacityFrames</c>; a larger packet is delivered in several chunks, so processing never allocates.
/// Single-threaded: used only by the capture thread.
/// </summary>
internal sealed class LoopbackPacketProcessor
{
    private const long TicksPerSecond = 10_000_000;

    private readonly SampleKind _kind;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _capacityFrames;
    private readonly int _bytesPerFrame;
    private readonly float[] _stereo;
    private readonly float[]? _decoded;        // integer formats only
    private long _handlerFaults;

    public LoopbackPacketProcessor(SampleKind kind, int channels, int sampleRate, int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        _kind = kind;
        _channels = channels;
        _sampleRate = sampleRate;
        _capacityFrames = capacityFrames;
        _bytesPerFrame = channels * MixFormat.BytesPerSample(kind);
        _stereo = new float[capacityFrames * 2];
        if (kind != SampleKind.Float32) _decoded = new float[capacityFrames * channels];
    }

    /// <summary>Number of handler invocations that threw (the audio was dropped for that chunk, the packet carried on).</summary>
    public long HandlerFaults => Interlocked.Read(ref _handlerFaults);

    /// <summary>
    /// Delivers <paramref name="frames"/> frames from <paramref name="raw"/>. <paramref name="devicePosition"/> and
    /// <paramref name="qpcTicks"/> describe the first frame and are passed unchanged for the first chunk; for later
    /// chunks the position advances by the frames already delivered and the QPC by their duration. A silent packet is delivered as zeros.
    /// </summary>
    public void Process(ReadOnlySpan<byte> raw, int frames, bool silent, long devicePosition, long qpcTicks, CaptureHandler handler)
    {
        if (raw.Length < (long)frames * _bytesPerFrame) throw new ArgumentException("Packet shorter than its frame count.", nameof(raw));

        for (int offset = 0; offset < frames; offset += _capacityFrames)
        {
            int n = Math.Min(frames - offset, _capacityFrames);
            Span<float> stereo = _stereo.AsSpan(0, n * 2);

            if (silent) stereo.Clear();
            else Convert(raw.Slice(offset * _bytesPerFrame, n * _bytesPerFrame), n, stereo);

            try
            {
                handler(stereo, devicePosition + offset, qpcTicks + offset * TicksPerSecond / _sampleRate);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _handlerFaults);
            }
        }
    }

    private void Convert(ReadOnlySpan<byte> raw, int frames, Span<float> stereo)
    {
        int samples = frames * _channels;
        if (_kind == SampleKind.Float32)
        {
            Downmixer.ToStereo(MemoryMarshal.Cast<byte, float>(raw.Slice(0, samples * 4)), _channels, stereo);
            return;
        }

        Span<float> decoded = _decoded.AsSpan(0, samples);
        switch (_kind)
        {
            case SampleKind.Int16: PcmConverter.FromInt16(raw, decoded); break;
            case SampleKind.Int24: PcmConverter.FromInt24(raw, decoded); break;
            default: PcmConverter.FromInt32(raw, decoded); break;
        }
        Downmixer.ToStereo(decoded, _channels, stereo);
    }
}
