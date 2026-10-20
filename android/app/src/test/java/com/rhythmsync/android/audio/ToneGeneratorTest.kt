package com.rhythmsync.android.audio

import com.rhythmsync.android.dsp.AudioFormat
import org.junit.Assert.*
import org.junit.Test
import kotlin.math.abs

class ToneGeneratorTest {

    @Test
    fun testDefaultParameters() {
        val generator = ToneGenerator()
        assertEquals(AudioFormat.SAMPLE_RATE, generator.sampleRate)
        assertEquals(440.0, generator.frequencyHz, 1e-6)
        assertEquals(0.5f, generator.amplitude, 1e-6f)
        assertEquals(0.0, generator.currentPhase, 1e-6)
    }

    @Test
    fun testGenerateFrameFloatsDimensionsAndStereoEquality() {
        val generator = ToneGenerator()
        val floats = generator.generateFrameFloats()

        assertEquals(AudioFormat.FRAME_FLOATS, floats.size) // 480 floats

        // Interleaved stereo: Left == Right at every sample
        for (i in 0 until AudioFormat.FRAME_SAMPLES) {
            val left = floats[2 * i]
            val right = floats[2 * i + 1]
            assertEquals("Sample $i stereo mismatch", left, right, 1e-6f)
        }
    }

    @Test
    fun testPeakAmplitude() {
        val generator = ToneGenerator(amplitude = 0.5f)
        var maxObserved = 0f
        var minObserved = 0f

        // Run 20 frames (100 ms) to observe full sine wave cycles
        val buf = FloatArray(AudioFormat.FRAME_FLOATS)
        for (f in 0 until 20) {
            generator.generateFrameFloats(buf)
            for (i in 0 until AudioFormat.FRAME_FLOATS) {
                if (buf[i] > maxObserved) maxObserved = buf[i]
                if (buf[i] < minObserved) minObserved = buf[i]
            }
        }

        assertTrue("Max amplitude $maxObserved should not exceed 0.5", maxObserved <= 0.5f + 1e-5f)
        assertTrue("Max amplitude $maxObserved should reach close to 0.5", maxObserved > 0.49f)
        assertTrue("Min amplitude $minObserved should reach close to -0.5", minObserved < -0.49f)
        assertTrue("Min amplitude $minObserved should not fall below -0.5", minObserved >= -0.5f - 1e-5f)
    }

    @Test
    fun testSineFrequencyZeroCrossings() {
        val generator = ToneGenerator(sampleRate = 48000, frequencyHz = 440.0, amplitude = 0.5f)
        var zeroCrossings = 0
        var previousSample = 0f
        var isFirst = true

        val buf = FloatArray(AudioFormat.FRAME_FLOATS)
        // 200 frames * 5 ms = 1000 ms = 1 second
        for (f in 0 until 200) {
            generator.generateFrameFloats(buf)
            for (i in 0 until AudioFormat.FRAME_SAMPLES) {
                val currentSample = buf[2 * i] // Left channel
                if (!isFirst) {
                    if ((previousSample < 0f && currentSample >= 0f) || (previousSample >= 0f && currentSample < 0f)) {
                        zeroCrossings++
                    }
                } else {
                    isFirst = false
                }
                previousSample = currentSample
            }
        }

        // A 440 Hz wave crosses zero 2 * 440 = 880 times per second (+/- 1 due to sample discretization)
        assertTrue("Zero crossing count $zeroCrossings should be in 879..881", zeroCrossings in 879..881)
    }

    @Test
    fun testPhaseContinuityAcrossFrameBoundaries() {
        val generator = ToneGenerator(sampleRate = 48000, frequencyHz = 440.0, amplitude = 0.5f)
        val frame1 = FloatArray(AudioFormat.FRAME_FLOATS)
        val frame2 = FloatArray(AudioFormat.FRAME_FLOATS)

        val phaseStep = (2.0 * Math.PI * 440.0) / 48000.0
        // Maximum slope of 0.5 * sin(wt) is amplitude * w = 0.5 * (2*pi*440/48000) = ~0.0288
        val maxSampleDelta = (0.5f * phaseStep.toFloat()) * 1.05f

        for (testIter in 0 until 10) {
            generator.generateFrameFloats(frame1)
            generator.generateFrameFloats(frame2)

            val lastSampleFrame1 = frame1[AudioFormat.FRAME_FLOATS - 2] // Left of sample 239
            val firstSampleFrame2 = frame2[0] // Left of sample 0

            val boundaryDelta = abs(firstSampleFrame2 - lastSampleFrame1)
            assertTrue(
                "Boundary jump ($boundaryDelta) must not exceed maximum continuous step ($maxSampleDelta)",
                boundaryDelta <= maxSampleDelta
            )
        }
    }

    @Test
    fun testGenerateFrameShortsAndPcmScale() {
        val generator = ToneGenerator(amplitude = 0.5f)
        val shorts = generator.generateFrameShorts()

        assertEquals(AudioFormat.FRAME_FLOATS, shorts.size)

        var maxShort: Short = 0
        var minShort: Short = 0

        for (i in 0 until AudioFormat.FRAME_SAMPLES) {
            val left = shorts[2 * i]
            val right = shorts[2 * i + 1]
            assertEquals("Sample $i stereo mismatch", left, right)

            if (left > maxShort) maxShort = left
            if (left < minShort) minShort = left
        }

        // 0.5 * 32767 ≈ 16383
        assertTrue("Max short $maxShort should be near 16383", maxShort in 15000..16384)
    }

    @Test
    fun testGenerateFrameBytesLittleEndian() {
        val generator = ToneGenerator()
        val shorts = ShortArray(AudioFormat.FRAME_FLOATS)
        val bytes = ByteArray(AudioFormat.PCM_BYTES)

        generator.resetPhase()
        generator.generateFrameShorts(shorts)

        generator.resetPhase()
        generator.generateFrameBytes(bytes)

        // Verify little-endian decoding
        for (i in 0 until AudioFormat.FRAME_FLOATS) {
            val b0 = bytes[2 * i].toInt() and 0xFF
            val b1 = bytes[2 * i + 1].toInt()
            val reconstructed = ((b1 shl 8) or b0).toShort()
            assertEquals("Sample $i byte decoding mismatch", shorts[i], reconstructed)
        }
    }

    @Test
    fun testGeneratePacket() {
        val generator = ToneGenerator()
        val packet = generator.generatePacket(sessionId = 12345L, sequence = 42L, playAt = 999999L)

        assertEquals(12345L, packet.sessionId)
        assertEquals(42L, packet.sequence)
        assertEquals(999999L, packet.playAt)
        assertEquals(AudioFormat.FRAME_SAMPLES, packet.frameSamples)
        assertEquals(AudioFormat.FRAME_FLOATS, packet.pcmData.size)
    }

    @Test
    fun testResetPhase() {
        val generator = ToneGenerator()
        val buf = FloatArray(AudioFormat.FRAME_FLOATS)
        generator.generateFrameFloats(buf)

        assertTrue("Phase should advance after frame generation", generator.currentPhase > 0.0)

        generator.resetPhase()
        assertEquals(0.0, generator.currentPhase, 1e-9)

        generator.generateFrameFloats(buf)
        // At phase 0.0, sin(0) == 0.0f
        assertEquals(0.0f, buf[0], 1e-6f)
    }

    @Test
    fun testBufferUndersizedThrows() {
        val generator = ToneGenerator()

        assertThrows(IllegalArgumentException::class.java) {
            generator.generateFrameFloats(FloatArray(479))
        }
        assertThrows(IllegalArgumentException::class.java) {
            generator.generateFrameShorts(ShortArray(479))
        }
        assertThrows(IllegalArgumentException::class.java) {
            generator.generateFrameBytes(ByteArray(959))
        }
    }
}
