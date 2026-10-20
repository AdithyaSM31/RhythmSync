package com.rhythmsync.android.network

import com.rhythmsync.android.protocol.PacketSerializer
import com.rhythmsync.android.protocol.PongPacket
import java.io.Closeable
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicBoolean

class ClockSyncServer(
    private val channel: IDatagramChannel,
    private val sessionId: Long,
    private val clockNowTicks: () -> Long = { System.nanoTime() / 100L }
) : Closeable {

    private val isDisposed = AtomicBoolean(false)

    fun start() {
        check(!isDisposed.get()) { "Server disposed." }
        channel.start { data, length, from -> onDatagram(data, length, from) }
    }

    private fun onDatagram(data: ByteArray, length: Int, from: InetSocketAddress) {
        val t2 = clockNowTicks()
        if (isDisposed.get()) return
        val ping = PacketSerializer.tryReadPingPacket(data, 0, length) ?: return
        if (ping.sessionId != sessionId) return

        val t3 = clockNowTicks()
        val pong = PongPacket(sessionId, ping.pingId, ping.t1, t2, t3)
        val bytes = PacketSerializer.writePongPacket(pong)
        channel.send(bytes, 0, bytes.size, from)
    }

    override fun close() {
        if (isDisposed.getAndSet(true)) return
        channel.close()
    }
}
