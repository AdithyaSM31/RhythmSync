package com.rhythmsync.android.audio

import com.rhythmsync.android.dsp.AudioFormat
import com.rhythmsync.android.dsp.EqProcessor
import com.rhythmsync.android.sync.ClockEstimator
import com.rhythmsync.android.sync.JitterBuffer
import com.rhythmsync.android.sync.PlaybackScheduler
import org.junit.Assert.*
import org.junit.Test

class AudioTrackPlayerTest {

    @Test
    fun testVolumeClamping() {
        val jitterBuffer = JitterBuffer()
        val scheduler = PlaybackScheduler()
        val eqProcessor = EqProcessor()
        val clockEstimator = ClockEstimator()

        val player = AudioTrackPlayer(jitterBuffer, scheduler, eqProcessor, clockEstimator)

        player.volume = 1.5f
        assertEquals(1.0f, player.volume, 1e-6f)

        player.volume = -0.5f
        assertEquals(0.0f, player.volume, 1e-6f)

        player.volume = 0.75f
        assertEquals(0.75f, player.volume, 1e-6f)
    }

    @Test
    fun testRenderFrameEmptyBufferConcealment() {
        val jitterBuffer = JitterBuffer()
        val scheduler = PlaybackScheduler()
        val eqProcessor = EqProcessor()
        val clockEstimator = ClockEstimator()

        val player = AudioTrackPlayer(jitterBuffer, scheduler, eqProcessor, clockEstimator)

        val outBytes = ByteArray(AudioFormat.PCM_BYTES)
        val byteCount = player.renderNextFrame(outBytes)
        assertEquals(AudioFormat.PCM_BYTES, byteCount)

        // In an empty buffer with no good frames, concealed audio is pure silence (+/- 1 LSB from TPDF dither)
        for (i in 0 until AudioFormat.FRAME_FLOATS) {
            val b0 = outBytes[2 * i].toInt() and 0xFF
            val b1 = outBytes[2 * i + 1].toInt()
            val sample = ((b1 shl 8) or b0).toShort()
            assertTrue("Sample $i ($sample) should be within 1 LSB of silence", Math.abs(sample.toInt()) <= 1)
        }
    }

    @Test
    fun testRenderFrameWithJitterBufferContent() {
        val jitterBuffer = JitterBuffer()
        val scheduler = PlaybackScheduler()
        val eqProcessor = EqProcessor()
        val clockEstimator = ClockEstimator()
        val toneGenerator = ToneGenerator()

        // Insert frames 0 to 10 into JitterBuffer
        for (seq in 0L..10L) {
            val playAt = seq * AudioFormat.FRAME_TICKS
            val pcm = toneGenerator.generateFrameShorts()
            jitterBuffer.insert(seq, playAt, pcm)
        }

        val player = AudioTrackPlayer(jitterBuffer, scheduler, eqProcessor, clockEstimator)
        val outBytes = ByteArray(AudioFormat.PCM_BYTES)
        val byteCount = player.renderNextFrame(outBytes)

        assertEquals(AudioFormat.PCM_BYTES, byteCount)
        // Check that non-zero audio samples were rendered
        val hasNonZero = outBytes.any { it != 0.toByte() }
        assertTrue("Rendered output should contain non-zero audio samples", hasNonZero)
    }

    @Test
    fun testVolumeDezippering() {
        val jitterBuffer = JitterBuffer()
        val scheduler = PlaybackScheduler()
        val eqProcessor = EqProcessor()
        val clockEstimator = ClockEstimator()
        val toneGenerator = ToneGenerator()

        // Pre-fill buffer
        for (seq in 0L..10L) {
            val playAt = seq * AudioFormat.FRAME_TICKS
            val pcm = toneGenerator.generateFrameShorts()
            jitterBuffer.insert(seq, playAt, pcm)
        }

        val player = AudioTrackPlayer(jitterBuffer, scheduler, eqProcessor, clockEstimator)
        player.volume = 1.0f

        val out1 = ByteArray(AudioFormat.PCM_BYTES)
        player.renderNextFrame(out1)

        // Drop volume to 0.0 -> linear ramp across 240 frames
        player.volume = 0.0f
        val out2 = ByteArray(AudioFormat.PCM_BYTES)
        player.renderNextFrame(out2)

        // The end of out2 must be completely silent (0 gain)
        val endShortL = ((out2[AudioFormat.PCM_BYTES - 4].toInt() and 0xFF) or (out2[AudioFormat.PCM_BYTES - 3].toInt() shl 8)).toShort()
        val endShortR = ((out2[AudioFormat.PCM_BYTES - 2].toInt() and 0xFF) or (out2[AudioFormat.PCM_BYTES - 1].toInt() shl 8)).toShort()
        assertEquals(0.toShort(), endShortL)
        assertEquals(0.toShort(), endShortR)
    }
}
