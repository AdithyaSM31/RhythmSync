package com.rhythmsync.android.audio

import android.media.AudioAttributes
import android.media.AudioFormat as AndroidAudioFormat
import android.media.AudioTrack
import android.os.Process
import android.util.Log
import com.rhythmsync.android.dsp.AudioFormat
import com.rhythmsync.android.dsp.EqProcessor
import com.rhythmsync.android.sync.ClockEstimator
import com.rhythmsync.android.sync.JitterBuffer
import com.rhythmsync.android.sync.PlaybackScheduler
import java.util.Random
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.floor

/**
 * Real-time low-latency audio playback engine utilizing Android [AudioTrack].
 *
 * Pulls timestamped frames from [JitterBuffer], performs Catmull-Rom resampling and PI drift steering
 * via [PlaybackScheduler], applies 10-band equalization via [EqProcessor], applies smooth volume scaling,
 * converts to 16-bit PCM with TPDF dither, and writes to [AudioTrack] in streaming mode.
 */
class AudioTrackPlayer(
    val jitterBuffer: JitterBuffer,
    val scheduler: PlaybackScheduler,
    val eqProcessor: EqProcessor,
    val clockEstimator: ClockEstimator,
    val targetLatencyMs: Int = 200,
    private val audioTrackFactory: (() -> AudioTrack)? = null
) {
    companion object {
        private const val TAG = "AudioTrackPlayer"
        const val RENDER_BURST_FRAMES = AudioFormat.FRAME_SAMPLES // 240 frames = 5 ms
        const val RENDER_BURST_FLOATS = AudioFormat.FRAME_FLOATS // 480 floats
        const val RENDER_BURST_BYTES = AudioFormat.PCM_BYTES // 960 bytes
    }

    private var audioTrack: AudioTrack? = null
    private var playbackThread: Thread? = null
    private val isRunning = AtomicBoolean(false)
    private val isPaused = AtomicBoolean(false)

    // Preallocated working buffers to ensure zero GC allocation on the audio render thread
    private val renderFloats = FloatArray(RENDER_BURST_FLOATS)
    private val currentGoodFrameFloats = FloatArray(RENDER_BURST_FLOATS)
    private val stagingFloats = FloatArray(RENDER_BURST_FLOATS * 4) // 4 frames for Catmull-Rom taps
    private val pcmBytes = ByteArray(RENDER_BURST_BYTES)
    private val ditherRng = Random(1337)

    // Unwrapped playback head tracking (handles 32-bit unsigned overflow)
    private var lastRawHeadPos: Long = 0L
    private var headPosWraps: Long = 0L

    // Volume scaling with linear de-zippering
    @Volatile
    var volume: Float = 1.0f
        set(value) {
            field = value.coerceIn(0.0f, 1.0f)
        }

    private var appliedVolume: Float = 1.0f

    // Underrun and diagnostic metrics
    var totalFramesWritten: Long = 0L
        private set

    var underrunCount: Int = 0
        private set

    val isPlaying: Boolean
        get() = isRunning.get() && !isPaused.get()

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
    }

    @Synchronized
    fun start() {
        if (isRunning.get()) return

        val track = if (audioTrackFactory != null) {
            audioTrackFactory.invoke()
        } else {
            val minBufSize = AudioTrack.getMinBufferSize(
                AudioFormat.SAMPLE_RATE,
                AndroidAudioFormat.CHANNEL_OUT_STEREO,
                AndroidAudioFormat.ENCODING_PCM_16BIT
            )
            // Ensure at least 20 ms buffer depth for low latency without under-buffering
            val bufferSize = maxOf(minBufSize, RENDER_BURST_BYTES * 4)

            val attributes = AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_MEDIA)
                .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                .build()

            val format = AndroidAudioFormat.Builder()
                .setSampleRate(AudioFormat.SAMPLE_RATE)
                .setChannelMask(AndroidAudioFormat.CHANNEL_OUT_STEREO)
                .setEncoding(AndroidAudioFormat.ENCODING_PCM_16BIT)
                .build()

            AudioTrack.Builder()
                .setAudioAttributes(attributes)
                .setAudioFormat(format)
                .setBufferSizeInBytes(bufferSize)
                .setTransferMode(AudioTrack.MODE_STREAM)
                .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                .build()
        }

        audioTrack = track
        totalFramesWritten = 0L
        appliedVolume = volume
        lastRawHeadPos = 0L
        headPosWraps = 0L

        try {
            track.play()
        } catch (e: Exception) {
            SafeLog.w(TAG, "AudioTrack play() failed", e)
        }

        isRunning.set(true)
        isPaused.set(false)

        playbackThread = Thread({ audioRenderLoop() }, "RhythmSync-AudioRender").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
        SafeLog.i(TAG, "AudioTrackPlayer started")
    }

    @Synchronized
    fun pause() {
        if (!isRunning.get() || isPaused.get()) return
        isPaused.set(true)
        try {
            audioTrack?.pause()
        } catch (e: Exception) {
            SafeLog.w(TAG, "Error pausing AudioTrack", e)
        }
        SafeLog.i(TAG, "AudioTrackPlayer paused")
    }

    @Synchronized
    fun resume() {
        if (!isRunning.get() || !isPaused.get()) return
        isPaused.set(false)
        try {
            audioTrack?.play()
        } catch (e: Exception) {
            SafeLog.w(TAG, "Error resuming AudioTrack", e)
        }
        SafeLog.i(TAG, "AudioTrackPlayer resumed")
    }

    @Synchronized
    fun stop() {
        if (!isRunning.get()) return
        isRunning.set(false)
        isPaused.set(false)

        playbackThread?.interrupt()
        try {
            playbackThread?.join(1000)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        }
        playbackThread = null

        audioTrack?.let {
            try {
                it.stop()
                it.flush()
                it.release()
            } catch (e: Exception) {
                SafeLog.w(TAG, "Error releasing AudioTrack", e)
            }
        }
        audioTrack = null
        SafeLog.i(TAG, "AudioTrackPlayer stopped")
    }

    /**
     * Renders a single 5 ms audio burst (240 stereo samples = 960 bytes) through the DSP chain,
     * writing the result to [outputPcm].
     *
     * Returns the number of bytes populated (always [RENDER_BURST_BYTES]).
     */
    fun renderNextFrame(outputPcm: ByteArray = pcmBytes): Int {
        require(outputPcm.size >= RENDER_BURST_BYTES) {
            "Output PCM buffer must have size >= $RENDER_BURST_BYTES"
        }

        // 1. Calculate device heard time: delay from write cursor to hardware DAC playback
        val currentHead = audioTrack?.let { track ->
            try {
                val raw = track.playbackHeadPosition.toLong() and 0xFFFFFFFFL
                getUnwrappedHeadPosition(raw)
            } catch (_: Throwable) {
                totalFramesWritten
            }
        } ?: totalFramesWritten

        val paddingFrames = (totalFramesWritten - currentHead).coerceAtLeast(0L)
        val delayTicks = paddingFrames * AudioFormat.TICKS_PER_SECOND / AudioFormat.SAMPLE_RATE
        val nowClientTicks = System.nanoTime() / 100L
        val heardClientTicks = nowClientTicks + delayTicks
        val hostTicks = clockEstimator.clientToHost(heardClientTicks) -
                (scheduler.currentOffsetMs * AudioFormat.TICKS_PER_MS).toLong()

        // 2. Query JitterBuffer for stream target position
        val targetPosition = jitterBuffer.tryMapHostTimeToPosition(hostTicks)

        if (targetPosition == null) {
            // Buffer empty or waiting for stream: conceal missing frame
            scheduler.concealMissingFrame(renderFloats)
        } else {
            scheduler.checkAndPerformResync(targetPosition)
            if (scheduler.isAwaitingContent) {
                renderFloats.fill(0f)
            } else {
                val err = targetPosition - scheduler.readPosition
                val stepRatio = scheduler.updateDriftController(err)

                val curSeq = floor(scheduler.readPosition / AudioFormat.FRAME_SAMPLES).toLong()
                val f0 = jitterBuffer.getFrame(curSeq - 1)
                val f1 = jitterBuffer.getFrame(curSeq)
                val f2 = jitterBuffer.getFrame(curSeq + 1)
                val f3 = jitterBuffer.getFrame(curSeq + 2)

                if (f1 != null && f2 != null) {
                    for (i in 0 until AudioFormat.FRAME_FLOATS) {
                        currentGoodFrameFloats[i] = AudioFormat.int16ToFloat(f1[i])
                    }
                    scheduler.recordGoodFrame(currentGoodFrameFloats)
                    scheduler.applyRecoveryFadeInIfNeeded(currentGoodFrameFloats)

                    fillStaging(f0, f2, f3)

                    val rawFractionalPos = (scheduler.readPosition % AudioFormat.FRAME_SAMPLES)
                    val fractionalPos = if (rawFractionalPos < 0.0) {
                        rawFractionalPos + AudioFormat.FRAME_SAMPLES
                    } else {
                        rawFractionalPos
                    }

                    scheduler.resampleStereo(
                        input = stagingFloats,
                        inputOffsetFrames = AudioFormat.FRAME_SAMPLES,
                        startFractionalFrame = fractionalPos,
                        stepRatio = stepRatio,
                        output = renderFloats,
                        outputFrames = RENDER_BURST_FRAMES
                    )
                    scheduler.stepReadPosition(RENDER_BURST_FRAMES * stepRatio)
                    if (curSeq > 2) {
                        jitterBuffer.consume(curSeq - 2)
                    }
                } else {
                    scheduler.concealMissingFrame(renderFloats)
                }
            }
        }

        // 3. Process 10-band Equalizer
        eqProcessor.process(renderFloats, RENDER_BURST_FRAMES)

        // 4. Smooth volume scaling with linear de-zippering ramp
        val targetVol = volume
        val startVol = appliedVolume
        if (startVol == targetVol) {
            if (targetVol != 1.0f) {
                for (i in 0 until RENDER_BURST_FLOATS) {
                    renderFloats[i] *= targetVol
                }
            }
        } else {
            val delta = targetVol - startVol
            for (f in 0 until RENDER_BURST_FRAMES) {
                val g = startVol + delta * ((f + 1).toFloat() / RENDER_BURST_FRAMES.toFloat())
                renderFloats[2 * f] *= g
                renderFloats[2 * f + 1] *= g
            }
            appliedVolume = targetVol
        }

        // 5. Convert Float [-1.0, 1.0] to 16-bit PCM Little-Endian bytes with TPDF dithering
        var byteIdx = 0
        for (i in 0 until RENDER_BURST_FLOATS) {
            val sampleShort = AudioFormat.floatToInt16Dithered(renderFloats[i], ditherRng).toInt()
            outputPcm[byteIdx++] = (sampleShort and 0xFF).toByte()
            outputPcm[byteIdx++] = ((sampleShort shr 8) and 0xFF).toByte()
        }

        return RENDER_BURST_BYTES
    }

    private fun audioRenderLoop() {
        try {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        } catch (_: Throwable) {}

        val track = audioTrack ?: return

        while (isRunning.get()) {
            if (isPaused.get()) {
                try {
                    Thread.sleep(10)
                } catch (_: InterruptedException) {
                    break
                }
                continue
            }

            val byteCount = renderNextFrame(pcmBytes)

            // Write to AudioTrack in blocking mode (paces render loop to hardware audio clock)
            val written = try {
                track.write(pcmBytes, 0, byteCount, AudioTrack.WRITE_BLOCKING)
            } catch (e: Exception) {
                SafeLog.w(TAG, "AudioTrack write exception", e)
                -1
            }

            if (written > 0) {
                totalFramesWritten += (written / (AudioFormat.CHANNELS * 2)) // 4 bytes per stereo frame
            }

            // Track hardware underrun count
            try {
                underrunCount = track.underrunCount
            } catch (_: Throwable) {}
        }
    }

    private fun getUnwrappedHeadPosition(raw: Long): Long {
        if (raw < lastRawHeadPos && (lastRawHeadPos - raw) > 0x7FFFFFFFL) {
            headPosWraps++
        }
        lastRawHeadPos = raw
        return (headPosWraps shl 32) or raw
    }

    private fun fillStaging(f0: ShortArray?, f2: ShortArray, f3: ShortArray?) {
        val s = AudioFormat.FRAME_FLOATS
        // Slot 0 (curSeq - 1)
        if (f0 != null) {
            for (i in 0 until s) stagingFloats[i] = AudioFormat.int16ToFloat(f0[i])
        } else {
            for (i in 0 until s) stagingFloats[i] = currentGoodFrameFloats[i]
        }
        // Slot 1 (curSeq): contains f1 with any recovery fade-in already applied
        for (i in 0 until s) stagingFloats[s + i] = currentGoodFrameFloats[i]

        // Slot 2 (curSeq + 1)
        for (i in 0 until s) stagingFloats[2 * s + i] = AudioFormat.int16ToFloat(f2[i])

        // Slot 3 (curSeq + 2)
        if (f3 != null) {
            for (i in 0 until s) stagingFloats[3 * s + i] = AudioFormat.int16ToFloat(f3[i])
        } else {
            for (i in 0 until s) stagingFloats[3 * s + i] = AudioFormat.int16ToFloat(f2[i])
        }
    }
}
