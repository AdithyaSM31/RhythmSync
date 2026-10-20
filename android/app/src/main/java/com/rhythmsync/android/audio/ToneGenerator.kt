package com.rhythmsync.android.audio

import com.rhythmsync.android.dsp.AudioFormat
import com.rhythmsync.android.protocol.AudioPacket
import kotlin.math.sin

/**
 * Continuous-phase sine wave tone generator producing 5 ms (240 sample / 480 float / 960 byte)
 * audio frames at 48 kHz stereo.
 *
 * Essential for testing, synchronization validation, and fallback host streaming when microphone
 * capture is unavailable.
 */
class ToneGenerator(
    val sampleRate: Int = AudioFormat.SAMPLE_RATE,
    val frequencyHz: Double = DEFAULT_FREQUENCY,
    val amplitude: Float = DEFAULT_AMPLITUDE
) {
    companion object {
        const val DEFAULT_FREQUENCY = 440.0
        const val DEFAULT_AMPLITUDE = 0.5f
    }

    private val phaseStep: Double = (2.0 * Math.PI * frequencyHz) / sampleRate.toDouble()
    private val twoPi: Double = 2.0 * Math.PI

    @Volatile
    private var phase: Double = 0.0

    /**
     * Current phase in radians [0, 2*PI).
     */
    val currentPhase: Double
        get() = phase

    /**
     * Generates a 5 ms frame of interleaved stereo float samples (480 floats).
     * Maintains strict phase continuity across consecutive invocations.
     */
    @Synchronized
    fun generateFrameFloats(
        destination: FloatArray = FloatArray(AudioFormat.FRAME_FLOATS)
    ): FloatArray {
        require(destination.size >= AudioFormat.FRAME_FLOATS) {
            "Destination float array must have size >= ${AudioFormat.FRAME_FLOATS} (got ${destination.size})"
        }

        var currentPhase = phase
        for (i in 0 until AudioFormat.FRAME_SAMPLES) {
            val sample = (amplitude * sin(currentPhase)).toFloat()
            destination[2 * i] = sample
            destination[2 * i + 1] = sample

            currentPhase += phaseStep
            while (currentPhase >= twoPi) {
                currentPhase -= twoPi
            }
        }
        phase = currentPhase
        return destination
    }

    /**
     * Generates a 5 ms frame of interleaved stereo 16-bit PCM shorts (480 shorts).
     */
    @Synchronized
    fun generateFrameShorts(
        destination: ShortArray = ShortArray(AudioFormat.FRAME_FLOATS)
    ): ShortArray {
        require(destination.size >= AudioFormat.FRAME_FLOATS) {
            "Destination short array must have size >= ${AudioFormat.FRAME_FLOATS} (got ${destination.size})"
        }

        var currentPhase = phase
        for (i in 0 until AudioFormat.FRAME_SAMPLES) {
            val sampleFloat = (amplitude * sin(currentPhase)).toFloat()
            val sampleShort = AudioFormat.floatToInt16(sampleFloat)
            destination[2 * i] = sampleShort
            destination[2 * i + 1] = sampleShort

            currentPhase += phaseStep
            while (currentPhase >= twoPi) {
                currentPhase -= twoPi
            }
        }
        phase = currentPhase
        return destination
    }

    /**
     * Generates a 5 ms frame of interleaved stereo 16-bit PCM Little-Endian bytes (960 bytes).
     */
    @Synchronized
    fun generateFrameBytes(
        destination: ByteArray = ByteArray(AudioFormat.PCM_BYTES)
    ): ByteArray {
        require(destination.size >= AudioFormat.PCM_BYTES) {
            "Destination byte array must have size >= ${AudioFormat.PCM_BYTES} (got ${destination.size})"
        }

        var currentPhase = phase
        var byteIdx = 0
        for (i in 0 until AudioFormat.FRAME_SAMPLES) {
            val sampleFloat = (amplitude * sin(currentPhase)).toFloat()
            val sampleShort = AudioFormat.floatToInt16(sampleFloat).toInt()

            val b0 = (sampleShort and 0xFF).toByte()
            val b1 = ((sampleShort shr 8) and 0xFF).toByte()

            // Left channel
            destination[byteIdx++] = b0
            destination[byteIdx++] = b1
            // Right channel
            destination[byteIdx++] = b0
            destination[byteIdx++] = b1

            currentPhase += phaseStep
            while (currentPhase >= twoPi) {
                currentPhase -= twoPi
            }
        }
        phase = currentPhase
        return destination
    }

    /**
     * Generates a complete 982-byte wire-compatible [AudioPacket] stamped with [sessionId],
     * [sequence], and [playAt].
     */
    @Synchronized
    fun generatePacket(sessionId: Long, sequence: Long, playAt: Long): AudioPacket {
        val pcm = generateFrameShorts()
        return AudioPacket(
            sessionId = sessionId,
            sequence = sequence,
            playAt = playAt,
            frameSamples = AudioFormat.FRAME_SAMPLES,
            pcmData = pcm
        )
    }

    /**
     * Resets the phase accumulator back to 0.0.
     */
    @Synchronized
    fun resetPhase() {
        phase = 0.0
    }
}
