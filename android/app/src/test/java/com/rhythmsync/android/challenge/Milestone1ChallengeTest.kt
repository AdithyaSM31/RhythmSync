package com.rhythmsync.android.challenge

import com.rhythmsync.android.dsp.EqBands
import com.rhythmsync.android.dsp.EqCurve
import com.rhythmsync.android.dsp.EqPresets
import com.rhythmsync.android.dsp.EqProcessor
import com.rhythmsync.android.sync.ClockEstimator
import com.rhythmsync.android.sync.JitterBuffer
import com.rhythmsync.android.sync.PlaybackScheduler
import com.rhythmsync.android.sync.SyncState
import org.junit.Assert.*
import org.junit.Test
import java.util.Random
import kotlin.math.*

class Milestone1ChallengeTest {

    // =========================================================================
    // 1. CLOCK ESTIMATOR CHALLENGES
    // =========================================================================

    @Test
    fun testClockDriftRegressionPositiveAndNegativePpm() {
        // Test multiple drift levels: +50 ppm, -50 ppm, +200 ppm, -200 ppm
        val testPpmCases = listOf(50.0, -50.0, 200.0, -200.0)

        for (targetPpm in testPpmCases) {
            val estimator = ClockEstimator()
            val driftSlope = 1.0 + (targetPpm * 1e-6)
            val baseOffsetTicks = 123_456_789L

            var clientTime = 10_000_000L // 1s in 100ns ticks
            val rttTicks = 10_000L // 1ms

            for (i in 1..40) {
                val t1 = clientTime
                val hostNow = (baseOffsetTicks + t1 * driftSlope).toLong()
                val t2 = hostNow + 5000L
                val t3 = t2 + 1000L
                val t4 = t1 + rttTicks + 1000L

                estimator.addSample(t1, t2, t3, t4)
                clientTime += 500_000L // 50ms interval
            }

            assertTrue("Must converge at $targetPpm ppm", estimator.isConverged)
            assertEquals("Sync state must be InSync", SyncState.InSync, estimator.state)
            assertEquals("Drift PPM must match target within 0.5 ppm", targetPpm, estimator.driftPpm, 0.5)
            assertTrue("Standard error must be < 0.5 ms (was ${estimator.stdErrorMs})", estimator.stdErrorMs < 0.5)

            // Verify mapping accuracy
            val queryClient = clientTime + 2_000_000L
            val expectedHost = (baseOffsetTicks + queryClient * driftSlope).toLong()
            val mappedHost = estimator.clientToHost(queryClient)
            val diffMs = abs(expectedHost - mappedHost) / 10000.0
            assertTrue("Mapping error must be < 0.05 ms at $targetPpm ppm (was $diffMs ms)", diffMs < 0.05)
        }
    }

    @Test
    fun testClockEstimatorHeavyJitterAndAsymmetricDelaySpikes() {
        val estimator = ClockEstimator()
        val rng = Random(42)
        val baseOffset = 50_000_000L // 5s
        val driftSlope = 1.000030 // +30 ppm

        var clientTime = 10_000_000L

        // 60 samples with heavy jitter and asymmetric spikes
        for (i in 1..60) {
            val t1 = clientTime
            // Baseline 2ms RTT (20,000 ticks)
            val baseForwardDelay = 10_000L
            val baseReverseDelay = 10_000L

            // 20% of packets suffer massive asymmetric forward spikes (up to 80ms = 800,000 ticks)
            val forwardJitter = if (rng.nextDouble() < 0.20) (rng.nextDouble() * 800_000L).toLong() else (rng.nextDouble() * 20_000L).toLong()
            // Reverse delay stays small (~1-3ms)
            val reverseJitter = (rng.nextDouble() * 20_000L).toLong()

            val forwardDelay = baseForwardDelay + forwardJitter
            val reverseDelay = baseReverseDelay + reverseJitter

            val hostT2 = (baseOffset + t1 * driftSlope).toLong() + forwardDelay
            val hostT3 = hostT2 + 1000L
            val clientT4 = t1 + forwardDelay + 1000L + reverseDelay

            estimator.addSample(t1, hostT2, hostT3, clientT4)
            clientTime += 5_000_000L // 500ms intervals across 30s window
        }

        assertTrue("Estimator must converge despite heavy jitter and asymmetric spikes", estimator.isConverged)
        assertEquals("State must be InSync", SyncState.InSync, estimator.state)
        // With Min-RTT bucketing, low-RTT samples dominate regression slope
        assertEquals("Drift slope must be close to +30 ppm", 30.0, estimator.driftPpm, 5.0)
    }

    @Test
    fun testClockEstimatorPacketReorderingAndPacketLoss() {
        val estimator = ClockEstimator()
        val rng = Random(12345)
        val baseOffset = 20_000_000L
        var clientTime = 5_000_000L

        data class RawExchange(val t1: Long, val t2: Long, val t3: Long, val t4: Long)
        val allExchanges = mutableListOf<RawExchange>()

        // Generate 60 exchanges
        for (i in 1..60) {
            val t1 = clientTime
            val t2 = baseOffset + t1 + 5000L
            val t3 = t2 + 1000L
            val t4 = t1 + 11_000L
            allExchanges.add(RawExchange(t1, t2, t3, t4))
            clientTime += 500_000L
        }

        // Simulate 40% packet loss
        val received = allExchanges.filter { rng.nextDouble() > 0.40 }.toMutableList()
        assertTrue("At least 25 samples should survive", received.size >= 25)

        // Simulate occasional packet reordering (swap adjacent pairs)
        for (i in 0 until received.size - 1 step 3) {
            val tmp = received[i]
            received[i] = received[i + 1]
            received[i + 1] = tmp
        }

        for (s in received) {
            estimator.addSample(s.t1, s.t2, s.t3, s.t4)
        }

        assertTrue("Estimator should converge with loss and reordering", estimator.isConverged)
        assertEquals(SyncState.InSync, estimator.state)
        assertEquals(0.0, estimator.driftPpm, 1.0)
    }

    // =========================================================================
    // 2. JITTER BUFFER CHALLENGES
    // =========================================================================

    @Test
    fun testJitterBufferHighVolumeWrapAroundAndOverflow() {
        val capacity = 256
        val buffer = JitterBuffer(capacity)
        val dummyPcm = ShortArray(480) { (it % 100).toShort() }

        // Continuous streaming of 5,000 frames with consumer tracking 100 frames behind
        for (seq in 0L until 5000L) {
            val playAt = seq * 50_000L
            val accepted = buffer.insert(seq, playAt, dummyPcm)
            assertTrue("Frame $seq should be accepted within sliding capacity", accepted)

            // Consumer consumes frames
            if (seq >= 100L) {
                buffer.consume(seq - 100L)
            }
        }

        assertEquals(0L, buffer.rejectedCount)
        assertEquals(0L, buffer.lateCount)
        assertEquals(0L, buffer.duplicateCount)
        assertEquals(5000L, buffer.acceptedCount)

        // Now test buffer overflow: try inserting a frame 300 frames ahead of consumer
        val currentConsumed = buffer.consumedThrough // 4899L
        val overflowSeq = currentConsumed + capacity + 10L
        val overflowAccepted = buffer.insert(overflowSeq, overflowSeq * 50_000L, dummyPcm)
        assertFalse("Frame beyond capacity must be rejected", overflowAccepted)
        assertEquals(1L, buffer.rejectedCount)

        // Late frame: insert frame <= currentConsumed
        val lateAccepted = buffer.insert(currentConsumed, currentConsumed * 50_000L, dummyPcm)
        assertFalse("Frame <= consumedThrough must be rejected as late", lateAccepted)
        assertEquals(1L, buffer.lateCount)
    }

    @Test
    fun testJitterBufferOutOfOrderBurstHandling() {
        val buffer = JitterBuffer(256)
        val frameCount = 100
        val pcmList = List(frameCount) { i -> ShortArray(480) { (i + 1).toShort() } }

        // Shuffle sequence numbers 0..99
        val shuffledIndices = (0 until frameCount).shuffled(Random(999))

        for (idx in shuffledIndices) {
            val seq = idx.toLong()
            val playAt = seq * 50_000L
            val ok = buffer.insert(seq, playAt, pcmList[idx])
            assertTrue("Shuffled frame $seq must be accepted", ok)
        }

        assertEquals(frameCount, buffer.getPresentFrameCount())
        assertEquals(0L, buffer.getOldestPresentSeq())
        assertEquals((frameCount - 1).toLong(), buffer.getNewestPresentSeq())

        // Verify retrieval in exact sequence order
        for (seq in 0 until frameCount) {
            val frame = buffer.getFrame(seq.toLong())
            assertNotNull("Frame $seq must be retrievable", frame)
            assertEquals((seq + 1).toShort(), frame!![0])
        }
    }

    @Test
    fun testJitterBufferHostTimeMappingEdgeCases() {
        val buffer = JitterBuffer(256)
        val pcm = ShortArray(480)

        // Empty buffer must return null
        assertNull(buffer.tryMapHostTimeToPosition(100_000L))

        // Single frame
        buffer.insert(10L, 500_000L, pcm)
        // At playAt -> 10 * 240 = 2400.0
        val posSingle = buffer.tryMapHostTimeToPosition(500_000L)
        assertNotNull(posSingle)
        assertEquals(2400.0, posSingle!!, 1e-4)

        // Before playAt -> extrapolates backward
        val posBefore = buffer.tryMapHostTimeToPosition(450_000L)
        assertNotNull(posBefore)
        // 50,000 ticks * 0.0048 = 240 samples earlier -> 2160.0
        assertEquals(2160.0, posBefore!!, 1e-4)

        // Latency step / hold test
        buffer.insert(11L, 1_000_000L, pcm) // Large gap from 500k to 1M ticks (> 100k nominal)
        // Position during gap must not exceed sequence 11 start (2640.0)
        val posDuringGap = buffer.tryMapHostTimeToPosition(700_000L)
        assertNotNull(posDuringGap)
        assertEquals(2640.0, posDuringGap!!, 1e-4)
    }

    // =========================================================================
    // 3. PLAYBACK SCHEDULER CHALLENGES
    // =========================================================================

    @Test
    fun testPlaybackSchedulerHardResyncBoundaries() {
        val scheduler = PlaybackScheduler()

        // Exact boundary is 960.0 samples (20.0 ms @ 48kHz)
        // At 959.0 samples: NO hard resync
        assertFalse(scheduler.checkAndPerformResync(959.0))
        assertEquals(0L, scheduler.resyncCount)

        // At 961.0 samples: HARD resync
        assertTrue(scheduler.checkAndPerformResync(961.0))
        assertEquals(1L, scheduler.resyncCount)
        assertEquals(961.0, scheduler.readPosition, 1e-6)
        assertFalse("Behind target: should NOT await content", scheduler.isAwaitingContent)

        // Target at 0.0 (error is -961.0: ahead of target)
        assertTrue(scheduler.checkAndPerformResync(0.0))
        assertEquals(2L, scheduler.resyncCount)
        assertEquals(0.0, scheduler.readPosition, 1e-6)
        assertTrue("Ahead of target: MUST await content", scheduler.isAwaitingContent)
    }

    @Test
    fun testPlaybackSchedulerOffsetResyncThreshold() {
        val scheduler = PlaybackScheduler(targetLatencyMs = 200)

        // Change offset by 0.20 ms (<= 0.25 ms threshold): offsetJumped should NOT be set
        scheduler.setOffsetMs(0.20)
        assertFalse(scheduler.checkAndPerformResync(15.0)) // 15 samples error (< 960 hard resync)

        // Change offset by 0.50 ms (> 0.25 ms threshold): offsetJumped IS set
        scheduler.setOffsetMs(0.70)
        // Sync error 15 samples (> 12.0 samples / 0.25 ms): triggers offset resync!
        assertTrue(scheduler.checkAndPerformResync(15.0))
        assertEquals(1L, scheduler.resyncCount)
    }

    @Test
    fun testPlaybackSchedulerPiLoopWindupAndStabilityUnderExtremeStress() {
        val scheduler = PlaybackScheduler()

        // Stress 1: 10,000 steps of massive error (+100,000 samples)
        for (i in 0 until 10_000) {
            val step = scheduler.updateDriftController(100_000.0)
            assertTrue("Step ratio must not exceed +500 ppm (got $step)", step <= 1.0005000001)
            assertTrue("Step ratio must be finite", step.isFinite())
        }

        // Verify integral clamp
        assertTrue("Integral must be bounded by MAX_INTEGRAL", scheduler.integral <= PlaybackScheduler.MAX_INTEGRAL)
        assertEquals(PlaybackScheduler.MAX_INTEGRAL, scheduler.integral, 1e-6)

        // Stress 2: Abrupt reversal to -100,000 samples
        for (i in 0 until 10_000) {
            val step = scheduler.updateDriftController(-100_000.0)
            assertTrue("Step ratio must not drop below -500 ppm (got $step)", step >= 0.9994999999)
            assertTrue("Step ratio must be finite", step.isFinite())
        }

        assertTrue("Integral must be bounded by -MAX_INTEGRAL", scheduler.integral >= -PlaybackScheduler.MAX_INTEGRAL)
        assertEquals(-PlaybackScheduler.MAX_INTEGRAL, scheduler.integral, 1e-6)

        // Stress 3: Return to zero error -> must recover towards 1.0
        for (i in 0 until 50_000) {
            scheduler.updateDriftController(0.0)
        }
        val recoveredStep = scheduler.updateDriftController(0.0)
        assertTrue("After zero error, step should return towards nominal (got $recoveredStep)", abs(recoveredStep - 1.0) < 0.0001)
    }

    // =========================================================================
    // 4. EQUALIZER & GOLDEN-SECTION PEAK SEARCH CHALLENGES
    // =========================================================================

    @Test
    fun testBiquadStabilityAtExtremeGainsAcrossAllBands() {
        val sampleRate = 48000
        val processor = EqProcessor(sampleRate = sampleRate, channels = 2)

        // Test configuration 1: All +12 dB
        processor.setGainsImmediate(FloatArray(10) { 12.0f })
        val buffer = FloatArray(9600) // 100 ms of audio
        // Unit impulse at sample 0
        buffer[0] = 1.0f
        buffer[1] = 1.0f
        processor.process(buffer, 4800)

        for (i in buffer.indices) {
            assertTrue("Impulse response must be finite at index $i", buffer[i].isFinite())
            assertFalse("No NaN allowed", buffer[i].isNaN())
        }

        // Test configuration 2: All -12 dB
        processor.setGainsImmediate(FloatArray(10) { -12.0f })
        buffer.fill(0.5f) // DC step input
        processor.process(buffer, 4800)

        for (i in buffer.indices) {
            assertTrue("DC response must be finite at index $i", buffer[i].isFinite())
            assertFalse("No NaN allowed", buffer[i].isNaN())
        }

        // Test configuration 3: Checkerboard +12 dB, -12 dB, +12 dB...
        val checkerboard = FloatArray(10) { if (it % 2 == 0) 12.0f else -12.0f }
        processor.setGainsImmediate(checkerboard)
        val rng = Random(777)
        for (i in buffer.indices) buffer[i] = (rng.nextFloat() * 2f - 1f) // White noise
        processor.process(buffer, 4800)

        for (i in buffer.indices) {
            assertTrue("Noise response must be finite at index $i", buffer[i].isFinite())
            assertFalse("No NaN allowed", buffer[i].isNaN())
        }
    }

    @Test
    fun testGoldenSectionPeakSearchExhaustiveAccuracy() {
        val sampleRate = 48000

        // Test several challenging gain profiles:
        val testProfiles = listOf(
            FloatArray(10) { 12.0f }, // All max
            FloatArray(10) { if (it == 4) 12.0f else 0.0f }, // Single 500 Hz boost
            FloatArray(10) { if (it == 4 || it == 5) 12.0f else 0.0f }, // Adjacent 500Hz + 1kHz boost
            EqPresets.BASS_BOOST,
            EqPresets.LAPTOP_SPEAKERS
        )

        for (gains in testProfiles) {
            val peakDb = EqCurve.calculatePeakDb(gains, sampleRate)
            val biquads = (0 until EqBands.BAND_COUNT).map { i ->
                EqCurve.calculatePeakingBiquad(EqBands.FREQUENCIES[i], sampleRate, gains[i].toDouble())
            }

            // Perform high-density sweep (10,000 frequencies log-spaced)
            val nSweep = 10_000
            val logMin = ln(20.0)
            val logMax = ln(minOf(20000.0, 0.45 * sampleRate))

            var maxExhaustiveDb = 0.0
            for (step in 0 until nSweep) {
                val f = exp(logMin + step * (logMax - logMin) / (nSweep - 1))
                val resp = EqCurve.evaluateResponseDb(biquads, f, sampleRate)
                if (resp > maxExhaustiveDb) {
                    maxExhaustiveDb = resp
                }
            }

            // calculatePeakDb must find peak within 0.01 dB of high-density sweep!
            assertTrue(
                "Peak search ($peakDb dB) must match or exceed exhaustive sweep ($maxExhaustiveDb dB) within 0.02 dB",
                peakDb >= maxExhaustiveDb - 0.02
            )
        }
    }

    @Test
    fun testPreampAttenuationGuaranteesZeroDbfsSteadyStateOutput() {
        val sampleRate = 48000
        val processor = EqProcessor(sampleRate = sampleRate, channels = 2)

        // Set all 10 bands to +12 dB
        val allBoost = FloatArray(10) { 12.0f }
        processor.setGainsImmediate(allBoost)

        // Steady-state preamp linear value
        val peakDb = processor.targetPeakDb
        val preampLinear = processor.targetPreampLinear
        assertTrue("Peak dB must be > 17 dB for all +12 dB (got $peakDb)", peakDb > 17.0)
        assertTrue("Preamp linear must attenuate (< 0.15, got $preampLinear)", preampLinear < 0.15)

        // Generate full-scale 0 dBFS sine waves (amplitude 1.0) across all band centers and intermediate frequencies
        val testFrequencies = EqBands.FREQUENCIES + doubleArrayOf(45.0, 90.0, 180.0, 350.0, 700.0, 1400.0, 2800.0, 5600.0, 11200.0)

        for (f in testFrequencies) {
            processor.clearState()
            // 2048 frames (enough for filter to reach steady state)
            val nFrames = 2048
            val buffer = FloatArray(nFrames * 2)
            for (i in 0 until nFrames) {
                val v = sin(2.0 * PI * f * i / sampleRate.toDouble()).toFloat()
                buffer[i * 2] = v
                buffer[i * 2 + 1] = v
            }

            processor.process(buffer, nFrames)

            // Measure steady-state peak in the last 512 frames
            var maxOut = 0.0f
            for (i in (nFrames - 512) until nFrames) {
                maxOut = maxOf(maxOut, abs(buffer[i * 2]), abs(buffer[i * 2 + 1]))
            }

            // Strictly verify output does NOT clip (> 1.0005)
            assertTrue(
                "Steady state output at freq $f Hz must not exceed 0 dBFS (got amplitude $maxOut)",
                maxOut <= 1.0005f
            )
        }
    }
}
