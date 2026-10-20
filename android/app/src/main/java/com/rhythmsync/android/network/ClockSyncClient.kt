package com.rhythmsync.android.network

import com.rhythmsync.android.protocol.PacketSerializer
import com.rhythmsync.android.protocol.PingPacket
import com.rhythmsync.android.sync.ClockEstimator
import java.io.Closeable
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

class ClockSyncClient(
    private val channel: IDatagramChannel,
    private val serverAddress: InetSocketAddress,
    private val sessionId: Long,
    private val estimator: ClockEstimator,
    private val clockNowTicks: () -> Long = { System.nanoTime() / 100L }
) : Closeable {

    companion object {
        const val FAST_INTERVAL_TICKS = 500_000L // 50 ms in 100ns ticks
        const val SLOW_INTERVAL_TICKS = 2_500_000L // 250 ms in 100ns ticks
        const val FAST_PHASE_TICKS = 20_000_000L // 2.0 s in 100ns ticks
        const val TRACKED_PINGS = 8
    }

    private val isDisposed = AtomicBoolean(false)
    private val isStarted = AtomicBoolean(false)
    private val pingsSent = AtomicLong(0)

    private val lock = Any()
    private val outstandingIds = LongArray(TRACKED_PINGS)
    private val outstandingT1 = LongArray(TRACKED_PINGS)
    private val isOutstanding = BooleanArray(TRACKED_PINGS)

    private var startTicks: Long = 0L
    private var lastPingTicks: Long? = null
    private var nextPingId: Long = 1L

    val totalPingsSent: Long get() = pingsSent.get()

    fun start() {
        check(!isDisposed.get()) { "Client disposed." }
        synchronized(lock) {
            check(!isStarted.getAndSet(true)) { "Already started." }
            startTicks = clockNowTicks()
        }
        channel.start { data, length, _ -> onDatagram(data, length) }
    }

    fun tick() {
        if (isDisposed.get() || !isStarted.get()) return
        synchronized(lock) {
            val now = clockNowTicks()
            val interval = if (now - startTicks < FAST_PHASE_TICKS) FAST_INTERVAL_TICKS else SLOW_INTERVAL_TICKS
            val last = lastPingTicks
            if (last != null && now - last < interval) return

            val id = nextPingId++
            val slot = (id % TRACKED_PINGS).toInt()
            outstandingIds[slot] = id
            outstandingT1[slot] = now
            isOutstanding[slot] = true
            lastPingTicks = now

            val ping = PingPacket(sessionId, id, now)
            val bytes = PacketSerializer.writePingPacket(ping)
            channel.send(bytes, 0, bytes.size, serverAddress)
            pingsSent.incrementAndGet()
        }
    }

    private fun onDatagram(data: ByteArray, length: Int) {
        val t4 = clockNowTicks()
        if (isDisposed.get()) return
        val pong = PacketSerializer.tryReadPongPacket(data, 0, length) ?: return
        if (pong.sessionId != sessionId) return

        synchronized(lock) {
            val slot = (pong.pingId % TRACKED_PINGS).toInt()
            if (!isOutstanding[slot] || outstandingIds[slot] != pong.pingId || outstandingT1[slot] != pong.t1) {
                return
            }
            isOutstanding[slot] = false // Mark handled to prevent duplicate handling
        }
        estimator.addSample(pong.t1, pong.t2, pong.t3, t4)
    }

    override fun close() {
        if (isDisposed.getAndSet(true)) return
        channel.close()
    }
}
