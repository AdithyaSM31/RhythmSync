package com.rhythmsync.android.sync

import org.junit.Assert.*
import org.junit.Test
import kotlin.math.sin

class PlaybackSchedulerTest {

    @Test
    fun testPiLoopSteeringAndAntiWindup() {
        val scheduler = PlaybackScheduler()

        // 1. Zero error -> nominal ratio 1.0
        val stepZero = scheduler.updateDriftController(0.0)
        assertEquals(1.0, stepZero, 1e-6)

        // 2. Small positive error (+10 samples) -> speeds up within +500 ppm
        val stepSmall = scheduler.updateDriftController(10.0)
        assertTrue("Step must be > 1.0 when behind", stepSmall > 1.0)
        assertTrue("Correction must be within +500 ppm", stepSmall <= 1.0005)

        // 3. Accumulate integral with moderate error until anti-windup engages
        for (i in 0 until 5000) {
            scheduler.updateDriftController(5.0)
        }
        // Correction must be saturated at +500 ppm
        assertEquals(1.0005, scheduler.updateDriftController(5.0), 1e-6)
        val saturatedIntegral = scheduler.integral
        assertTrue("Integral accumulated up to anti-windup boundary", saturatedIntegral > 20000.0)
        assertTrue("Integral never exceeds MAX_INTEGRAL", saturatedIntegral <= PlaybackScheduler.MAX_INTEGRAL)

        // 4. Negative error reverses and unwinds integral
        for (i in 0 until 10000) {
            scheduler.updateDriftController(-5.0)
        }
        assertEquals(0.9995, scheduler.updateDriftController(-5.0), 1e-6)
        assertTrue("Integral unwound and saturated in negative direction", scheduler.integral < -20000.0)
        assertTrue("Integral never exceeds -MAX_INTEGRAL", scheduler.integral >= -PlaybackScheduler.MAX_INTEGRAL)
    }

    @Test
    fun testHardResyncThreshold() {
        val scheduler = PlaybackScheduler()

        // Read position at 0.0, target at 500 samples (within 20ms = 960 samples)
        assertFalse("500 samples is within 20ms threshold", scheduler.checkAndPerformResync(500.0))
        assertEquals(0L, scheduler.resyncCount)

        // Target at 1000 samples (> 960 samples / 20ms behind)
        assertTrue("1000 samples error must trigger hard resync", scheduler.checkAndPerformResync(1000.0))
        assertEquals(1L, scheduler.resyncCount)
        assertEquals(1000.0, scheduler.readPosition, 1e-6)
        assertFalse(scheduler.isAwaitingContent)

        // Target at -50 samples (ahead by > 960 samples: readPos is 1000, target is 0)
        assertTrue("Negative error > 960 samples must trigger ahead resync", scheduler.checkAndPerformResync(0.0))
        assertEquals(2L, scheduler.resyncCount)
        assertTrue("Must be awaiting content when ahead", scheduler.isAwaitingContent)
    }

    @Test
    fun testOffsetResyncThreshold() {
        val scheduler = PlaybackScheduler(targetLatencyMs = 200)

        // Move offset by 0.1 ms (<= 0.25 ms): slewed, no hard resync
        scheduler.setOffsetMs(0.1)
        assertFalse(scheduler.checkAndPerformResync(10.0)) // 10 samples < 12 samples

        // Move offset by 2.0 ms (> 0.25 ms): offset jump flag armed
        scheduler.setOffsetMs(2.0)
        // With sync error 20 samples (> 12 samples / 0.25 ms): immediate hard resync triggered
        assertTrue(scheduler.checkAndPerformResync(20.0))
        assertEquals(1L, scheduler.resyncCount)
    }

    @Test
    fun testCatmullRomResamplerAccuracy() {
        val scheduler = PlaybackScheduler()

        // Test with points on f(x) = x
        // Catmull-Rom of linear points must be exact
        val p0 = 0f
        val p1 = 1f
        val p2 = 2f
        val p3 = 3f

        val mid = scheduler.catmullRom(p0, p1, p2, p3, 0.5f)
        assertEquals(1.5f, mid, 1e-6f)

        // Test stereo resampling of 440Hz sine wave
        val input = FloatArray(16) // 8 stereo frames
        for (f in 0 until 8) {
            val v = sin(2.0 * Math.PI * 440.0 * f / 48000.0).toFloat()
            input[f * 2] = v
            input[f * 2 + 1] = v
        }

        val output = FloatArray(4) // 2 output frames
        scheduler.resampleStereo(
            input = input,
            inputOffsetFrames = 1,
            startFractionalFrame = 0.5,
            stepRatio = 1.0,
            output = output,
            outputFrames = 2
        )

        // Expected mid values for 440Hz wave
        val expected0 = sin(2.0 * Math.PI * 440.0 * 1.5 / 48000.0).toFloat()
        assertEquals(expected0, output[0], 1e-3f)
        assertEquals(expected0, output[1], 1e-3f)
    }

    @Test
    fun testPacketLossConcealmentDecayAndSilence() {
        val scheduler = PlaybackScheduler()
        val goodFrame = FloatArray(480) { 1.0f } // Constant 1.0 amplitude
        scheduler.recordGoodFrame(goodFrame)

        val out = FloatArray(480)

        // Frame 1 lost: startGain 1.0, endGain 0.9 -> average ~0.95
        scheduler.concealMissingFrame(out)
        assertTrue(out[478] > 0.85f && out[478] < 0.95f)

        // Conceal frames 2 through 10: gain steadily decays
        for (m in 2..10) {
            scheduler.concealMissingFrame(out)
        }
        // At frame 10: endGain is 0.0 -> trailing sample is 0.0
        assertEquals(0.0f, out[478], 1e-4f)

        // Frame 11 lost: must be pure silence
        scheduler.concealMissingFrame(out)
        for (v in out) {
            assertEquals(0.0f, v, 1e-6f)
        }

        // Recovery: good frame received after outage -> apply recovery fade in
        val recoveryFrame = FloatArray(480) { 1.0f }
        scheduler.applyRecoveryFadeInIfNeeded(recoveryFrame)

        // First sample should be 0.0, last sample near 1.0
        assertEquals(0.0f, recoveryFrame[0], 1e-6f)
        assertTrue(recoveryFrame[478] > 0.99f)
    }
}
