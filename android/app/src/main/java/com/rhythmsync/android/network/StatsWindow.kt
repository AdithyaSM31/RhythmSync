package com.rhythmsync.android.network

import com.rhythmsync.android.dsp.AudioFormat

class StatsWindow(
    val windowTicks: Long = WINDOW_TICKS
) {
    companion object {
        const val WINDOW_TICKS = 10 * AudioFormat.TICKS_PER_SECOND // 10 seconds in 100ns ticks (100_000_000L)
    }

    private data class Sample(
        val ticks: Long,
        val received: Long,
        val late: Long,
        val concealed: Long
    )

    private val samples = mutableListOf<Sample>()

    fun add(nowTicks: Long, received: Long, late: Long, concealed: Long) {
        samples.add(Sample(nowTicks, received, late, concealed))
        var drop = 0
        while (drop + 1 < samples.size && samples[drop + 1].ticks <= nowTicks - windowTicks) {
            drop++
        }
        if (drop > 0) {
            for (i in 0 until drop) {
                samples.removeAt(0)
            }
        }
    }

    val latePct: Double
        get() {
            if (samples.size < 2) return 0.0
            val dReceived = samples.last().received - samples.first().received
            val dLate = samples.last().late - samples.first().late
            return if (dReceived > 0) (100.0 * dLate) / dReceived else 0.0
        }

    val lossPct: Double
        get() {
            if (samples.size < 2) return 0.0
            val dReceived = samples.last().received - samples.first().received
            val dConcealed = samples.last().concealed - samples.first().concealed
            val total = dReceived + dConcealed
            return if (total > 0) (100.0 * dConcealed) / total else 0.0
        }
}
