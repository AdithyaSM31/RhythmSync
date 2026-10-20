package com.rhythmsync.android.protocol

import com.rhythmsync.android.network.ClientSession
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.nio.charset.StandardCharsets
import java.util.UUID

/**
 * Checks the Android codecs against the golden files in docs/interop that the Windows tests also verify.
 * `control-messages.jsonl` is what the Windows ControlCodec writes; `control-messages.android.jsonl` is what this
 * app must write (the Windows tests decode those lines with the real codec); `udp-packets.hex.txt` holds packets
 * written by the Windows PacketSerializer.
 */
class InteropFixtureTest {

    private val deviceId = "e0b5b111-6677-4c07-b353-2bf9b4862dc7"

    private val heartbeatFinite = HeartbeatStats(0.5, 0.25, -14.2, 0.18, 0.32, "InSync")
    private val heartbeatNaN = HeartbeatStats(Double.NaN, Double.NaN, 0.0, 0.18, Double.NaN, "Syncing")
    private val heartbeatInfinity =
        HeartbeatStats(Double.NaN, Double.POSITIVE_INFINITY, Double.NEGATIVE_INFINITY, 0.18, Double.NaN, "Degraded")

    /** The Windows-written lines, in fixture order. */
    private val windowsMessages: List<ControlMessage> = listOf(
        Hello(deviceId, "Living Room PC", "4821", 2, 47802),
        Welcome(0x8899AABBL, 48000, 2, 240, 250, 47802, 47803),
        Reject(RejectReason.BadCode),
        Reject(RejectReason.LockedOut),
        Reject(RejectReason.VersionMismatch),
        Reject(RejectReason.SessionFull),
        Ready(),
        Heartbeat(null),
        Heartbeat(heartbeatFinite),
        Heartbeat(heartbeatNaN),
        Heartbeat(heartbeatInfinity),
        SetDeviceSettings(0.75f, -12.5),
        SetDeviceSettings(0.5f, 3.25, listOf(-9f, -6f, -1f, 2f, 1f, 0f, 0f, 1f, 2f, 1.5f)),
        SetLatency(300),
        Bye()
    )

    /** What this app sends, in fixture order. */
    private val androidMessages: List<ControlMessage> = listOf(
        Hello(deviceId, "Pixel 8", "4821", 2, 47802),
        Ready(),
        Heartbeat(null),
        Heartbeat(heartbeatFinite),
        Heartbeat(heartbeatNaN),
        Heartbeat(heartbeatInfinity),
        Bye()
    )

    private fun interopDir(): File {
        var dir: File? = File(System.getProperty("user.dir")!!).absoluteFile
        while (dir != null) {
            val candidate = File(dir, "docs/interop")
            if (candidate.isDirectory) return candidate
            dir = dir.parentFile
        }
        throw AssertionError("docs/interop not found above ${System.getProperty("user.dir")}")
    }

    private fun fixtureLines(name: String): List<String> {
        val file = File(interopDir(), name)
        assertTrue("Missing interop fixture $file", file.isFile)
        return file.readText(StandardCharsets.UTF_8)
            .replace("\r\n", "\n").split('\n')
            .filter { it.isNotEmpty() && !it.startsWith("#") }
    }

    /** The JSON body that [ControlCodec] writes (the frame without its 4-byte length prefix). */
    private fun encodeBody(message: ControlMessage): String {
        val frame = ControlCodec.encode(message)
        return String(frame, 4, frame.size - 4, StandardCharsets.UTF_8)
    }

    private fun decodeBody(line: String): ControlMessage =
        ControlCodec.decodePayload(line.toByteArray(StandardCharsets.UTF_8))

    private fun hexToBytes(hex: String): ByteArray {
        require(hex.length % 2 == 0)
        return ByteArray(hex.length / 2) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }

    private fun bytesToHex(bytes: ByteArray): String =
        bytes.joinToString("") { "%02x".format(it.toInt() and 0xFF) }

    @Test
    fun everyWindowsControlLineParsesToTheExpectedMessage() {
        val lines = fixtureLines("control-messages.jsonl")
        assertEquals("fixture line count", windowsMessages.size, lines.size)
        for (i in lines.indices) {
            assertEquals("control-messages.jsonl line ${i + 1}: ${lines[i]}", windowsMessages[i], decodeBody(lines[i]))
        }
    }

    @Test
    fun windowsNanAndInfinityStatsKeepTheirValues() {
        val nan = decodeBody(fixtureLines("control-messages.jsonl")[9]) as Heartbeat
        assertTrue(nan.Stats!!.LossPct.isNaN())
        assertTrue(nan.Stats!!.LatePct.isNaN())
        assertTrue(nan.Stats!!.SyncErrorMs.isNaN())
        assertEquals(0.18, nan.Stats!!.FitResidualMs, 0.0)

        val inf = decodeBody(fixtureLines("control-messages.jsonl")[10]) as Heartbeat
        assertEquals(Double.POSITIVE_INFINITY, inf.Stats!!.LatePct, 0.0)
        assertEquals(Double.NEGATIVE_INFINITY, inf.Stats!!.DriftPpm, 0.0)
    }

    @Test
    fun androidOriginatedMessagesEncodeToTheGoldenLines() {
        val lines = fixtureLines("control-messages.android.jsonl")
        assertEquals("fixture line count", androidMessages.size, lines.size)
        for (i in lines.indices) {
            assertEquals("control-messages.android.jsonl line ${i + 1}", lines[i], encodeBody(androidMessages[i]))
            assertEquals(androidMessages[i], decodeBody(lines[i]))
        }
    }

    @Test
    fun androidMessagesNeverWriteUnquotedNonFiniteNumbers() {
        for (message in androidMessages) {
            val body = encodeBody(message)
            assertTrue("unquoted NaN in $body", !Regex(""":\s*NaN""").containsMatchIn(body))
            assertTrue("unquoted Infinity in $body", !Regex(""":\s*-?Infinity""").containsMatchIn(body))
        }
    }

    @Test
    fun windowsPacketsParseAndRewriteByteForByte() {
        val packets = fixtureLines("udp-packets.hex.txt").associate {
            val (name, hex) = it.split(' ', limit = 2)
            name to hex
        }
        assertEquals(setOf("audio", "ping", "pong", "beacon"), packets.keys)

        val audio = PacketSerializer.tryReadAudioPacket(hexToBytes(packets.getValue("audio")))!!
        assertEquals(0xA1B2C3D4L, audio.sessionId)
        assertEquals(77L, audio.sequence)
        assertEquals(123_456_789_012L, audio.playAt)
        assertEquals(240, audio.frameSamples)
        assertEquals(480, audio.pcmData.size)
        for (i in 0 until 480) assertEquals("pcm[$i]", (i * 37 - 8000).toShort(), audio.pcmData[i])
        assertEquals(packets.getValue("audio"), bytesToHex(PacketSerializer.writeAudioPacket(audio)))

        val ping = PacketSerializer.tryReadPingPacket(hexToBytes(packets.getValue("ping")))!!
        assertEquals(PingPacket(0xDEADBEEFL, 42L, 9_876_543_210_123L), ping)
        assertEquals(packets.getValue("ping"), bytesToHex(PacketSerializer.writePingPacket(ping)))

        val pong = PacketSerializer.tryReadPongPacket(hexToBytes(packets.getValue("pong")))!!
        assertEquals(PongPacket(0xCAFEF00DL, 7L, -5L, 123_456_789_012_345L, 987_654_321_098_765L), pong)
        assertEquals(packets.getValue("pong"), bytesToHex(PacketSerializer.writePongPacket(pong)))

        val beacon = PacketSerializer.tryReadBeaconPacket(hexToBytes(packets.getValue("beacon")))!!
        assertEquals(BeaconPacket(0x8899AABBL, 47801, 2, "Living Room PC"), beacon)
        assertEquals(packets.getValue("beacon"), bytesToHex(PacketSerializer.writeBeaconPacket(beacon)))
    }

    @Test
    fun clientDeviceIdIsAGuidTheWindowsHostCanParse() {
        // The Windows Hello record has a Guid DeviceId; any other text makes the host drop the connection.
        val id = ClientSession().deviceId
        assertEquals(id, UUID.fromString(id).toString())
    }
}
