package com.rhythmsync.android.dsp

import kotlin.math.exp

class EqProcessor(
    val sampleRate: Int = AudioFormat.SAMPLE_RATE,
    val channels: Int = AudioFormat.CHANNELS
) {
    companion object {
        const val SUB_BLOCK_FRAMES = 32
        const val TAU_GAIN = 0.010 // 10 ms
        const val TAU_PREAMP_FALL = 0.005 // 5 ms
        const val TAU_PREAMP_RISE = 0.020 // 20 ms
        const val DENORMAL_THRESHOLD = 1e-30
    }

    init {
        require(channels in 1..2) { "Only mono (1) or stereo (2) channels are supported (got $channels)" }
    }

    private val targetGainsDb = FloatArray(EqBands.BAND_COUNT)
    private val currentGainsDb = DoubleArray(EqBands.BAND_COUNT)

    // Biquad state: s1[band][ch], s2[band][ch]
    private val s1 = Array(EqBands.BAND_COUNT) { DoubleArray(channels) }
    private val s2 = Array(EqBands.BAND_COUNT) { DoubleArray(channels) }

    var currentPreampDb: Double = 0.0
        private set

    var targetPreampDb: Double = 0.0
        private set

    var currentPreampLinear: Double = 1.0
        private set

    var targetPreampLinear: Double = 1.0
        private set

    var targetPeakDb: Double = 0.0
        private set

    @Synchronized
    fun setGains(gains: FloatArray) {
        require(gains.size == EqBands.BAND_COUNT) {
            "Expected ${EqBands.BAND_COUNT} band gains, got ${gains.size}"
        }
        for (i in 0 until EqBands.BAND_COUNT) {
            targetGainsDb[i] = EqBands.snapGainDb(gains[i])
        }
        targetPeakDb = EqCurve.calculatePeakDb(targetGainsDb, sampleRate)
        targetPreampDb = EqCurve.calculatePreampDb(targetPeakDb)
        targetPreampLinear = EqCurve.calculatePreampLinear(targetPreampDb)
    }

    @Synchronized
    fun setGainsImmediate(gains: FloatArray) {
        setGains(gains)
        for (i in 0 until EqBands.BAND_COUNT) {
            currentGainsDb[i] = targetGainsDb[i].toDouble()
        }
        currentPreampDb = targetPreampDb
        currentPreampLinear = targetPreampLinear
        clearState()
    }

    @Synchronized
    fun getTargetGains(): FloatArray = targetGainsDb.clone()

    @Synchronized
    fun getCurrentGains(): FloatArray = FloatArray(EqBands.BAND_COUNT) { currentGainsDb[it].toFloat() }

    @Synchronized
    fun clearState() {
        for (i in 0 until EqBands.BAND_COUNT) {
            s1[i].fill(0.0)
            s2[i].fill(0.0)
        }
    }

    /**
     * Processes interleaved audio buffer of length [totalFrames] in-place.
     * [buffer] must have size >= totalFrames * channels.
     */
    @Synchronized
    fun process(buffer: FloatArray, totalFrames: Int) {
        // Check for bit-exact bypass
        val isTargetFlat = targetGainsDb.all { Math.abs(it) < 0.001f }
        val isCurrentFlat = currentGainsDb.all { Math.abs(it) < 0.001 }
        val isPreampOne = Math.abs(currentPreampLinear - 1.0) < 0.0001 && Math.abs(targetPreampLinear - 1.0) < 0.0001

        if (isTargetFlat && isCurrentFlat && isPreampOne) {
            clearState()
            return // Bit-exact bypass: buffer untouched!
        }

        var frameOffset = 0
        while (frameOffset < totalFrames) {
            val subBlock = minOf(SUB_BLOCK_FRAMES, totalFrames - frameOffset)
            processSubBlock(buffer, frameOffset, subBlock)
            frameOffset += subBlock
        }

        // Flush denormals
        flushDenormals()
    }

    private fun processSubBlock(buffer: FloatArray, frameOffset: Int, n: Int) {
        // 1. Update band gains and check if any band is moving
        var anyBandMoving = false
        val kGain = 1.0 - exp(-n.toDouble() / (TAU_GAIN * sampleRate.toDouble()))

        val startCoeffs = Array(EqBands.BAND_COUNT) { BiquadCoefficients() }
        val endCoeffs = Array(EqBands.BAND_COUNT) { BiquadCoefficients() }
        val isBandMoving = BooleanArray(EqBands.BAND_COUNT)

        for (i in 0 until EqBands.BAND_COUNT) {
            val f0 = EqBands.FREQUENCIES[i]
            val active = EqBands.isActive(i, sampleRate)
            val target = if (active) targetGainsDb[i].toDouble() else 0.0
            val cur = currentGainsDb[i]

            startCoeffs[i] = EqCurve.calculatePeakingBiquad(f0, sampleRate, cur)

            if (Math.abs(target - cur) > 0.01) {
                anyBandMoving = true
                isBandMoving[i] = true
                var next = cur + (target - cur) * kGain
                if (Math.abs(target - next) <= 0.01) {
                    next = target
                }
                currentGainsDb[i] = next
                endCoeffs[i] = EqCurve.calculatePeakingBiquad(f0, sampleRate, next)
            } else {
                currentGainsDb[i] = target
                endCoeffs[i] = startCoeffs[i]
            }
        }

        // 2. Preamp smoothing with asymmetric rise/fall and resonance gating
        val startPreamp = currentPreampLinear
        val goalDb = targetPreampDb
        if (currentPreampDb != goalDb && (goalDb < currentPreampDb || !anyBandMoving)) {
            val kPreamp = if (goalDb < currentPreampDb) {
                1.0 - exp(-n.toDouble() / (TAU_PREAMP_FALL * sampleRate.toDouble()))
            } else {
                1.0 - exp(-n.toDouble() / (TAU_PREAMP_RISE * sampleRate.toDouble()))
            }
            currentPreampDb += (goalDb - currentPreampDb) * kPreamp
            if (Math.abs(goalDb - currentPreampDb) <= 0.01) {
                currentPreampDb = goalDb
                currentPreampLinear = targetPreampLinear
            } else {
                currentPreampLinear = Math.pow(10.0, currentPreampDb / 20.0)
            }
        }
        val endPreamp = currentPreampLinear

        // 3. Process each frame in subblock
        for (t in 0 until n) {
            val progress = if (n > 1) t.toDouble() / (n - 1) else 1.0
            val preamp = startPreamp + (endPreamp - startPreamp) * progress

            for (c in 0 until channels) {
                val idx = (frameOffset + t) * channels + c
                var sample = buffer[idx].toDouble()

                // Cascade through the 10 bands
                for (b in 0 until EqBands.BAND_COUNT) {
                    val b0: Double
                    val b1: Double
                    val b2: Double
                    val a1: Double
                    val a2: Double

                    if (isBandMoving[b]) {
                        val s = startCoeffs[b]
                        val e = endCoeffs[b]
                        b0 = s.b0 + (e.b0 - s.b0) * progress
                        b1 = s.b1 + (e.b1 - s.b1) * progress
                        b2 = s.b2 + (e.b2 - s.b2) * progress
                        a1 = s.a1 + (e.a1 - s.a1) * progress
                        a2 = s.a2 + (e.a2 - s.a2) * progress
                    } else {
                        val s = startCoeffs[b]
                        b0 = s.b0
                        b1 = s.b1
                        b2 = s.b2
                        a1 = s.a1
                        a2 = s.a2
                    }

                    // Transposed Direct Form II
                    val y = b0 * sample + s1[b][c]
                    s1[b][c] = b1 * sample - a1 * y + s2[b][c]
                    s2[b][c] = b2 * sample - a2 * y
                    sample = y
                }

                val out = (sample * preamp).toFloat()
                buffer[idx] = if (out > 1.0f) 1.0f else if (out < -1.0f) -1.0f else out
            }
        }
    }

    private fun flushDenormals() {
        for (b in 0 until EqBands.BAND_COUNT) {
            for (c in 0 until channels) {
                if (Math.abs(s1[b][c]) < DENORMAL_THRESHOLD) s1[b][c] = 0.0
                if (Math.abs(s2[b][c]) < DENORMAL_THRESHOLD) s2[b][c] = 0.0
            }
        }
    }
}
