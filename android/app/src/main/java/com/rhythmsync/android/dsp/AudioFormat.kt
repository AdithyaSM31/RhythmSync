package com.rhythmsync.android.dsp

import java.util.Random

object AudioFormat {
    const val SAMPLE_RATE = 48000
    const val CHANNELS = 2
    const val FRAME_SAMPLES = 240 // 5 ms @ 48 kHz per channel
    const val FRAME_FLOATS = 480 // 240 * 2 channels
    const val PCM_BYTES = 960 // 480 * 2 bytes

    const val TICKS_PER_SECOND = 10_000_000L
    const val TICKS_PER_MS = 10_000L
    const val FRAME_TICKS = 50_000L // 5 ms

    fun int16ToFloat(pcmShort: Short): Float {
        return pcmShort / 32768.0f
    }

    fun floatToInt16(sample: Float): Short {
        val clamped = Math.round(sample * 32767.0).coerceIn(-32767L, 32767L)
        return clamped.toShort()
    }

    fun floatToInt16Dithered(sample: Float, rng: Random): Short {
        val dither = rng.nextDouble() - rng.nextDouble() // TPDF in (-1.0, 1.0)
        val scaled = sample * 32767.0 + dither
        val clamped = Math.round(scaled).coerceIn(-32767L, 32767L)
        return clamped.toShort()
    }
}
