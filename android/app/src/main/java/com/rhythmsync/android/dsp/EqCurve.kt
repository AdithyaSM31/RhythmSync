package com.rhythmsync.android.dsp

data class BiquadCoefficients(
    val b0: Double = 1.0,
    val b1: Double = 0.0,
    val b2: Double = 0.0,
    val a1: Double = 0.0,
    val a2: Double = 0.0
)

object EqCurve {
    /**
     * Calculates Robert Bristow-Johnson (RBJ) peaking filter coefficients normalized by a0.
     */
    fun calculatePeakingBiquad(
        f0: Double,
        sampleRate: Int,
        gainDb: Double,
        q: Double = EqBands.Q
    ): BiquadCoefficients {
        if (f0 >= 0.45 * sampleRate || Math.abs(gainDb) < 1e-6) {
            return BiquadCoefficients(1.0, 0.0, 0.0, 0.0, 0.0)
        }

        val a = Math.pow(10.0, gainDb / 40.0)
        val w0 = 2.0 * Math.PI * f0 / sampleRate.toDouble()
        val sinW0 = Math.sin(w0)
        val cosW0 = Math.cos(w0)
        val alpha = sinW0 / (2.0 * q)
        val a0 = 1.0 + alpha / a

        val b0 = (1.0 + alpha * a) / a0
        val b1 = (-2.0 * cosW0) / a0
        val b2 = (1.0 - alpha * a) / a0
        val a1 = (-2.0 * cosW0) / a0
        val a2 = (1.0 - alpha / a) / a0

        return BiquadCoefficients(b0, b1, b2, a1, a2)
    }

    /**
     * Evaluates total dB response across cascaded biquads at frequency [f].
     */
    fun evaluateResponseDb(biquads: List<BiquadCoefficients>, f: Double, sampleRate: Int): Double {
        val w = 2.0 * Math.PI * f / sampleRate.toDouble()
        val cosW = Math.cos(w)
        val sinW = Math.sin(w)
        val cos2W = Math.cos(2.0 * w)
        val sin2W = Math.sin(2.0 * w)

        var totalDb = 0.0
        for (b in biquads) {
            val nRe = b.b0 + b.b1 * cosW + b.b2 * cos2W
            val nIm = -(b.b1 * sinW + b.b2 * sin2W)
            val dRe = 1.0 + b.a1 * cosW + b.a2 * cos2W
            val dIm = -(b.a1 * sinW + b.a2 * sin2W)

            val numMag2 = nRe * nRe + nIm * nIm
            val denMag2 = dRe * dRe + dIm * dIm
            if (denMag2 > 0.0 && numMag2 > 0.0) {
                totalDb += 10.0 * Math.log10(numMag2 / denMag2)
            }
        }
        return totalDb
    }

    /**
     * Finds the peak dB response of the 10-band equalizer curve using a grid sweep
     * and golden-section search over local maxima.
     */
    fun calculatePeakDb(gainsDb: FloatArray, sampleRate: Int): Double {
        val hasPositive = gainsDb.any { it > 0.001f }
        if (!hasPositive) return 0.0

        val biquads = (0 until EqBands.BAND_COUNT).map { i ->
            calculatePeakingBiquad(EqBands.FREQUENCIES[i], sampleRate, gainsDb[i].toDouble())
        }

        val minFreq = 20.0
        val maxFreq = minOf(20000.0, 0.45 * sampleRate)
        val gridPoints = 601
        val logMin = Math.log(minFreq)
        val logMax = Math.log(maxFreq)

        val frequencies = DoubleArray(gridPoints)
        val responses = DoubleArray(gridPoints)

        var globalPeakDb = 0.0

        for (i in 0 until gridPoints) {
            val f = Math.exp(logMin + i * (logMax - logMin) / (gridPoints - 1))
            frequencies[i] = f
            val resp = evaluateResponseDb(biquads, f, sampleRate)
            responses[i] = resp
            if (resp > globalPeakDb) {
                globalPeakDb = resp
            }
        }

        for (i in 0 until EqBands.BAND_COUNT) {
            val f = EqBands.FREQUENCIES[i]
            if (f in minFreq..maxFreq) {
                val r = evaluateResponseDb(biquads, f, sampleRate)
                if (r > globalPeakDb) globalPeakDb = r
            }
        }

        // Golden-Section Search on local maxima
        val invPhi = 0.6180339887498949
        for (i in 1 until gridPoints - 1) {
            if (responses[i] > responses[i - 1] && responses[i] >= responses[i + 1] && responses[i] > 0.0) {
                var a = Math.log(frequencies[i - 1])
                var b = Math.log(frequencies[i + 1])
                var c = b - (b - a) * invPhi
                var d = a + (b - a) * invPhi

                var fc = evaluateResponseDb(biquads, Math.exp(c), sampleRate)
                var fd = evaluateResponseDb(biquads, Math.exp(d), sampleRate)

                for (iter in 0 until 40) {
                    if (fc > fd) {
                        b = d
                        d = c
                        fd = fc
                        c = b - (b - a) * invPhi
                        fc = evaluateResponseDb(biquads, Math.exp(c), sampleRate)
                    } else {
                        a = c
                        c = d
                        fc = fd
                        d = a + (b - a) * invPhi
                        fd = evaluateResponseDb(biquads, Math.exp(d), sampleRate)
                    }
                }
                val peak = maxOf(fc, fd)
                if (peak > globalPeakDb) {
                    globalPeakDb = peak
                }
            }
        }

        return globalPeakDb
    }

    fun calculatePreampDb(peakDb: Double): Double = if (peakDb > 0.0) -peakDb else 0.0

    fun calculatePreampLinear(preampDb: Double): Double = Math.pow(10.0, preampDb / 20.0)
}
