package com.rhythmsync.android.audio

import com.rhythmsync.android.dsp.AudioFormat
import org.junit.Assert.*
import org.junit.Test

class AudioCaptureTest {

    @Test
    fun testFallbackPacketGeneration() {
        var capturedCount = 0
        val capture = AudioCapture(
            sessionId = 98765L,
            targetLatencyMs = 150,
            onFrameCaptured = { _ ->
                capturedCount++
            }
        )

        val beforeTicks = System.nanoTime() / 100L
        val p1 = capture.generateFallbackPacket()
        val afterTicks = System.nanoTime() / 100L

        assertEquals(98765L, p1.sessionId)
        assertEquals(0L, p1.sequence)
        assertEquals(AudioFormat.FRAME_SAMPLES, p1.frameSamples)
        assertEquals(AudioFormat.FRAME_FLOATS, p1.pcmData.size)

        // playAt must be roughly nowTicks + targetLatencyMs * 10_000L
        val expectedPlayAtMin = beforeTicks + 150 * AudioFormat.TICKS_PER_MS
        val expectedPlayAtMax = afterTicks + 150 * AudioFormat.TICKS_PER_MS
        assertTrue("playAt should be stamped with target latency", p1.playAt in expectedPlayAtMin..expectedPlayAtMax)

        // Next packet must advance sequence
        val p2 = capture.generateFallbackPacket()
        assertEquals(1L, p2.sequence)

        val p3 = capture.generateFallbackPacket()
        assertEquals(2L, p3.sequence)
    }

    @Test
    fun testFallbackToneSamplesNonZero() {
        val capture = AudioCapture(
            sessionId = 1111L,
            targetLatencyMs = 200,
            onFrameCaptured = {}
        )

        val packet = capture.generateFallbackPacket()
        val hasAudio = packet.pcmData.any { it != 0.toShort() }
        assertTrue("Fallback packet must contain synthesized tone data", hasAudio)
    }
}
