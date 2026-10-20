package com.rhythmsync.android.sync

import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt

enum class SyncState {
    Connecting,
    Syncing,
    InSync,
    Degraded,
    Disconnected
}

data class ClockSample(
    val t1: Long,
    val t2: Long,
    val t3: Long,
    val t4: Long,
    val rtt: Long,
    val offset: Long,
    val clientMidpoint: Long,
    val hostMidpoint: Long
)

class ClockEstimator(
    val windowTicks: Long = 600_000_000L, // 60 seconds (100ns ticks)
    val maxSampleAgeTicks: Long = 100_000_000L, // 10 seconds
    val weightFloorTicks: Double = 5000.0 // 0.5 ms
) {
    private val samples = mutableListOf<ClockSample>()

    var sampleCount: Int = 0
        private set

    var everConverged: Boolean = false
        private set

    var isConverged: Boolean = false
        private set

    var slope: Double = 1.0
        private set

    var intercept: Double = 0.0
        private set

    var c0: Long = 0L
        private set

    var h0: Long = 0L
        private set

    var driftPpm: Double = 0.0
        private set

    var fitResidualMs: Double = 0.0
        private set

    var stdErrorMs: Double = Double.POSITIVE_INFINITY
        private set

    var lastSampleTimeTicks: Long = 0L
        private set

    var state: SyncState = SyncState.Syncing
        private set

    @Synchronized
    fun addSample(t1: Long, t2: Long, t3: Long, t4: Long): SyncState {
        val rtt = (t4 - t1) - (t3 - t2)
        val offset = ((t2 - t1) + (t3 - t4)) / 2L
        val c = t1 + (t4 - t1) / 2L
        val h = c + offset

        val sample = ClockSample(
            t1 = t1,
            t2 = t2,
            t3 = t3,
            t4 = t4,
            rtt = maxOf(0L, rtt),
            offset = offset,
            clientMidpoint = c,
            hostMidpoint = h
        )

        samples.add(sample)
        lastSampleTimeTicks = t4

        // Evict samples outside 60s window
        val cutoff = t4 - windowTicks
        samples.removeAll { it.clientMidpoint < cutoff }
        sampleCount = samples.size

        recompute(t4)
        return state
    }

    @Synchronized
    fun recompute(nowTicks: Long) {
        val n = samples.size
        if (n == 0) {
            isConverged = false
            state = if (everConverged) SyncState.Degraded else SyncState.Syncing
            return
        }

        // Min-RTT bucketing
        // Keep ~20% of samples, bounded between 4 and 20 buckets.
        // For n >= 20, this guarantees each bucket has >= 5 samples, ensuring min-RTT
        // selection filters out transient delay spikes (spike prob 0.2 -> 0.2^5 = 0.00032).
        val k = min(n, max(4, min(20, Math.ceil(0.2 * n).toInt())))

        // Sort by clientMidpoint
        val sorted = samples.sortedBy { it.clientMidpoint }
        val selected = ArrayList<ClockSample>(k)

        for (j in 0 until k) {
            val startIdx = (j * n) / k
            val endIdx = ((j + 1) * n) / k
            var best = sorted[startIdx]
            for (i in (startIdx + 1) until endIdx) {
                if (sorted[i].rtt < best.rtt) {
                    best = sorted[i]
                }
            }
            selected.add(best)
        }

        // Adaptive RTT outlier gating: reject any bucket candidate whose RTT
        // is an extreme spike relative to the clean baseline.
        val minRtt = samples.minOf { it.rtt }
        val sortedCandidateRtt = selected.map { it.rtt }.sorted()
        val medianCandidateRtt = sortedCandidateRtt[sortedCandidateRtt.size / 2]
        val rttSpread = maxOf(medianCandidateRtt - minRtt, weightFloorTicks.toLong())
        val rttCutoff = medianCandidateRtt + 3L * rttSpread
        val regressionPoints = selected.filter { it.rtt <= rttCutoff }.let {
            if (it.size >= 3) it else selected
        }

        val regK = regressionPoints.size

        // Regression anchoring
        c0 = regressionPoints[0].clientMidpoint
        h0 = regressionPoints[0].hostMidpoint

        var sumW = 0.0
        var sumWx = 0.0
        var sumWy = 0.0

        val weights = DoubleArray(regK)
        val xs = DoubleArray(regK)
        val ys = DoubleArray(regK)

        for (j in 0 until regK) {
            val s = regressionPoints[j]
            val ej = (s.rtt - minRtt).toDouble() + weightFloorTicks
            val w = 1.0 / (ej * ej)
            val x = (s.clientMidpoint - c0).toDouble()
            val y = (s.hostMidpoint - h0).toDouble()

            weights[j] = w
            xs[j] = x
            ys[j] = y

            sumW += w
            sumWx += w * x
            sumWy += w * y
        }

        val xBar = if (sumW > 0.0) sumWx / sumW else 0.0
        val yBar = if (sumW > 0.0) sumWy / sumW else 0.0

        var sxx = 0.0
        var sxy = 0.0
        for (j in 0 until regK) {
            val dx = xs[j] - xBar
            val dy = ys[j] - yBar
            sxx += weights[j] * dx * dx
            sxy += weights[j] * dx * dy
        }

        if (sxx > 0.0) {
            slope = sxy / sxx
            intercept = yBar - slope * xBar
        } else {
            slope = 1.0
            intercept = 0.0
        }

        driftPpm = (slope - 1.0) * 1e6

        // Compute residuals
        var sumR2 = 0.0
        var sumWR2 = 0.0
        for (j in 0 until regK) {
            val fitted = intercept + slope * xs[j]
            val r = ys[j] - fitted
            sumR2 += r * r
            sumWR2 += weights[j] * r * r
        }

        fitResidualMs = (1.0 / 10000.0) * sqrt(sumR2 / regK)

        if (regK > 2 && sxx > 0.0) {
            val s2 = sumWR2 / (regK - 2)
            val lastClientX = (nowTicks - c0).toDouble()
            val varianceAtNow = s2 * ((1.0 / sumW) + ((lastClientX - xBar) * (lastClientX - xBar)) / sxx)
            stdErrorMs = (1.0 / 10000.0) * sqrt(max(0.0, varianceAtNow))
        } else {
            stdErrorMs = Double.POSITIVE_INFINITY
        }

        // Convergence evaluation
        isConverged = (n >= 20 && stdErrorMs < 0.5)
        if (isConverged) {
            everConverged = true
        }

        // Health state evaluation
        val sampleAge = nowTicks - lastSampleTimeTicks
        state = when {
            !everConverged -> SyncState.Syncing
            sampleAge > maxSampleAgeTicks || stdErrorMs > 1.0 -> SyncState.Degraded
            else -> SyncState.InSync
        }
    }

    /**
     * Maps client monotonic clock ticks to estimated host session ticks.
     */
    fun clientToHost(clientTicks: Long): Long {
        if (samples.isEmpty()) return clientTicks
        val x = (clientTicks - c0).toDouble()
        val fitted = intercept + slope * x
        return h0 + Math.round(fitted)
    }

    @Synchronized
    fun reset() {
        samples.clear()
        sampleCount = 0
        everConverged = false
        isConverged = false
        slope = 1.0
        intercept = 0.0
        c0 = 0L
        h0 = 0L
        driftPpm = 0.0
        fitResidualMs = 0.0
        stdErrorMs = Double.POSITIVE_INFINITY
        lastSampleTimeTicks = 0L
        state = SyncState.Syncing
    }
}
