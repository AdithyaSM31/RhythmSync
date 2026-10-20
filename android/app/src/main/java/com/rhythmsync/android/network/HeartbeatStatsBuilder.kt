package com.rhythmsync.android.network

import com.rhythmsync.android.protocol.HeartbeatStats

/**
 * Builds the stats carried by a heartbeat the way the Windows client does: loss and lateness are percentages over the
 * trailing 10 s ([StatsWindow], sampled once per heartbeat from cumulative counters), never running counts. The host
 * shows "Wi-Fi congested" when LatePct exceeds 5, so a count in that field raised false warnings.
 *
 * Percentages are 0 when the window holds fewer than two samples or no frames; they are always finite and within
 * 0..100. If a counter goes backwards (a new session restarted the counters) the window starts over.
 * [HeartbeatStats.SyncErrorMs] is NaN unless the player is running, as on Windows (it goes stale while silent).
 * Not thread-safe; call from the session's tick thread.
 */
class HeartbeatStatsBuilder {
    private var window = StatsWindow()
    private var lastReceived = 0L
    private var lastLate = 0L
    private var lastConcealed = 0L
    private var hasSample = false

    fun build(
        nowTicks: Long,
        received: Long,
        late: Long,
        concealed: Long,
        playing: Boolean,
        syncErrorMs: Double,
        driftPpm: Double,
        fitResidualMs: Double,
        state: String
    ): HeartbeatStats {
        if (hasSample && (received < lastReceived || late < lastLate || concealed < lastConcealed)) {
            window = StatsWindow()
        }
        lastReceived = received
        lastLate = late
        lastConcealed = concealed
        hasSample = true

        window.add(nowTicks, received, late, concealed)
        return HeartbeatStats(
            LossPct = percent(window.lossPct),
            LatePct = percent(window.latePct),
            DriftPpm = driftPpm,
            FitResidualMs = fitResidualMs,
            SyncErrorMs = if (playing) syncErrorMs else Double.NaN,
            State = state
        )
    }

    private fun percent(value: Double): Double =
        if (value.isNaN() || value.isInfinite()) 0.0 else value.coerceIn(0.0, 100.0)
}
