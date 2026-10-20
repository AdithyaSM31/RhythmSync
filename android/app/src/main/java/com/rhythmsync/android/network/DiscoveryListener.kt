package com.rhythmsync.android.network

import com.rhythmsync.android.protocol.PacketSerializer
import java.io.Closeable
import java.net.InetAddress
import java.net.InetSocketAddress

data class DiscoveredHost(
    val sessionId: Long,
    val name: String,
    val controlEndPoint: InetSocketAddress,
    val lastSeenTicks: Long
)

class DiscoveryListener(
    private val channel: IDatagramChannel,
    private val clockNowTicks: () -> Long = { System.nanoTime() / 100L },
    private val expiryTicks: Long = DEFAULT_EXPIRY_TICKS
) : Closeable {

    companion object {
        const val DEFAULT_DISCOVERY_PORT = 47800
        const val DEFAULT_EXPIRY_TICKS = 30_000_000L // 3.0 s in 100ns ticks
    }

    private data class HostKey(val address: InetAddress, val sessionId: Long)

    private val lock = Any()
    private val hostsMap = mutableMapOf<HostKey, DiscoveredHost>()

    var onChanged: (() -> Unit)? = null

    val currentHosts: List<DiscoveredHost>
        get() {
            val now = clockNowTicks()
            synchronized(lock) {
                return hostsMap.values
                    .filter { now - it.lastSeenTicks <= expiryTicks }
                    .sortedWith(
                        compareBy<DiscoveredHost, String>(String.CASE_INSENSITIVE_ORDER) { it.name }
                            .thenBy { it.sessionId }
                    )
            }
        }

    fun start() {
        channel.start { data, length, from ->
            onDatagram(data, length, from)
        }
    }

    fun tick() {
        val now = clockNowTicks()
        val changed = removeExpired(now)
        if (changed) {
            onChanged?.invoke()
        }
    }

    private fun onDatagram(data: ByteArray, length: Int, from: InetSocketAddress) {
        val beacon = PacketSerializer.tryReadBeaconPacket(data, 0, length) ?: return
        val now = clockNowTicks()
        val controlEp = InetSocketAddress(from.address, beacon.controlPort)
        val host = DiscoveredHost(beacon.sessionId, beacon.name, controlEp, now)
        val key = HostKey(from.address, beacon.sessionId)

        var changed = removeExpired(now)
        synchronized(lock) {
            val existing = hostsMap[key]
            val differs = existing == null || existing.name != host.name || existing.controlEndPoint != host.controlEndPoint
            changed = changed || differs
            hostsMap[key] = host
        }
        if (changed) {
            onChanged?.invoke()
        }
    }

    private fun removeExpired(now: Long): Boolean {
        synchronized(lock) {
            val before = hostsMap.size
            hostsMap.values.removeAll { now - it.lastSeenTicks > expiryTicks }
            return hostsMap.size != before
        }
    }

    override fun close() {
        channel.close()
    }
}
