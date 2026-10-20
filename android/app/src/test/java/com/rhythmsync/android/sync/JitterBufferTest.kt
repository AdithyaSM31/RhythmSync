package com.rhythmsync.android.sync

import org.junit.Assert.*
import org.junit.Test

class JitterBufferTest {

    @Test
    fun testInOrderAndOutOfOrderInsertion() {
        val buffer = JitterBuffer(256)
        val pcm1 = ShortArray(480) { 1 }
        val pcm2 = ShortArray(480) { 2 }
        val pcm3 = ShortArray(480) { 3 }

        // Insert 1, then 3, then 2 (out of order)
        assertTrue(buffer.insert(1L, 100_000L, pcm1))
        assertTrue(buffer.insert(3L, 200_000L, pcm3))
        assertTrue(buffer.insert(2L, 150_000L, pcm2))

        assertEquals(3, buffer.getPresentFrameCount())
        assertEquals(1L, buffer.getOldestPresentSeq())
        assertEquals(3L, buffer.getNewestPresentSeq())

        // Verify content retrieved in order
        assertArrayEquals(pcm1, buffer.getFrame(1L))
        assertArrayEquals(pcm2, buffer.getFrame(2L))
        assertArrayEquals(pcm3, buffer.getFrame(3L))
    }

    @Test
    fun testLatePacketDropped() {
        val buffer = JitterBuffer(256)
        val pcm = ShortArray(480)

        buffer.insert(10L, 500_000L, pcm)
        buffer.consume(10L)
        assertEquals(10L, buffer.consumedThrough)

        // Packet 9 is late
        assertFalse(buffer.insert(9L, 450_000L, pcm))
        assertEquals(1L, buffer.lateCount)

        // Packet 10 is late (already consumed)
        assertFalse(buffer.insert(10L, 500_000L, pcm))
        assertEquals(2L, buffer.lateCount)
    }

    @Test
    fun testDuplicatePacketDropped() {
        val buffer = JitterBuffer(256)
        val pcm = ShortArray(480)

        assertTrue(buffer.insert(15L, 750_000L, pcm))
        assertFalse("Duplicate frame must be rejected", buffer.insert(15L, 750_000L, pcm))
        assertEquals(1L, buffer.duplicateCount)
    }

    @Test
    fun testRingBufferWrapAround() {
        val buffer = JitterBuffer(256)
        val pcm = ShortArray(480)

        // Insert frames 0 to 400, consuming frames that are older than 200 frames behind
        for (seq in 0L..400L) {
            val playAt = seq * 50_000L
            assertTrue("Frame $seq should be accepted", buffer.insert(seq, playAt, pcm))
            if (seq >= 200L) {
                buffer.consume(seq - 150L)
            }
        }

        assertEquals(0L, buffer.rejectedCount)
        assertEquals(0L, buffer.lateCount)
        assertNotNull(buffer.getFrame(400L))
    }

    @Test
    fun testExcessiveJumpRejected() {
        val buffer = JitterBuffer(256)
        val pcm = ShortArray(480)

        buffer.insert(0L, 0L, pcm)
        // Jump ahead by 300 frames (> 256)
        assertFalse("Jump > 256 frames ahead must be rejected", buffer.insert(300L, 15_000_000L, pcm))
        assertEquals(1L, buffer.rejectedCount)
    }

    @Test
    fun testHostTimeMappingAndMonotonicHoldAcrossLatencyJump() {
        val buffer = JitterBuffer(256)
        val pcm = ShortArray(480)

        // Frame 0: playAt = 1,000,000 ticks
        // Frame 1: host latency stepped up! playAt = 1,300,000 ticks (normal delta is 50,000 ticks)
        buffer.insert(0L, 1_000_000L, pcm)
        buffer.insert(1L, 1_300_000L, pcm)

        // 1. Before frame 0: extrapolates
        val posBefore = buffer.tryMapHostTimeToPosition(900_000L)
        assertNotNull(posBefore)
        assertTrue(posBefore!! < 0.0)

        // 2. Exactly at frame 0: 0 samples
        val posAt0 = buffer.tryMapHostTimeToPosition(1_000_000L)
        assertEquals(0.0, posAt0!!, 1e-4)

        // 3. During nominal progress (5 ms later = 1,050,000 ticks):
        // 50,000 * 0.0048 = 240 samples
        val posNominal = buffer.tryMapHostTimeToPosition(1_050_000L)
        assertEquals(240.0, posNominal!!, 1e-4)

        // 4. During latency gap hold (e.g. at 1,150,000 ticks):
        // Nominal would be 0 + 150,000 * 0.0048 = 720 samples,
        // BUT hold clamp must hold at frame 1 start (seq 1 * 240 = 240 samples)!
        val posHold = buffer.tryMapHostTimeToPosition(1_150_000L)
        assertEquals(240.0, posHold!!, 1e-4)

        // 5. When frame 1 playAt arrives (1,300,000 ticks):
        val posAt1 = buffer.tryMapHostTimeToPosition(1_300_000L)
        assertEquals(240.0, posAt1!!, 1e-4)

        // 6. Beyond frame 1: extrapolates forward
        val posAfter = buffer.tryMapHostTimeToPosition(1_350_000L)
        assertEquals(480.0, posAfter!!, 1e-4)
    }
}
