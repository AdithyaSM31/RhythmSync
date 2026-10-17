namespace RhythmSync.Core.Client;

/// <summary>
/// Client-side store of received 5 ms audio frames keyed by sequence number, plus the mapping from
/// host time to stream position. The network thread calls <see cref="Insert"/>; the audio thread calls
/// <see cref="TryReadFrame"/>, <see cref="TryMapHostTimeToPosition"/> and <see cref="MarkConsumedThrough"/>.
/// All storage is preallocated; none of those calls allocates. Wrap-around of the u32 sequence number
/// (after roughly 248 days of streaming) is not supported.
/// </summary>
public sealed class JitterBuffer
{
    private const double StreamSamplesPerTick = (double)AudioFormat.SampleRate / AudioFormat.TicksPerSecond;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly short[][] _pcm;
    private readonly long[] _playAt;
    private readonly uint[] _seq;
    private readonly bool[] _present;
    private int _count;

    private long _consumedThrough = -1;          // -1: nothing consumed yet
    private bool _hasRef;                        // retained playAt of the highest consumed frame that was present
    private uint _refSeq;
    private long _refPlayAt;

    private long _received, _duplicates, _late, _rejected;

    public JitterBuffer(int capacityFrames = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        _capacity = capacityFrames;
        _pcm = new short[capacityFrames][];
        for (int i = 0; i < capacityFrames; i++) _pcm[i] = new short[AudioFormat.FrameFloats];
        _playAt = new long[capacityFrames];
        _seq = new uint[capacityFrames];
        _present = new bool[capacityFrames];
    }

    public long Received { get { lock (_gate) return _received; } }
    public long Duplicates { get { lock (_gate) return _duplicates; } }
    public long Late { get { lock (_gate) return _late; } }
    public long Rejected { get { lock (_gate) return _rejected; } }

    /// <summary>Stores a frame. Returns false (and counts exactly one of Late/Duplicates/Rejected) if refused.</summary>
    public bool Insert(uint seq, long playAt, ReadOnlySpan<short> pcm)
    {
        if (pcm.Length != AudioFormat.FrameFloats)
            throw new ArgumentException($"pcm must hold {AudioFormat.FrameFloats} samples.", nameof(pcm));

        lock (_gate)
        {
            if (seq <= _consumedThrough) { _late++; return false; }

            int slot = (int)(seq % (uint)_capacity);
            if (_present[slot] && _seq[slot] == seq) { _duplicates++; return false; }

            // Anchor = first unconsumed seq, or the oldest present frame if nothing was consumed. The new frame must
            // lie inside [anchor, anchor + capacity), and the newest present frame must be within `capacity` of it,
            // so that no two live frames ever share a ring slot. The very first frame is always accepted.
            long anchor = seq;
            long newest = seq;
            if (_count > 0)
            {
                long oldestPresent = long.MaxValue;
                newest = long.MinValue;
                for (int i = 0; i < _capacity; i++)
                {
                    if (!_present[i]) continue;
                    if (_seq[i] < oldestPresent) oldestPresent = _seq[i];
                    if (_seq[i] > newest) newest = _seq[i];
                }
                anchor = oldestPresent;
            }
            if (_consumedThrough >= 0) anchor = _consumedThrough + 1;

            if (seq >= anchor + _capacity || newest >= seq + _capacity) { _rejected++; return false; }

            _seq[slot] = seq;
            _playAt[slot] = playAt;
            pcm.CopyTo(_pcm[slot]);
            _present[slot] = true;
            _count++;
            _received++;
            return true;
        }
    }

    /// <summary>Copies frame <paramref name="seq"/> to <paramref name="dst"/> as floats (sample / 32768).</summary>
    public bool TryReadFrame(uint seq, Span<float> dst)
    {
        if (dst.Length != AudioFormat.FrameFloats)
            throw new ArgumentException($"dst must hold {AudioFormat.FrameFloats} samples.", nameof(dst));

        lock (_gate)
        {
            int slot = (int)(seq % (uint)_capacity);
            if (!_present[slot] || _seq[slot] != seq) return false;
            short[] src = _pcm[slot];
            for (int i = 0; i < src.Length; i++) dst[i] = src[i] / 32768f;
            return true;
        }
    }

    /// <summary>True if frame <paramref name="seq"/> is stored and not yet consumed.</summary>
    public bool Contains(uint seq)
    {
        lock (_gate)
        {
            int slot = (int)(seq % (uint)_capacity);
            return _present[slot] && _seq[slot] == seq;
        }
    }

    /// <summary>Frees every frame with sequence number up to and including <paramref name="seq"/> and remembers the last one's play time.</summary>
    public void MarkConsumedThrough(uint seq)
    {
        lock (_gate)
        {
            if (seq <= _consumedThrough) return;
            _consumedThrough = seq;

            bool found = false;
            uint bestSeq = 0;
            long bestPlayAt = 0;
            for (int i = 0; i < _capacity; i++)
            {
                if (!_present[i] || _seq[i] > seq) continue;
                if (!found || _seq[i] > bestSeq) { found = true; bestSeq = _seq[i]; bestPlayAt = _playAt[i]; }
                _present[i] = false;
                _count--;
            }
            if (found && (!_hasRef || bestSeq >= _refSeq))
            {
                _hasRef = true;
                _refSeq = bestSeq;
                _refPlayAt = bestPlayAt;
            }
        }
    }

    /// <summary>
    /// Maps a host time to a (fractional) stream sample position. Uses the latest reference frame whose play time
    /// is at or before <paramref name="hostTicks"/>, interpolating to the next present frame or extrapolating at the
    /// nominal rate; before every reference it extrapolates backward from the earliest. If the next present frame's
    /// play time is more than one frame later than nominal spacing implies (a latency jump), the gap is a
    /// discontinuity: the position advances at the nominal rate and then holds at the next frame's start until it
    /// is due, so the mapping never decreases. False only if no frame has ever been seen.
    /// </summary>
    public bool TryMapHostTimeToPosition(long hostTicks, out double streamSample)
    {
        lock (_gate)
        {
            bool hasF = false, hasE = false;
            uint fSeq = 0, eSeq = 0;
            long fPlayAt = 0, ePlayAt = 0;

            if (_hasRef)
            {
                hasE = true; eSeq = _refSeq; ePlayAt = _refPlayAt;
                if (_refPlayAt <= hostTicks) { hasF = true; fSeq = _refSeq; fPlayAt = _refPlayAt; }
            }
            for (int i = 0; i < _capacity; i++)
            {
                if (!_present[i]) continue;
                uint s = _seq[i];
                if (!hasE || s < eSeq) { hasE = true; eSeq = s; ePlayAt = _playAt[i]; }
                if (_playAt[i] <= hostTicks && (!hasF || s > fSeq)) { hasF = true; fSeq = s; fPlayAt = _playAt[i]; }
            }

            if (!hasE) { streamSample = 0; return false; }
            if (!hasF) { streamSample = Extrapolate(eSeq, ePlayAt, hostTicks); return true; }

            bool hasG = false;
            uint gSeq = 0;
            long gPlayAt = 0;
            for (int i = 0; i < _capacity; i++)
            {
                if (!_present[i] || _seq[i] <= fSeq) continue;
                if (!hasG || _seq[i] < gSeq) { hasG = true; gSeq = _seq[i]; gPlayAt = _playAt[i]; }
            }

            if (hasG && gPlayAt - fPlayAt > ((long)gSeq - fSeq) * AudioFormat.FrameTicks + AudioFormat.FrameTicks)
            {
                // playAt jumped (host latency change): play f on at the nominal rate, then hold at g's start until g is due.
                streamSample = Math.Min(Extrapolate(fSeq, fPlayAt, hostTicks), (long)gSeq * AudioFormat.FrameSamples);
            }
            else if (hasG && gPlayAt > fPlayAt)
            {
                double fraction = (double)(hostTicks - fPlayAt) / (gPlayAt - fPlayAt);
                streamSample = (long)fSeq * AudioFormat.FrameSamples
                               + fraction * ((long)gSeq - fSeq) * AudioFormat.FrameSamples;
            }
            else
            {
                streamSample = Extrapolate(fSeq, fPlayAt, hostTicks);
            }
            return true;
        }
    }

    private static double Extrapolate(uint seq, long playAt, long hostTicks) =>
        (long)seq * AudioFormat.FrameSamples + (hostTicks - playAt) * StreamSamplesPerTick;
}
