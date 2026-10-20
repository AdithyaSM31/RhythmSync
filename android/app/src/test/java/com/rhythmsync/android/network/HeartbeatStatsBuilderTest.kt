package com.rhythmsync.android.network

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class HeartbeatStatsBuilderTest {

    private val second = 10_000_000L

    private fun HeartbeatStatsBuilder.sample(
        seconds: Int,
        received: Long,
        late: Long,
        concealed: Long,
        playing: Boolean = true,
        syncErrorMs: Double = 0.4
    ) = build(
        nowTicks = seconds * second,
        received = received,
        late = late,
        concealed = concealed,
        playing = playing,
        syncErrorMs = syncErrorMs,
        driftPpm = 12.0,
        fitResidualMs = 0.2,
        state = "InSync"
    )

    @Test
    fun firstHeartbeatReportsZeroPercentNotARunningCount() {
        val builder = HeartbeatStatsBuilder()
        val stats = builder.sample(1, received = 1000, late = 50, concealed = 30)
        assertEquals(0.0, stats.LatePct, 0.0)
        assertEquals(0.0, stats.LossPct, 0.0)
    }

    @Test
    fun latePctIsAPercentageOfReceivedFramesInTheWindow() {
        val builder = HeartbeatStatsBuilder()
        builder.sample(1, received = 0, late = 0, concealed = 0)
        // 200 received and 2 late in the second since the last heartbeat is 1 percent, not a count of 2.
        val stats = builder.sample(2, received = 200, late = 2, concealed = 0)
        assertEquals(1.0, stats.LatePct, 1e-9)
    }

    @Test
    fun aHighCumulativeLateCountDoesNotTripTheCongestionThreshold() {
        val builder = HeartbeatStatsBuilder()
        // 50 late frames in total over thousands received; the old code sent the count (50) and the host warned (> 5).
        var stats = builder.sample(1, received = 0, late = 0, concealed = 0)
        for (s in 2..30) {
            stats = builder.sample(s, received = (s - 1) * 200L, late = if (s < 4) 25L * (s - 1) else 50L, concealed = 0)
            assertTrue("LatePct=${stats.LatePct} at ${s}s", stats.LatePct <= 25.0 && stats.LatePct.isFinite())
        }
        assertEquals(0.0, stats.LatePct, 1e-9) // the burst is more than 10 s old
    }

    @Test
    fun lossPctIsConcealedOverPlayedFrames() {
        val builder = HeartbeatStatsBuilder()
        builder.sample(1, received = 0, late = 0, concealed = 0)
        val stats = builder.sample(2, received = 90, late = 0, concealed = 10)
        assertEquals(10.0, stats.LossPct, 1e-9)
    }

    @Test
    fun percentagesAreZeroWhenNothingArrived() {
        val builder = HeartbeatStatsBuilder()
        builder.sample(1, received = 5, late = 1, concealed = 0)
        val stats = builder.sample(2, received = 5, late = 1, concealed = 0)
        assertEquals(0.0, stats.LatePct, 0.0)
        assertEquals(0.0, stats.LossPct, 0.0)
    }

    @Test
    fun counterRestartNeverProducesNegativeOrNonFinitePercentages() {
        val builder = HeartbeatStatsBuilder()
        builder.sample(1, received = 5000, late = 100, concealed = 100)
        // A new session restarts the counters from zero.
        val stats = builder.sample(2, received = 10, late = 0, concealed = 0)
        assertTrue(stats.LatePct.isFinite() && stats.LatePct >= 0.0)
        assertTrue(stats.LossPct.isFinite() && stats.LossPct >= 0.0)
    }

    @Test
    fun syncErrorIsNaNUnlessPlaying() {
        val builder = HeartbeatStatsBuilder()
        assertTrue(builder.sample(1, 0, 0, 0, playing = false, syncErrorMs = 7.0).SyncErrorMs.isNaN())
        assertEquals(0.4, builder.sample(2, 0, 0, 0, playing = true, syncErrorMs = 0.4).SyncErrorMs, 0.0)
    }

    @Test
    fun otherFieldsPassThrough() {
        val stats = HeartbeatStatsBuilder().sample(1, 0, 0, 0)
        assertEquals(12.0, stats.DriftPpm, 0.0)
        assertEquals(0.2, stats.FitResidualMs, 0.0)
        assertEquals("InSync", stats.State)
    }
}
