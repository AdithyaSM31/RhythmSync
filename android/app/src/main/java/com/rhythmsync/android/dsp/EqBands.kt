package com.rhythmsync.android.dsp

object EqBands {
    const val BAND_COUNT = 10
    const val Q = 1.41
    const val MAX_GAIN_DB = 12.0f
    const val MIN_GAIN_DB = -12.0f
    const val STEP_DB = 0.5f

    // Octave band centers: 1000 * 2^(k - 5) Hz
    val FREQUENCIES = doubleArrayOf(
        31.25,
        62.5,
        125.0,
        250.0,
        500.0,
        1000.0,
        2000.0,
        4000.0,
        8000.0,
        16000.0
    )

    val LABELS = listOf(
        "31",
        "63",
        "125",
        "250",
        "500",
        "1k",
        "2k",
        "4k",
        "8k",
        "16k"
    )

    fun snapGainDb(gainDb: Float): Float {
        val clamped = gainDb.coerceIn(MIN_GAIN_DB, MAX_GAIN_DB)
        val snapped = Math.round(clamped / STEP_DB) * STEP_DB
        return snapped + 0.0f // Normalizes -0.0f to +0.0f
    }

    fun isActive(bandIndex: Int, sampleRate: Int): Boolean {
        if (bandIndex !in 0 until BAND_COUNT) return false
        return FREQUENCIES[bandIndex] < 0.45 * sampleRate
    }
}
