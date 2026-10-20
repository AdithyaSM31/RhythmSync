package com.rhythmsync.android.network

import com.rhythmsync.android.protocol.PacketSerializer
import com.rhythmsync.android.sync.JitterBuffer
import java.io.Closeable
import java.net.InetAddress

class AudioReceiver(
    private val channel: IDatagramChannel,
    private val expectedHostAddress: InetAddress,
    private val sessionId: Long,
    private val jitterBuffer: JitterBuffer
) : Closeable {

    fun start() {
        channel.start { data, length, from ->
            if (from.address == expectedHostAddress) {
                val packet = PacketSerializer.tryReadAudioPacket(data, 0, length)
                if (packet != null && packet.sessionId == sessionId) {
                    jitterBuffer.insert(packet.sequence, packet.playAt, packet.pcmData)
                }
            }
        }
    }

    override fun close() {
        channel.close()
    }
}
