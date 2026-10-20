package com.rhythmsync.android.network

import com.rhythmsync.android.protocol.AudioPacket
import com.rhythmsync.android.protocol.PacketSerializer
import java.net.InetSocketAddress

class AudioStreamer(
    private val audioChannel: IDatagramChannel,
    private val sessionId: Long
) {
    data class ClientEndpoint(val deviceId: String, val audioEndPoint: InetSocketAddress)

    private val lock = Any()
    @Volatile
    private var clients: List<ClientEndpoint> = emptyList()

    var localSink: ((seq: Long, playAt: Long, pcm: ShortArray) -> Unit)? = null

    val clientEndpoints: List<ClientEndpoint> get() = clients

    fun addClient(deviceId: String, audioEndPoint: InetSocketAddress) {
        synchronized(lock) {
            val next = clients.filterNot { it.deviceId == deviceId }.toMutableList()
            next.add(ClientEndpoint(deviceId, audioEndPoint))
            clients = next
        }
    }

    fun removeClient(deviceId: String) {
        synchronized(lock) {
            clients = clients.filterNot { it.deviceId == deviceId }
        }
    }

    fun onFrame(seq: Long, playAt: Long, pcm: ShortArray) {
        val packet = AudioPacket(sessionId, seq, playAt, 240, pcm)
        val bytes = PacketSerializer.writeAudioPacket(packet)

        val currentClients = clients
        for (i in currentClients.indices) {
            audioChannel.send(bytes, 0, bytes.size, currentClients[i].audioEndPoint)
        }
        localSink?.invoke(seq, playAt, pcm)
    }
}
