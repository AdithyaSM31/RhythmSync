package com.rhythmsync.android.network

import com.rhythmsync.android.protocol.BeaconPacket
import com.rhythmsync.android.protocol.PacketSerializer
import java.io.Closeable
import java.net.InetSocketAddress

class DiscoveryBroadcaster(
    private val channel: IDatagramChannel,
    private val targetBroadcast: InetSocketAddress = InetSocketAddress("255.255.255.255", DEFAULT_DISCOVERY_PORT),
    private val clockNowTicks: () -> Long = { System.nanoTime() / 100L },
    private val intervalTicks: Long = DEFAULT_INTERVAL_TICKS,
    private val beaconSupplier: () -> BeaconPacket
) : Closeable {

    companion object {
        const val DEFAULT_DISCOVERY_PORT = 47800
        const val DEFAULT_INTERVAL_TICKS = 5_000_000L // 500 ms in 100ns ticks
    }

    private var lastSentTicks: Long? = null

    fun tick() {
        val now = clockNowTicks()
        val last = lastSentTicks
        if (last != null && now - last < intervalTicks) return

        lastSentTicks = now
        val beacon = beaconSupplier()
        val bytes = PacketSerializer.writeBeaconPacket(beacon)
        channel.send(bytes, 0, bytes.size, targetBroadcast)
    }

    override fun close() {
        channel.close()
    }
}
