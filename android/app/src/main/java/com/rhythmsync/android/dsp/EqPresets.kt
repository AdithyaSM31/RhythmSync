package com.rhythmsync.android.dsp

object EqPresets {
    val FLAT = floatArrayOf(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f)
    val LAPTOP_SPEAKERS = floatArrayOf(-9f, -6f, -1f, 2f, 1f, 0f, 0f, 1f, 2f, 1f)
    val BASS_BOOST = floatArrayOf(6f, 5f, 4f, 2f, 0f, 0f, 0f, 0f, 0f, 0f)
    val TREBLE_CUT = floatArrayOf(0f, 0f, 0f, 0f, 0f, 0f, -1f, -3f, -5f, -6f)

    val PRESET_NAMES = listOf(
        "Flat",
        "Laptop speakers",
        "Bass boost",
        "Treble cut"
    )

    fun getGainsForPreset(name: String): FloatArray? {
        return when (name.lowercase().trim()) {
            "flat" -> FLAT.clone()
            "laptop speakers", "laptop", "phone speakers" -> LAPTOP_SPEAKERS.clone()
            "bass boost" -> BASS_BOOST.clone()
            "treble cut" -> TREBLE_CUT.clone()
            else -> null
        }
    }

    fun isFlat(gains: FloatArray): Boolean {
        if (gains.size != EqBands.BAND_COUNT) return false
        for (g in gains) {
            if (Math.abs(g) > 0.001f) return false
        }
        return true
    }
}
