package com.rhythmsync.android.protocol

import org.junit.Assert.*
import org.junit.Test

class PacketSerializerTest {

    @Test
    fun testAudioPacketRoundTrip() {
        val pcm = ShortArray(480) { (it * 100 - 24000).toShort() }
        val original = AudioPacket(
            sessionId = 0x12345678L,
            sequence = 1001L,
            playAt = 9876543210L,
            frameSamples = 240,
            pcmData = pcm
        )

        val bytes = PacketSerializer.writeAudioPacket(original)
        assertEquals(PacketSerializer.AUDIO_PACKET_BYTES, bytes.size)

        val decoded = PacketSerializer.tryReadAudioPacket(bytes)
        assertNotNull(decoded)
        assertEquals(original.sessionId, decoded!!.sessionId)
        assertEquals(original.sequence, decoded.sequence)
        assertEquals(original.playAt, decoded.playAt)
        assertEquals(original.frameSamples, decoded.frameSamples)
        assertArrayEquals(original.pcmData, decoded.pcmData)
        assertEquals(original, decoded)
    }

    @Test
    fun testPingPacketRoundTrip() {
        val original = PingPacket(
            sessionId = 0xAABBCCDDL,
            pingId = 42L,
            t1 = 1234567890123L
        )
        val bytes = PacketSerializer.writePingPacket(original)
        assertEquals(PacketSerializer.PING_PACKET_BYTES, bytes.size)

        val decoded = PacketSerializer.tryReadPingPacket(bytes)
        assertNotNull(decoded)
        assertEquals(original, decoded)
    }

    @Test
    fun testPongPacketRoundTrip() {
        val original = PongPacket(
            sessionId = 0xCAFEBABEL,
            pingId = 88L,
            t1 = 1000000L,
            t2 = 1000500L,
            t3 = 1000520L
        )
        val bytes = PacketSerializer.writePongPacket(original)
        assertEquals(PacketSerializer.PONG_PACKET_BYTES, bytes.size)

        val decoded = PacketSerializer.tryReadPongPacket(bytes)
        assertNotNull(decoded)
        assertEquals(original, decoded)
    }

    @Test
    fun testBeaconPacketRoundTrip() {
        val original = BeaconPacket(
            sessionId = 0x99887766L,
            controlPort = 47801,
            protocolVersion = 2,
            name = "Pixel 8 Pro Speaker"
        )
        val bytes = PacketSerializer.writeBeaconPacket(original)
        val decoded = PacketSerializer.tryReadBeaconPacket(bytes)
        assertNotNull(decoded)
        assertEquals(original, decoded)
    }

    @Test
    fun testCorruptMagicRejected() {
        val packet = PingPacket(1L, 1L, 100L)
        val bytes = PacketSerializer.writePingPacket(packet)

        // Corrupt magic byte 0
        bytes[0] = 0x00
        assertNull(PacketSerializer.tryReadHeader(bytes))
        assertNull(PacketSerializer.tryReadPingPacket(bytes))
    }

    @Test
    fun testWrongVersionRejected() {
        val packet = PingPacket(1L, 1L, 100L)
        val bytes = PacketSerializer.writePingPacket(packet)

        // Corrupt version at byte 2
        bytes[2] = 2
        assertNull(PacketSerializer.tryReadHeader(bytes))
        assertNull(PacketSerializer.tryReadPingPacket(bytes))
    }

    @Test
    fun testTruncatedPacketsRejected() {
        val packet = PingPacket(1L, 1L, 100L)
        val bytes = PacketSerializer.writePingPacket(packet)

        // Less than header length
        assertNull(PacketSerializer.tryReadHeader(bytes, 0, 7))
        // Header ok, but ping payload truncated
        assertNull(PacketSerializer.tryReadPingPacket(bytes, 0, 19))
    }

    @Test
    fun testBeaconNameLengthValidation() {
        val longName = "A".repeat(65)
        try {
            PacketSerializer.writeBeaconPacket(BeaconPacket(1L, 47801, 2, longName))
            fail("Expected IllegalArgumentException for name > 64 bytes")
        } catch (_: IllegalArgumentException) {
            // Success
        }
    }

    @Test
    fun testAudioFrameSamplesMismatchRejected() {
        val pcm = ShortArray(480)
        val packet = AudioPacket(1L, 1L, 100L, 240, pcm)
        val bytes = PacketSerializer.writeAudioPacket(packet)

        // Byte 20 is frameSamples low byte (240 = 0xF0)
        bytes[20] = 0x78 // 120 samples
        assertNull(PacketSerializer.tryReadAudioPacket(bytes))
    }
}
