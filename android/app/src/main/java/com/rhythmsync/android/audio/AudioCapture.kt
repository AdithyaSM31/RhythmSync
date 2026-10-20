package com.rhythmsync.android.audio

import android.annotation.SuppressLint
import android.media.AudioFormat as AndroidAudioFormat
import android.media.AudioRecord
import android.media.AudioTimestamp
import android.media.MediaRecorder
import android.os.Process
import android.util.Log
import com.rhythmsync.android.dsp.AudioFormat
import com.rhythmsync.android.protocol.AudioPacket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Host audio capture engine using Android [AudioRecord].
 *
 * Captures 48 kHz stereo 16-bit PCM audio from the device microphone (or audio source),
 * partitions into 5 ms (240 sample) frames, stamps with host session timestamps + target latency,
 * and passes framed packets to [onFrameCaptured].
 *
 * If microphone permission is absent or hardware capture fails, it automatically falls back
 * to [ToneGenerator] for uninterrupted session operation.
 */
class AudioCapture(
    val sessionId: Long,
    var targetLatencyMs: Int = 200,
    private val audioRecordFactory: (() -> AudioRecord)? = null,
    val onFrameCaptured: (AudioPacket) -> Unit
) {
    companion object {
        private const val TAG = "AudioCapture"
    }

    private var audioRecord: AudioRecord? = null
    private var captureThread: Thread? = null
    private val isRunning = AtomicBoolean(false)
    val toneGenerator = ToneGenerator()

    var isUsingFallbackTone: Boolean = false
        private set

    var sequence: Long = 0L
        private set

    private object SafeLog {
        fun i(tag: String, msg: String) {
            try {
                Log.i(tag, msg)
            } catch (_: Throwable) {
                println("[$tag] $msg")
            }
        }

        fun w(tag: String, msg: String, tr: Throwable? = null) {
            try {
                Log.w(tag, msg, tr)
            } catch (_: Throwable) {
                println("[$tag] $msg ${tr ?: ""}")
            }
        }

        fun e(tag: String, msg: String, tr: Throwable? = null) {
            try {
                Log.e(tag, msg, tr)
            } catch (_: Throwable) {
                System.err.println("[$tag] $msg ${tr ?: ""}")
            }
        }
    }

    /**
     * Synthesizes and returns a single fallback 5 ms [AudioPacket] without starting background threads.
     */
    @Synchronized
    fun generateFallbackPacket(): AudioPacket {
        val nowTicks = System.nanoTime() / 100L
        val playAt = nowTicks + (targetLatencyMs * AudioFormat.TICKS_PER_MS)
        return toneGenerator.generatePacket(sessionId, sequence++, playAt)
    }

    @SuppressLint("MissingPermission")
    @Synchronized
    fun start() {
        if (isRunning.get()) return

        sequence = 0L
        isUsingFallbackTone = false

        if (audioRecordFactory != null) {
            try {
                val record = audioRecordFactory.invoke()
                if (record.state != AudioRecord.STATE_INITIALIZED) {
                    SafeLog.w(TAG, "Custom AudioRecord failed initialization. Falling back to ToneGenerator.")
                    isUsingFallbackTone = true
                    try { record.release() } catch (_: Throwable) {}
                } else {
                    audioRecord = record
                    record.startRecording()
                }
            } catch (e: Throwable) {
                SafeLog.w(TAG, "Custom AudioRecord setup error. Falling back to ToneGenerator.", e)
                isUsingFallbackTone = true
            }
        } else {
            val minBufSize = try {
                AudioRecord.getMinBufferSize(
                    AudioFormat.SAMPLE_RATE,
                    AndroidAudioFormat.CHANNEL_IN_STEREO,
                    AndroidAudioFormat.ENCODING_PCM_16BIT
                )
            } catch (_: Throwable) {
                AudioFormat.PCM_BYTES * 4
            }
            val bufferSize = maxOf(minBufSize, AudioFormat.PCM_BYTES * 4)

            try {
                val record = AudioRecord(
                    MediaRecorder.AudioSource.MIC,
                    AudioFormat.SAMPLE_RATE,
                    AndroidAudioFormat.CHANNEL_IN_STEREO,
                    AndroidAudioFormat.ENCODING_PCM_16BIT,
                    bufferSize
                )

                if (record.state != AudioRecord.STATE_INITIALIZED) {
                    SafeLog.w(TAG, "AudioRecord failed to initialize. Falling back to ToneGenerator.")
                    isUsingFallbackTone = true
                    try { record.release() } catch (_: Throwable) {}
                } else {
                    audioRecord = record
                    record.startRecording()
                }
            } catch (e: SecurityException) {
                SafeLog.w(TAG, "RECORD_AUDIO permission denied. Falling back to ToneGenerator.", e)
                isUsingFallbackTone = true
            } catch (e: Throwable) {
                SafeLog.w(TAG, "AudioRecord setup error. Falling back to ToneGenerator.", e)
                isUsingFallbackTone = true
            }
        }

        isRunning.set(true)
        captureThread = Thread({ captureLoop() }, "RhythmSync-AudioCapture").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
        SafeLog.i(TAG, "AudioCapture started (fallbackTone=$isUsingFallbackTone)")
    }

    @Synchronized
    fun stop() {
        if (!isRunning.get()) return
        isRunning.set(false)

        captureThread?.interrupt()
        try {
            captureThread?.join(1000)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        }
        captureThread = null

        audioRecord?.let {
            try {
                it.stop()
                it.release()
            } catch (e: Exception) {
                SafeLog.w(TAG, "Error releasing AudioRecord", e)
            }
        }
        audioRecord = null
        SafeLog.i(TAG, "AudioCapture stopped")
    }

    private fun captureLoop() {
        try {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        } catch (_: Throwable) {}

        val shortBuffer = ShortArray(AudioFormat.FRAME_FLOATS)
        val audioTimestamp = AudioTimestamp()
        var nextFrameTimeNs = System.nanoTime()

        while (isRunning.get()) {
            if (isUsingFallbackTone || audioRecord == null) {
                // Synthetic continuous 440 Hz test tone generated at 5 ms pacing
                val nowTicks = System.nanoTime() / 100L
                val playAt = nowTicks + (targetLatencyMs * AudioFormat.TICKS_PER_MS)

                val packet = toneGenerator.generatePacket(sessionId, sequence++, playAt)
                onFrameCaptured(packet)

                nextFrameTimeNs += 5_000_000L // 5 ms in nanoseconds
                val sleepNs = nextFrameTimeNs - System.nanoTime()
                if (sleepNs > 0) {
                    val sleepMs = sleepNs / 1_000_000L
                    val sleepRemainingNs = (sleepNs % 1_000_000L).toInt()
                    try {
                        Thread.sleep(sleepMs, sleepRemainingNs)
                    } catch (_: InterruptedException) {
                        break
                    }
                } else if (sleepNs < -50_000_000L) {
                    // Fell more than 50 ms behind, resync clock
                    nextFrameTimeNs = System.nanoTime()
                }
            } else {
                val record = audioRecord ?: break
                var shortsRead = 0
                while (shortsRead < AudioFormat.FRAME_FLOATS && isRunning.get()) {
                    val read = record.read(
                        shortBuffer,
                        shortsRead,
                        AudioFormat.FRAME_FLOATS - shortsRead
                    )
                    if (read > 0) {
                        shortsRead += read
                    } else if (read == AudioRecord.ERROR_INVALID_OPERATION || read == AudioRecord.ERROR_BAD_VALUE) {
                        SafeLog.e(TAG, "AudioRecord read error ($read), switching to fallback tone")
                        isUsingFallbackTone = true
                        break
                    }
                }

                if (shortsRead == AudioFormat.FRAME_FLOATS) {
                    val captureTicks = try {
                        if (record.getTimestamp(audioTimestamp, AudioTimestamp.TIMEBASE_MONOTONIC) == AudioRecord.SUCCESS) {
                            audioTimestamp.nanoTime / 100L
                        } else {
                            System.nanoTime() / 100L
                        }
                    } catch (_: Throwable) {
                        System.nanoTime() / 100L
                    }

                    val playAt = captureTicks + (targetLatencyMs * AudioFormat.TICKS_PER_MS)

                    val packet = AudioPacket(
                        sessionId = sessionId,
                        sequence = sequence++,
                        playAt = playAt,
                        frameSamples = AudioFormat.FRAME_SAMPLES,
                        pcmData = shortBuffer.clone()
                    )
                    onFrameCaptured(packet)
                }
            }
        }
    }
}
