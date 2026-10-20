package com.rhythmsync.android.sync

import org.junit.Assert.*
import org.junit.Test

class ClockEstimatorTest {

    @Test
    fun testNtpCalculations() {
        val estimator = ClockEstimator()
        // Client sends at T1 = 1,000,000 ticks (100 ms)
        // Host receives at T2 = 1,050,000 ticks (Host clock offset +45ms)
        // Host sends at T3 = 1,051,000 ticks (processing took 1,000 ticks / 0.1ms)
        // Client receives at T4 = 1,012,000 ticks
        // One way delay = ~5,500 ticks. Total trip = 12,000 ticks. Server proc = 1,000 ticks.
        // RTT = (1,012,000 - 1,000,000) - (1,051,000 - 1,050,000) = 12,000 - 1,000 = 11,000 ticks (1.1 ms)
        // Offset = ((1,050,000 - 1,000,000) + (1,051,000 - 1,012,000)) / 2 = (50,000 + 39,000) / 2 = 44,500 ticks (4.45 ms)
        val t1 = 1_000_000L
        val t2 = 1_050_000L
        val t3 = 1_051_000L
        val t4 = 1_012_000L

        estimator.addSample(t1, t2, t3, t4)
        assertEquals(1, estimator.sampleCount)
        assertFalse("Single sample must not converge", estimator.isConverged)
        assertEquals(SyncState.Syncing, estimator.state)
    }

    @Test
    fun testConvergenceAndLinearDriftRegression() {
        val estimator = ClockEstimator()
        // Host offset: +50,000,000 ticks (5 seconds)
        // Drift: +20 ppm -> slope b = 1.000020
        val baseOffsetTicks = 50_000_000L
        val driftSlope = 1.000020

        var clientTime = 10_000_000L // 1 sec
        val rttTicks = 10_000L // 1 ms symmetric RTT (0.5 ms one way)

        for (i in 1..25) {
            val t1 = clientTime
            val hostNow = (baseOffsetTicks + t1 * driftSlope).toLong()
            val t2 = hostNow + 5000L
            val t3 = t2 + 1000L // 0.1 ms host turnaround
            val t4 = t1 + rttTicks + 1000L

            estimator.addSample(t1, t2, t3, t4)
            clientTime += 500_000L // 50 ms step
        }

        assertEquals(25, estimator.sampleCount)
        assertTrue("Estimator should converge after 25 clean samples", estimator.isConverged)
        assertTrue("everConverged should be true", estimator.everConverged)
        assertEquals(SyncState.InSync, estimator.state)

        // Verify drift ppm is close to +20 ppm
        assertEquals(20.0, estimator.driftPpm, 1.0)
        assertTrue("Standard error must be < 0.5 ms", estimator.stdErrorMs < 0.5)

        // Verify clientToHost mapping accuracy
        val queryClient = clientTime + 1_000_000L
        val expectedHost = (baseOffsetTicks + queryClient * driftSlope).toLong()
        val mappedHost = estimator.clientToHost(queryClient)
        val diffMs = Math.abs(expectedHost - mappedHost) / 10000.0
        assertTrue("Host mapping error must be < 0.1 ms (was $diffMs ms)", diffMs < 0.1)
    }

    @Test
    fun testHighRttSamplesAreDownweighted() {
        val estimator = ClockEstimator()
        val baseOffset = 20_000_000L

        var clientTime = 5_000_000L
        for (i in 1..25) {
            val t1 = clientTime
            // Baseline 1ms RTT
            var rtt = 10_000L
            // Inject network spikes on samples 5, 12, 19 (e.g. 150 ms RTT)
            if (i == 5 || i == 12 || i == 19) {
                rtt = 1_500_000L
            }
            val t2 = (baseOffset + t1).toLong() + (rtt / 2)
            val t3 = t2 + 1000L
            val t4 = t1 + rtt + 1000L

            estimator.addSample(t1, t2, t3, t4)
            clientTime += 500_000L
        }

        assertTrue("Should converge despite isolated network spikes", estimator.isConverged)
        assertEquals(SyncState.InSync, estimator.state)
        assertEquals(0.0, estimator.driftPpm, 2.0)
    }

    @Test
    fun testDegradedStateOnStaleSamples() {
        val estimator = ClockEstimator()
        var clientTime = 1_000_000L

        for (i in 1..22) {
            val t1 = clientTime
            val t2 = t1 + 5000L
            val t3 = t2 + 500L
            val t4 = t1 + 10_500L
            estimator.addSample(t1, t2, t3, t4)
            clientTime += 500_000L
        }

        assertTrue(estimator.isConverged)
        assertEquals(SyncState.InSync, estimator.state)

        // Recompute with now = lastSample + 11 seconds (110,000,000 ticks)
        val staleTime = clientTime + 110_000_000L
        estimator.recompute(staleTime)

        assertEquals("State must become Degraded after 11s of silence", SyncState.Degraded, estimator.state)
        assertTrue("everConverged must remain true", estimator.everConverged)
    }

    @Test
    fun testSlidingWindowEviction() {
        val estimator = ClockEstimator(windowTicks = 600_000_000L) // 60s
        var clientTime = 1_000_000L

        for (i in 1..10) {
            estimator.addSample(clientTime, clientTime + 5000L, clientTime + 5500L, clientTime + 10500L)
            clientTime += 10_000_000L // 1s
        }
        assertEquals(10, estimator.sampleCount)

        // Advance by 70 seconds and add 5 new samples
        clientTime += 700_000_000L
        for (i in 1..5) {
            estimator.addSample(clientTime, clientTime + 5000L, clientTime + 5500L, clientTime + 10500L)
            clientTime += 10_000_000L
        }

        // Old 10 samples should have been evicted
        assertEquals(5, estimator.sampleCount)
    }
}
