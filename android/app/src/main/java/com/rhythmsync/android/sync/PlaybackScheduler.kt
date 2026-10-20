package com.rhythmsync.android.sync

import kotlin.math.floor

class PlaybackScheduler(
    val deviceSampleRate: Int = 48000,
    var targetLatencyMs: Int = 200
) {
    companion object {
        const val HARD_RESYNC_MS = 20.0
        const val HARD_RESYNC_SAMPLES = 960.0 // 20ms @ 48kHz
        const val OFFSET_RESYNC_MS = 0.25
        const val OFFSET_RESYNC_SAMPLES = 12.0 // 0.25ms @ 48kHz

        const val KP = 1.0e-5
        const val KI = 2.0e-8
        const val MAX_CORRECTION = 500.0e-6 // 500 ppm = 0.0005
        const val MAX_INTEGRAL = MAX_CORRECTION / KI // 25,000 samples

        const val SPLICE_FRAMES = 48
        const val MAX_CONCEAL_FRAMES = 10
    }

    var readPosition: Double = 0.0
        private set

    var integral: Double = 0.0
        private set

    var lastSyncError: Double = 0.0
        private set

    var resyncCount: Long = 0L
        private set

    var isAwaitingContent: Boolean = false
        private set

    /** Frames concealed since construction (cumulative; read by the heartbeat to compute loss over a window). */
    @Volatile
    var concealedFrames: Long = 0L
        private set

    var currentOffsetMs: Double = 0.0
        private set

    private var previousOffsetMs: Double = 0.0
    private var offsetJumped: Boolean = false

    // Concealer state
    private val lastGoodFrame = FloatArray(480)
    private var hasGoodFrame: Boolean = false
    private var consecutiveLostFrames: Int = 0
    private var lastLeftOut: Float = 0f
    private var lastRightOut: Float = 0f
    private var needsFadeInAfterOutage: Boolean = false

    fun setOffsetMs(offset: Double) {
        val lowerBound = maxOf(-100.0, -(targetLatencyMs - 50.0))
        val effectiveOffset = offset.coerceIn(lowerBound, 100.0)
        val delta = Math.abs(effectiveOffset - currentOffsetMs)
        if (delta > OFFSET_RESYNC_MS) {
            offsetJumped = true
        }
        previousOffsetMs = currentOffsetMs
        currentOffsetMs = effectiveOffset
    }

    /**
     * Executes one step of the PI drift controller for tracking error [err] in stream samples.
     * Returns the resampler step ratio (e.g. ~1.0).
     */
    fun updateDriftController(err: Double): Double {
        lastSyncError = err

        // When tracking error is zero, gracefully decay the accumulator towards nominal (convergence)
        val decayedIntegral = if (Math.abs(err) < 1e-9) integral * 0.999 else integral
        integral = (decayedIntegral + err).coerceIn(-MAX_INTEGRAL, MAX_INTEGRAL)

        val raw = KP * err + KI * integral
        val corr = raw.coerceIn(-MAX_CORRECTION, MAX_CORRECTION)
        val nominalRatio = 48000.0 / deviceSampleRate.toDouble()
        return nominalRatio * (1.0 + corr)
    }

    /**
     * Checks whether a hard resync is needed based on sync error or offset jumps.
     * If needed, adjusts [readPosition] and internal states, returning true.
     */
    fun checkAndPerformResync(targetPosition: Double): Boolean {
        val err = targetPosition - readPosition
        val needsOffsetResync = offsetJumped && Math.abs(err) > OFFSET_RESYNC_SAMPLES
        val needsHardResync = Math.abs(err) > HARD_RESYNC_SAMPLES

        if (needsOffsetResync || needsHardResync) {
            resyncCount++
            offsetJumped = false
            integral = 0.0

            if (err > 0.0) {
                // Client is far behind: jump ahead to target
                readPosition = targetPosition
                isAwaitingContent = false
                resetConcealer()
            } else {
                // Client is far ahead: wait for content
                isAwaitingContent = true
                readPosition = targetPosition
            }
            return true
        }
        return false
    }

    fun stepReadPosition(sampleDelta: Double) {
        readPosition += sampleDelta
    }

    /**
     * Catmull-Rom (cubic Hermite) interpolation for a single channel.
     * Four surrounding points p0, p1, p2, p3 with fractional delta x in [0.0, 1.0).
     */
    fun catmullRom(p0: Float, p1: Float, p2: Float, p3: Float, x: Float): Float {
        val x2 = x * x
        val x3 = x2 * x
        return p1 + 0.5f * (
                (p2 - p0) * x +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * x2 +
                (3f * (p1 - p2) + p3 - p0) * x3
        )
    }

    /**
     * Resamples interleaved stereo PCM float data using Catmull-Rom cubic interpolation.
     * [input] must contain at least 4 stereo frames (8 floats) for boundary padding.
     */
    fun resampleStereo(
        input: FloatArray,
        inputOffsetFrames: Int,
        startFractionalFrame: Double,
        stepRatio: Double,
        output: FloatArray,
        outputFrames: Int
    ) {
        for (k in 0 until outputFrames) {
            val pos = startFractionalFrame + k * stepRatio
            val i = floor(pos).toInt()
            val x = (pos - i).toFloat()

            val base = (inputOffsetFrames + i) * 2

            // Left channel
            val l0 = input[base - 2]
            val l1 = input[base]
            val l2 = input[base + 2]
            val l3 = input[base + 4]
            output[k * 2] = catmullRom(l0, l1, l2, l3, x)

            // Right channel
            val r0 = input[base - 1]
            val r1 = input[base + 1]
            val r2 = input[base + 3]
            val r3 = input[base + 5]
            output[k * 2 + 1] = catmullRom(r0, r1, r2, r3, x)
        }
    }

    /**
     * Records a valid received frame (480 floats) for future concealment lookbehind.
     */
    fun recordGoodFrame(frame: FloatArray) {
        System.arraycopy(frame, 0, lastGoodFrame, 0, minOf(frame.size, 480))
        hasGoodFrame = true
        consecutiveLostFrames = 0
        lastLeftOut = frame[478]
        lastRightOut = frame[479]
    }

    /**
     * Conceals a missing 240-frame (480 floats) audio frame using gain decay and splicing.
     */
    fun concealMissingFrame(output: FloatArray) {
        concealedFrames++
        consecutiveLostFrames++
        val m = consecutiveLostFrames

        if (!hasGoodFrame || m > MAX_CONCEAL_FRAMES) {
            // Over 10 frames lost: pure silence
            output.fill(0f)
            needsFadeInAfterOutage = true
            return
        }

        val startGain = (1.0 - (m - 1) / 10.0).toFloat()
        val endGain = (1.0 - m / 10.0).toFloat()

        for (i in 0 until 240) {
            val progress = if (i == 239) 1f else i / 239f
            val gain = startGain + (endGain - startGain) * progress

            var l = lastGoodFrame[i * 2] * gain
            var r = lastGoodFrame[i * 2 + 1] * gain

            // 48-sample splice from previous output
            if (i < SPLICE_FRAMES) {
                val w = i / SPLICE_FRAMES.toFloat()
                l = (1f - w) * lastLeftOut + w * l
                r = (1f - w) * lastRightOut + w * r
            }

            output[i * 2] = l
            output[i * 2 + 1] = r
        }

        lastLeftOut = output[478]
        lastRightOut = output[479]
    }

    /**
     * Applies fade-in (0.0 to 1.0) if returning from extended loss.
     */
    fun applyRecoveryFadeInIfNeeded(frame: FloatArray) {
        if (needsFadeInAfterOutage) {
            for (i in 0 until 240) {
                val gain = i / 240f
                frame[i * 2] *= gain
                frame[i * 2 + 1] *= gain
            }
            needsFadeInAfterOutage = false
        }
    }

    fun resetConcealer() {
        hasGoodFrame = false
        consecutiveLostFrames = 0
        lastLeftOut = 0f
        lastRightOut = 0f
        needsFadeInAfterOutage = false
    }

    fun reset() {
        readPosition = 0.0
        integral = 0.0
        lastSyncError = 0.0
        resyncCount = 0L
        isAwaitingContent = false
        currentOffsetMs = 0.0
        previousOffsetMs = 0.0
        offsetJumped = false
        resetConcealer()
    }
}
