package com.rhythmsync.android.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets

data class PacketHeader(
    val version: Int,
    val type: PacketType,
    val sessionId: Long
)

data class AudioPacket(
    val sessionId: Long,
    val sequence: Long,
    val playAt: Long,
    val frameSamples: Int = 240,
    val pcmData: ShortArray
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (javaClass != other?.javaClass) return false
        other as AudioPacket
        if (sessionId != other.sessionId) return false
        if (sequence != other.sequence) return false
        if (playAt != other.playAt) return false
        if (frameSamples != other.frameSamples) return false
        if (!pcmData.contentEquals(other.pcmData)) return false
        return true
    }

    override fun hashCode(): Int {
        var result = sessionId.hashCode()
        result = 31 * result + sequence.hashCode()
        result = 31 * result + playAt.hashCode()
        result = 31 * result + frameSamples
        result = 31 * result + pcmData.contentHashCode()
        return result
    }
}

data class PingPacket(
    val sessionId: Long,
    val pingId: Long,
    val t1: Long
)

data class PongPacket(
    val sessionId: Long,
    val pingId: Long,
    val t1: Long,
    val t2: Long,
    val t3: Long
)

data class BeaconPacket(
    val sessionId: Long,
    val controlPort: Int,
    val protocolVersion: Int = 2,
    val name: String
)

object PacketSerializer {
    const val MAGIC: Short = 0x5253.toShort()
    const val VERSION: Byte = 1
    const val HEADER_BYTES: Int = 8

    const val AUDIO_PACKET_BYTES: Int = 982
    const val PING_PACKET_BYTES: Int = 20
    const val PONG_PACKET_BYTES: Int = 36
    const val MIN_BEACON_BYTES: Int = 12
    const val MAX_BEACON_NAME_BYTES: Int = 64
    const val MAX_BEACON_BYTES: Int = 12 + MAX_BEACON_NAME_BYTES

    fun tryReadHeader(buffer: ByteArray, offset: Int = 0, length: Int = buffer.size): PacketHeader? {
        if (length < HEADER_BYTES || offset < 0 || offset + length > buffer.size) {
            return null
        }
        val bb = ByteBuffer.wrap(buffer, offset, length).order(ByteOrder.LITTLE_ENDIAN)
        val magic = bb.short
        if (magic != MAGIC) return null
        val version = bb.get()
        if (version != VERSION) return null
        val typeByte = bb.get()
        val type = PacketType.fromByte(typeByte) ?: return null
        val sessionId = bb.int.toLong() and 0xFFFFFFFFL
        return PacketHeader(version.toInt(), type, sessionId)
    }

    fun writeHeader(type: PacketType, sessionId: Long, destination: ByteBuffer) {
        destination.order(ByteOrder.LITTLE_ENDIAN)
        destination.putShort(MAGIC)
        destination.put(VERSION)
        destination.put(type.value)
        destination.putInt(sessionId.toInt())
    }

    // --- Audio Packet ---

    fun writeAudioPacket(packet: AudioPacket): ByteArray {
        val bytes = ByteArray(AUDIO_PACKET_BYTES)
        val bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        writeHeader(PacketType.Audio, packet.sessionId, bb)
        bb.putInt(packet.sequence.toInt())
        bb.putLong(packet.playAt)
        bb.putShort(packet.frameSamples.toShort())
        for (i in 0 until minOf(packet.pcmData.size, 480)) {
            bb.putShort(packet.pcmData[i])
        }
        return bytes
    }

    fun tryReadAudioPacket(buffer: ByteArray, offset: Int = 0, length: Int = buffer.size): AudioPacket? {
        if (length != AUDIO_PACKET_BYTES || offset < 0 || offset + length > buffer.size) {
            return null
        }
        val header = tryReadHeader(buffer, offset, length) ?: return null
        if (header.type != PacketType.Audio) return null

        val bb = ByteBuffer.wrap(buffer, offset + HEADER_BYTES, length - HEADER_BYTES).order(ByteOrder.LITTLE_ENDIAN)
        val sequence = bb.int.toLong() and 0xFFFFFFFFL
        val playAt = bb.long
        val frameSamples = bb.short.toInt() and 0xFFFF
        if (frameSamples != 240) return null

        val pcm = ShortArray(480)
        for (i in 0 until 480) {
            pcm[i] = bb.short
        }
        return AudioPacket(
            sessionId = header.sessionId,
            sequence = sequence,
            playAt = playAt,
            frameSamples = frameSamples,
            pcmData = pcm
        )
    }

    // --- Ping Packet ---

    fun writePingPacket(packet: PingPacket): ByteArray {
        val bytes = ByteArray(PING_PACKET_BYTES)
        val bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        writeHeader(PacketType.Ping, packet.sessionId, bb)
        bb.putInt(packet.pingId.toInt())
        bb.putLong(packet.t1)
        return bytes
    }

    fun tryReadPingPacket(buffer: ByteArray, offset: Int = 0, length: Int = buffer.size): PingPacket? {
        if (length != PING_PACKET_BYTES || offset < 0 || offset + length > buffer.size) {
            return null
        }
        val header = tryReadHeader(buffer, offset, length) ?: return null
        if (header.type != PacketType.Ping) return null

        val bb = ByteBuffer.wrap(buffer, offset + HEADER_BYTES, length - HEADER_BYTES).order(ByteOrder.LITTLE_ENDIAN)
        val pingId = bb.int.toLong() and 0xFFFFFFFFL
        val t1 = bb.long
        return PingPacket(sessionId = header.sessionId, pingId = pingId, t1 = t1)
    }

    // --- Pong Packet ---

    fun writePongPacket(packet: PongPacket): ByteArray {
        val bytes = ByteArray(PONG_PACKET_BYTES)
        val bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        writeHeader(PacketType.Pong, packet.sessionId, bb)
        bb.putInt(packet.pingId.toInt())
        bb.putLong(packet.t1)
        bb.putLong(packet.t2)
        bb.putLong(packet.t3)
        return bytes
    }

    fun tryReadPongPacket(buffer: ByteArray, offset: Int = 0, length: Int = buffer.size): PongPacket? {
        if (length != PONG_PACKET_BYTES || offset < 0 || offset + length > buffer.size) {
            return null
        }
        val header = tryReadHeader(buffer, offset, length) ?: return null
        if (header.type != PacketType.Pong) return null

        val bb = ByteBuffer.wrap(buffer, offset + HEADER_BYTES, length - HEADER_BYTES).order(ByteOrder.LITTLE_ENDIAN)
        val pingId = bb.int.toLong() and 0xFFFFFFFFL
        val t1 = bb.long
        val t2 = bb.long
        val t3 = bb.long
        return PongPacket(sessionId = header.sessionId, pingId = pingId, t1 = t1, t2 = t2, t3 = t3)
    }

    // --- Beacon Packet ---

    fun writeBeaconPacket(packet: BeaconPacket): ByteArray {
        val nameBytes = packet.name.toByteArray(StandardCharsets.UTF_8)
        require(nameBytes.size <= MAX_BEACON_NAME_BYTES) {
            "Beacon name exceeds maximum $MAX_BEACON_NAME_BYTES bytes (got ${nameBytes.size})"
        }
        val totalBytes = MIN_BEACON_BYTES + nameBytes.size
        val bytes = ByteArray(totalBytes)
        val bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        writeHeader(PacketType.Beacon, packet.sessionId, bb)
        bb.putShort(packet.controlPort.toShort())
        bb.put(packet.protocolVersion.toByte())
        bb.put(nameBytes.size.toByte())
        bb.put(nameBytes)
        return bytes
    }

    fun tryReadBeaconPacket(buffer: ByteArray, offset: Int = 0, length: Int = buffer.size): BeaconPacket? {
        if (length < MIN_BEACON_BYTES || offset < 0 || offset + length > buffer.size) {
            return null
        }
        val header = tryReadHeader(buffer, offset, length) ?: return null
        if (header.type != PacketType.Beacon) return null

        val bb = ByteBuffer.wrap(buffer, offset + HEADER_BYTES, length - HEADER_BYTES).order(ByteOrder.LITTLE_ENDIAN)
        val controlPort = bb.short.toInt() and 0xFFFF
        val protocolVersion = bb.get().toInt() and 0xFF
        val nameLen = bb.get().toInt() and 0xFF
        if (nameLen > MAX_BEACON_NAME_BYTES) return null
        if (length != MIN_BEACON_BYTES + nameLen) return null

        val nameBytes = ByteArray(nameLen)
        bb.get(nameBytes)
        val name = try {
            val decoder = StandardCharsets.UTF_8.newDecoder()
            decoder.decode(ByteBuffer.wrap(nameBytes)).toString()
        } catch (_: Exception) {
            return null
        }
        return BeaconPacket(
            sessionId = header.sessionId,
            controlPort = controlPort,
            protocolVersion = protocolVersion,
            name = name
        )
    }
}
