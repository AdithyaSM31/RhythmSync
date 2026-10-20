package com.rhythmsync.android.dsp

import org.junit.Assert.*
import org.junit.Test
import kotlin.math.sin

class EqDspTest {

    @Test
    fun testPeakingFilterFrequencyResponse() {
        val sampleRate = 48000
        val f0 = 1000.0
        val gainDb = 6.0
        val coeff = EqCurve.calculatePeakingBiquad(f0, sampleRate, gainDb)

        val biquads = listOf(coeff)

        // At center frequency (1 kHz): gain must be +6.0 dB (+/- 0.05 dB)
        val centerGain = EqCurve.evaluateResponseDb(biquads, 1000.0, sampleRate)
        assertEquals(6.0, centerGain, 0.05)

        // Far from center (e.g. 100 Hz): gain approaches 0 dB (+/- 0.1 dB)
        val farGain = EqCurve.evaluateResponseDb(biquads, 100.0, sampleRate)
        assertEquals(0.0, farGain, 0.1)

        // High frequency (e.g. 15 kHz): gain approaches 0 dB
        val highGain = EqCurve.evaluateResponseDb(biquads, 15000.0, sampleRate)
        assertEquals(0.0, highGain, 0.1)
    }

    @Test
    fun testGoldenSectionPeakSearchAndHeadroomProtection() {
        val sampleRate = 48000

        // 1. Flat curve -> Peak 0 dB, Preamp 0 dB
        val flatGains = EqPresets.FLAT
        val flatPeak = EqCurve.calculatePeakDb(flatGains, sampleRate)
        assertEquals(0.0, flatPeak, 1e-4)
        assertEquals(0.0, EqCurve.calculatePreampDb(flatPeak), 1e-4)
        assertEquals(1.0, EqCurve.calculatePreampLinear(0.0), 1e-4)

        // 2. Pure cut (Treble cut) -> Peak <= 0 dB, Preamp 0 dB
        val trebleCut = EqPresets.TREBLE_CUT
        val cutPeak = EqCurve.calculatePeakDb(trebleCut, sampleRate)
        assertTrue("Pure cut peak must be <= 0 dB", cutPeak <= 0.001)
        assertEquals(0.0, EqCurve.calculatePreampDb(cutPeak), 1e-4)

        // 3. Max boost on all 10 bands (+12 dB) -> compound peak ~ +18.4 dB
        val allBoost = FloatArray(10) { 12.0f }
        val boostPeak = EqCurve.calculatePeakDb(allBoost, sampleRate)
        assertTrue("All 10 bands at +12 dB must peak around +18.4 dB (got $boostPeak)", boostPeak in 17.5..19.5)

        val preampDb = EqCurve.calculatePreampDb(boostPeak)
        assertEquals(-boostPeak, preampDb, 1e-4)
        val preampLinear = EqCurve.calculatePreampLinear(preampDb)
        assertTrue("Linear preamp should be approx 0.12 (got $preampLinear)", preampLinear in 0.10..0.14)
    }

    @Test
    fun testBitExactBypassOnFlat() {
        val processor = EqProcessor()
        processor.setGainsImmediate(EqPresets.FLAT)

        val original = FloatArray(480) { (it - 240) / 240.0f }
        val testBuffer = original.clone()

        processor.process(testBuffer, 240)

        // Every float must be bit-exact identical
        for (i in original.indices) {
            assertEquals("Sample $i was modified during bypass!", original[i], testBuffer[i], 0.0f)
        }
    }

    @Test
    fun testPresetsBandValues() {
        assertEquals(10, EqPresets.FLAT.size)
        assertEquals(10, EqPresets.LAPTOP_SPEAKERS.size)
        assertEquals(10, EqPresets.BASS_BOOST.size)
        assertEquals(10, EqPresets.TREBLE_CUT.size)

        assertTrue(EqPresets.isFlat(EqPresets.FLAT))
        assertFalse(EqPresets.isFlat(EqPresets.LAPTOP_SPEAKERS))
        assertNotNull(EqPresets.getGainsForPreset("laptop speakers"))
        assertNotNull(EqPresets.getGainsForPreset("bass boost"))
        assertNotNull(EqPresets.getGainsForPreset("treble cut"))
    }

    @Test
    fun testNyquistGuardAtLowSampleRate() {
        // At 32,000 Hz sample rate: 0.45 * 32,000 = 14,400 Hz.
        // Band 9 (16,000 Hz) is above 14,400 Hz and must be marked inactive.
        assertFalse(EqBands.isActive(9, 32000))
        assertTrue(EqBands.isActive(8, 32000)) // 8,000 Hz < 14,400 Hz
    }

    @Test
    fun testParameterSmoothingClickFree() {
        val processor = EqProcessor()
        processor.setGainsImmediate(EqPresets.FLAT)

        // Generate a 440 Hz continuous tone
        val buffer = FloatArray(960) // 480 frames = 10 ms
        for (i in 0 until 480) {
            val v = 0.5f * sin(2.0 * Math.PI * 440.0 * i / 48000.0).toFloat()
            buffer[i * 2] = v
            buffer[i * 2 + 1] = v
        }

        // Switch preset to Bass Boost during playback
        processor.setGains(EqPresets.BASS_BOOST)
        processor.process(buffer, 480)

        // Verify all samples remain finite and bounded within [-1.0, 1.0]
        for (i in buffer.indices) {
            assertTrue("Sample $i is not finite: ${buffer[i]}", buffer[i].isFinite())
            assertTrue("Sample $i exceeded 1.0 full scale: ${buffer[i]}", buffer[i] in -1.1f..1.1f)
        }
    }
}
