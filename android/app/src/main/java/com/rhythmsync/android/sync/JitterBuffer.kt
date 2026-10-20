package com.rhythmsync.android.sync

class JitterBuffer(val capacity: Int = 256) {
    class FrameSlot {
        var isPresent: Boolean = false
        var sequence: Long = -1L
        var playAt: Long = 0L
        val pcmData = ShortArray(480)

        fun clear() {
            isPresent = false
            sequence = -1L
            playAt = 0L
        }
    }

    private val slots = Array(capacity) { FrameSlot() }

    var consumedThrough: Long = -1L
        private set

    @Volatile
    var lateCount: Long = 0L
        private set

    var duplicateCount: Long = 0L
        private set

    var rejectedCount: Long = 0L
        private set

    @Volatile
    var acceptedCount: Long = 0L
        private set

    @Synchronized
    fun insert(sequence: Long, playAt: Long, pcmData: ShortArray): Boolean {
        if (sequence <= consumedThrough) {
            lateCount++
            return false
        }

        val slotIdx = (sequence % capacity).toInt()
        val slot = slots[slotIdx]

        if (slot.isPresent && slot.sequence == sequence) {
            duplicateCount++
            return false
        }

        val oldestPresent = getOldestPresentSeq()
        val newestPresent = getNewestPresentSeq()
        val anchor = if (consumedThrough >= 0) consumedThrough + 1 else oldestPresent

        if (anchor >= 0 && sequence >= anchor + capacity) {
            rejectedCount++
            return false
        }
        if (newestPresent >= 0 && newestPresent >= sequence + capacity) {
            rejectedCount++
            return false
        }

        slot.isPresent = true
        slot.sequence = sequence
        slot.playAt = playAt
        System.arraycopy(pcmData, 0, slot.pcmData, 0, minOf(pcmData.size, 480))
        acceptedCount++
        return true
    }

    @Synchronized
    fun getOldestPresentSeq(): Long {
        var minSeq = -1L
        for (i in 0 until capacity) {
            if (slots[i].isPresent) {
                if (minSeq == -1L || slots[i].sequence < minSeq) {
                    minSeq = slots[i].sequence
                }
            }
        }
        return minSeq
    }

    @Synchronized
    fun getNewestPresentSeq(): Long {
        var maxSeq = -1L
        for (i in 0 until capacity) {
            if (slots[i].isPresent) {
                if (maxSeq == -1L || slots[i].sequence > maxSeq) {
                    maxSeq = slots[i].sequence
                }
            }
        }
        return maxSeq
    }

    @Synchronized
    fun getPresentFrameCount(): Int {
        var count = 0
        for (i in 0 until capacity) {
            if (slots[i].isPresent) count++
        }
        return count
    }

    @Synchronized
    fun getFrame(sequence: Long): ShortArray? {
        val slotIdx = (sequence % capacity).toInt()
        val slot = slots[slotIdx]
        return if (slot.isPresent && slot.sequence == sequence) {
            slot.pcmData
        } else {
            null
        }
    }

    @Synchronized
    fun consume(sequence: Long) {
        if (sequence > consumedThrough) {
            val from = if (consumedThrough >= 0) consumedThrough + 1 else sequence
            for (s in from..sequence) {
                val idx = (s % capacity).toInt()
                if (slots[idx].sequence == s) {
                    slots[idx].clear()
                }
            }
            consumedThrough = sequence
        }
    }

    /**
     * Maps host time (in 100ns ticks) to stream position in fractional samples @ 48kHz.
     * Returns null if no frames are available.
     */
    @Synchronized
    fun tryMapHostTimeToPosition(hostTicks: Long): Double? {
        val presentFrames = mutableListOf<FrameSlot>()
        for (i in 0 until capacity) {
            if (slots[i].isPresent) {
                presentFrames.add(slots[i])
            }
        }
        if (presentFrames.isEmpty()) return null

        presentFrames.sortBy { it.sequence }

        // Find f: latest frame with playAt <= hostTicks
        var f: FrameSlot? = null
        for (frame in presentFrames) {
            if (frame.playAt <= hostTicks) {
                f = frame
            } else {
                break
            }
        }

        // If no frame has playAt <= hostTicks, use earliest frame
        if (f == null) {
            val first = presentFrames.first()
            val dt = hostTicks - first.playAt
            return first.sequence * 240.0 + (dt * 0.0048)
        }

        // Find g: earliest frame with seq > f.seq
        val fSeq = f.sequence
        val g = presentFrames.firstOrNull { it.sequence > fSeq }

        if (g == null) {
            // Beyond newest frame: extrapolate forward
            val dt = hostTicks - f.playAt
            return f.sequence * 240.0 + (dt * 0.0048)
        }

        // Between f and g: check latency jump / discontinuity
        val dtPlayAt = g.playAt - f.playAt
        val nominalTicks = (g.sequence - f.sequence + 1) * 50_000L

        return if (dtPlayAt > nominalTicks) {
            // Discontinuity / Latency step: advance nominally from f then hold at g's start
            val nominalPos = f.sequence * 240.0 + (hostTicks - f.playAt) * 0.0048
            val holdPos = g.sequence * 240.0
            minOf(nominalPos, holdPos)
        } else {
            // Standard linear interpolation
            val fraction = (hostTicks - f.playAt).toDouble() / dtPlayAt.toDouble()
            f.sequence * 240.0 + fraction * (g.sequence - f.sequence) * 240.0
        }
    }

    @Synchronized
    fun reset() {
        for (i in 0 until capacity) {
            slots[i].clear()
        }
        consumedThrough = -1L
        lateCount = 0L
        duplicateCount = 0L
        rejectedCount = 0L
        acceptedCount = 0L
    }
}
