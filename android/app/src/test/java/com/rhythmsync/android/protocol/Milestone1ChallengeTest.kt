package com.rhythmsync.android.protocol

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.IOException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets

/**
 * Empirical challenge test suite for Milestone 1:
 * - Wire format interoperability (byte-for-byte against C# reference)
 * - Negative and overflow session IDs
 * - Corrupted magic, truncated packets, boundary conditions
 * - ControlCodec malformed JSON, truncation, oversized payloads, unknown types, invalid enums
 * - Pairing authentication constant-time, per-device lockout (3 attempts), global lockout (10 attempts)
 */
class Milestone1ChallengeTest {

    // =========================================================================
    // 1. PacketSerializer Wire Format Byte-for-Byte & Edge Case Tests
    // =========================================================================

    @Test
    fun challengeAudioPacketExactByteLayout() {
        // Construct known AudioPacket
        val pcm = ShortArray(480) { (it and 0x7FFF).toShort() }
        val packet = AudioPacket(
            sessionId = 0x11223344L,
            sequence = 0x55667788L,
            playAt = 0x0102030405060708L,
            frameSamples = 240,
            pcmData = pcm
        )

        val bytes = PacketSerializer.writeAudioPacket(packet)
        assertEquals(982, bytes.size)

        val bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)

        // Offset 0..1: Magic 0x5253 (LE: 0x53, 0x52)
        assertEquals(0x53.toByte(), bytes[0])
        assertEquals(0x52.toByte(), bytes[1])
        assertEquals(0x5253.toShort(), bb.short)

        // Offset 2: Version 1
        assertEquals(1.toByte(), bytes[2])

        // Offset 3: PacketType.Audio = 1
        assertEquals(1.toByte(), bytes[3])

        // Offset 4..7: SessionId = 0x11223344 (LE: 44 33 22 11)
        assertEquals(0x11223344, bb.getInt(4))

        // Offset 8..11: Sequence = 0x55667788
        assertEquals(0x55667788, bb.getInt(8))

        // Offset 12..19: PlayAt = 0x0102030405060708L
        assertEquals(0x0102030405060708L, bb.getLong(12))

        // Offset 20..21: FrameSamples = 240 (0x00F0, LE: F0 00)
        assertEquals(240.toShort(), bb.getShort(20))

        // Offset 22..981: 480 PCM samples
        for (i in 0 until 480) {
            val sample = bb.getShort(22 + i * 2)
            assertEquals((i and 0x7FFF).toShort(), sample)
        }

        // Test deserialization roundtrip
        val decoded = PacketSerializer.tryReadAudioPacket(bytes)
        assertNotNull(decoded)
        assertEquals(packet, decoded)
    }

    @Test
    fun challengePingPacketExactByteLayout() {
        val ping = PingPacket(
            sessionId = 0x0ABCDEF0L,
            pingId = 0x12345678L,
            t1 = 0x1122334455667788L
        )
        val bytes = PacketSerializer.writePingPacket(ping)
        assertEquals(20, bytes.size)

        val bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(0x5253.toShort(), bb.short) // 0..1
        assertEquals(1.toByte(), bb.get())       // 2
        assertEquals(2.toByte(), bb.get())       // 3: PacketType.Ping = 2
        assertEquals(0x0ABCDEF0.toInt(), bb.int) // 4..7: SessionId
        assertEquals(0x12345678, bb.int)         // 8..11: PingId
        assertEquals(0x1122334455667788L, bb.long) // 12..19: T1

        val decoded = PacketSerializer.tryReadPingPacket(bytes)
        assertNotNull(decoded)
        assertEquals(ping, decoded)
    }

    @Test
    fun challengePongPacketExactByteLayout() {
        val pong = PongPacket(
            sessionId = 0xFEDCBA09L,
            pingId = 0x778899AAL,
            t1 = 1000L,
            t2 = 2000L,
            t3 = 3000L
        )
        val bytes = PacketSerializer.writePongPacket(pong)
        assertEquals(36, bytes.size)

        val bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(0x5253.toShort(), bb.short) // 0..1
        assertEquals(1.toByte(), bb.get())       // 2
        assertEquals(3.toByte(), bb.get())       // 3: PacketType.Pong = 3
        assertEquals(0xFEDCBA09.toInt(), bb.int) // 4..7: SessionId
        assertEquals(0x778899AA.toInt(), bb.int) // 8..11: PingId
        assertEquals(1000L, bb.long)             // 12..19: T1
        assertEquals(2000L, bb.long)             // 20..27: T2
        assertEquals(3000L, bb.long)             // 28..35: T3

        val decoded = PacketSerializer.tryReadPongPacket(bytes)
        assertNotNull(decoded)
        assertEquals(pong, decoded)
    }

    @Test
    fun challengeBeaconPacketExactByteLayout() {
        val beacon = BeaconPacket(
            sessionId = 0x12345678L,
            controlPort = 47801,
            protocolVersion = 2,
            name = "Test Host"
        )
        val bytes = PacketSerializer.writeBeaconPacket(beacon)
        val expectedLength = 12 + "Test Host".toByteArray(StandardCharsets.UTF_8).size
        assertEquals(expectedLength, bytes.size)

        val bb = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        assertEquals(0x5253.toShort(), bb.short) // 0..1
        assertEquals(1.toByte(), bb.get())       // 2
        assertEquals(4.toByte(), bb.get())       // 3: PacketType.Beacon = 4
        assertEquals(0x12345678, bb.int)         // 4..7: SessionId
        assertEquals(47801.toShort(), bb.short)  // 8..9: ControlPort (47801 = 0xBAB9)
        assertEquals(2.toByte(), bb.get())       // 10: ProtocolVersion = 2
        assertEquals(9.toByte(), bb.get())       // 11: NameLen = 9

        val nameBytes = ByteArray(9)
        bb.get(nameBytes)
        assertEquals("Test Host", String(nameBytes, StandardCharsets.UTF_8))

        val decoded = PacketSerializer.tryReadBeaconPacket(bytes)
        assertNotNull(decoded)
        assertEquals(beacon, decoded)
    }

    @Test
    fun challengeCorruptedMagicRejection() {
        val ping = PingPacket(1L, 1L, 100L)
        val validBytes = PacketSerializer.writePingPacket(ping)

        // Magic byte 0 corrupted
        val corrupt0 = validBytes.clone()
        corrupt0[0] = 0x00
        assertNull("Corrupt magic byte 0 should be rejected", PacketSerializer.tryReadHeader(corrupt0))
        assertNull(PacketSerializer.tryReadPingPacket(corrupt0))

        // Magic byte 1 corrupted
        val corrupt1 = validBytes.clone()
        corrupt1[1] = 0x00
        assertNull("Corrupt magic byte 1 should be rejected", PacketSerializer.tryReadHeader(corrupt1))
        assertNull(PacketSerializer.tryReadPingPacket(corrupt1))

        // Inverted magic 0x5352 instead of 0x5253
        val inverted = validBytes.clone()
        inverted[0] = 0x52
        inverted[1] = 0x53
        assertNull("Inverted magic endianness should be rejected", PacketSerializer.tryReadHeader(inverted))
    }

    @Test
    fun challengeTruncatedPacketsRejection() {
        val ping = PingPacket(1L, 1L, 100L)
        val pingBytes = PacketSerializer.writePingPacket(ping)

        // Header truncation tests (lengths 0..7)
        for (len in 0 until 8) {
            assertNull("Header with length $len must be rejected", PacketSerializer.tryReadHeader(pingBytes, 0, len))
        }

        // Ping payload truncation (lengths 8..19)
        for (len in 8 until 20) {
            assertNull("Ping packet with length $len must be rejected", PacketSerializer.tryReadPingPacket(pingBytes, 0, len))
        }

        // Pong payload truncation (lengths 8..35)
        val pong = PongPacket(1L, 1L, 100L, 200L, 300L)
        val pongBytes = PacketSerializer.writePongPacket(pong)
        for (len in 8 until 36) {
            assertNull("Pong packet with length $len must be rejected", PacketSerializer.tryReadPongPacket(pongBytes, 0, len))
        }

        // Audio payload truncation (lengths 8..981)
        val pcm = ShortArray(480)
        val audio = AudioPacket(1L, 1L, 100L, 240, pcm)
        val audioBytes = PacketSerializer.writeAudioPacket(audio)
        assertNull("Audio packet with length 981 must be rejected", PacketSerializer.tryReadAudioPacket(audioBytes, 0, 981))
        assertNull("Audio packet with length 22 must be rejected", PacketSerializer.tryReadAudioPacket(audioBytes, 0, 22))

        // Beacon truncation tests
        val beacon = BeaconPacket(1L, 47801, 2, "Test")
        val beaconBytes = PacketSerializer.writeBeaconPacket(beacon)
        // Beacon length 15 (1 byte truncated)
        assertNull("Beacon packet with truncated payload must be rejected", PacketSerializer.tryReadBeaconPacket(beaconBytes, 0, beaconBytes.size - 1))
        assertNull("Beacon packet with length 11 must be rejected", PacketSerializer.tryReadBeaconPacket(beaconBytes, 0, 11))
    }

    @Test
    fun challengeOversizedPacketsRejection() {
        // Audio packet parser must reject packets larger than AUDIO_PACKET_BYTES
        val audio = AudioPacket(1L, 1L, 100L, 240, ShortArray(480))
        val audioBytes = PacketSerializer.writeAudioPacket(audio)
        val oversizedAudio = audioBytes.copyOf(audioBytes.size + 10)
        assertNull("Audio packet with trailing bytes must be rejected", PacketSerializer.tryReadAudioPacket(oversizedAudio))

        // Ping parser must reject packets larger than PING_PACKET_BYTES
        val ping = PingPacket(1L, 1L, 100L)
        val pingBytes = PacketSerializer.writePingPacket(ping)
        val oversizedPing = pingBytes.copyOf(pingBytes.size + 1)
        assertNull("Ping packet with trailing bytes must be rejected", PacketSerializer.tryReadPingPacket(oversizedPing))

        // Pong parser must reject packets larger than PONG_PACKET_BYTES
        val pong = PongPacket(1L, 1L, 100L, 200L, 300L)
        val pongBytes = PacketSerializer.writePongPacket(pong)
        val oversizedPong = pongBytes.copyOf(pongBytes.size + 1)
        assertNull("Pong packet with trailing bytes must be rejected", PacketSerializer.tryReadPongPacket(oversizedPong))

        // Beacon parser must reject packets with trailing bytes beyond declared name length
        val beacon = BeaconPacket(1L, 47801, 2, "Test")
        val beaconBytes = PacketSerializer.writeBeaconPacket(beacon)
        val oversizedBeacon = beaconBytes.copyOf(beaconBytes.size + 5)
        assertNull("Beacon packet with trailing bytes must be rejected", PacketSerializer.tryReadBeaconPacket(oversizedBeacon))
    }

    @Test
    fun challengeSessionIdBoundaryAndOverflow() {
        // 1. SessionId = 0
        val p0 = PingPacket(0L, 1L, 100L)
        val d0 = PacketSerializer.tryReadPingPacket(PacketSerializer.writePingPacket(p0))
        assertEquals(0L, d0?.sessionId)

        // 2. SessionId = 0x7FFFFFFF (max signed 32-bit int: 2,147,483,647)
        val pMaxSigned = PingPacket(0x7FFFFFFFL, 1L, 100L)
        val dMaxSigned = PacketSerializer.tryReadPingPacket(PacketSerializer.writePingPacket(pMaxSigned))
        assertEquals(0x7FFFFFFFL, dMaxSigned?.sessionId)

        // 3. SessionId = 0x80000000 (signed bit set in 32-bit: 2,147,483,648)
        val pHighBit = PingPacket(0x80000000L, 1L, 100L)
        val dHighBit = PacketSerializer.tryReadPingPacket(PacketSerializer.writePingPacket(pHighBit))
        assertEquals(0x80000000L, dHighBit?.sessionId)

        // 4. SessionId = 0xFFFFFFFF (max unsigned 32-bit uint: 4,294,967,295)
        val pMaxUint = PingPacket(0xFFFFFFFFL, 1L, 100L)
        val dMaxUint = PacketSerializer.tryReadPingPacket(PacketSerializer.writePingPacket(pMaxUint))
        assertEquals(0xFFFFFFFFL, dMaxUint?.sessionId)

        // 5. Overflow session ID: 0x1_0000_0042L
        // Wire format holds uint32 (4 bytes). Serializer truncates to 32 bits.
        val pOverflow = PingPacket(0x1_0000_0042L, 1L, 100L)
        val dOverflow = PacketSerializer.tryReadPingPacket(PacketSerializer.writePingPacket(pOverflow))
        assertEquals("Overflow upper 32 bits should be masked to uint32", 0x42L, dOverflow?.sessionId)

        // 6. Negative session ID: -1L
        // -1L.toInt() is 0xFFFFFFFF. When read as unsigned uint32, it maps to 4294967295L (0xFFFFFFFFL).
        val pNeg = PingPacket(-1L, 1L, 100L)
        val dNeg = PacketSerializer.tryReadPingPacket(PacketSerializer.writePingPacket(pNeg))
        assertEquals(0xFFFFFFFFL, dNeg?.sessionId)
    }

    @Test
    fun challengeBeaconNameBoundariesAndEncoding() {
        // 0-byte name (empty name)
        val emptyBeacon = BeaconPacket(1L, 47801, 2, "")
        val emptyBytes = PacketSerializer.writeBeaconPacket(emptyBeacon)
        assertEquals(12, emptyBytes.size)
        val decodedEmpty = PacketSerializer.tryReadBeaconPacket(emptyBytes)
        assertNotNull(decodedEmpty)
        assertEquals("", decodedEmpty?.name)

        // Exactly 64-byte name (maximum allowed)
        val maxName = "A".repeat(64)
        val maxBeacon = BeaconPacket(1L, 47801, 2, maxName)
        val maxBytes = PacketSerializer.writeBeaconPacket(maxBeacon)
        assertEquals(12 + 64, maxBytes.size)
        val decodedMax = PacketSerializer.tryReadBeaconPacket(maxBytes)
        assertNotNull(decodedMax)
        assertEquals(maxName, decodedMax?.name)

        // 65-byte name should throw on write
        val overMaxName = "A".repeat(65)
        try {
            PacketSerializer.writeBeaconPacket(BeaconPacket(1L, 47801, 2, overMaxName))
            fail("writeBeaconPacket must reject name > 64 bytes")
        } catch (_: IllegalArgumentException) {
            // Success
        }

        // Byte 11 claiming nameLen = 65 should be rejected on read
        val forgedBytes = ByteArray(12 + 65)
        val bb = ByteBuffer.wrap(forgedBytes).order(ByteOrder.LITTLE_ENDIAN)
        PacketSerializer.writeHeader(PacketType.Beacon, 1L, bb)
        bb.putShort(47801.toShort())
        bb.put(2.toByte())
        bb.put(65.toByte()) // nameLen = 65
        assertNull("Beacon with nameLen > 64 must return null", PacketSerializer.tryReadBeaconPacket(forgedBytes))
    }

    // =========================================================================
    // 2. ControlCodec Fuzzing & Resilience Tests
    // =========================================================================

    @Test
    fun challengeControlCodecMalformedJson() {
        val malformedStrings = listOf(
            "",                                  // empty string
            "{",                                 // unclosed brace
            """{"type": "Hello"""",              // truncated JSON
            """{"type": "Hello", "DeviceId"}""", // invalid JSON syntax
            """[1, 2, 3]""",                     // JSON array instead of object
            """{"type": null}""",                // null type
            """{"type": 12345}""",               // numeric type
            """{"type": "UnknownType"}""",       // unrecognized message type
            """Not JSON at all!"""               // raw non-json string
        )

        for (malformed in malformedStrings) {
            val payload = malformed.toByteArray(StandardCharsets.UTF_8)
            try {
                ControlCodec.decodePayload(payload)
                fail("Expected IOException for malformed JSON: '$malformed'")
            } catch (_: IOException) {
                // Expected
            }
        }
    }

    @Test
    fun challengeControlCodecTruncatedLengthPrefix() {
        // Stream with 0 bytes (clean EOF at message boundary)
        val emptyStream = ByteArrayInputStream(ByteArray(0))
        assertNull("Clean EOF at 0 bytes should return null", ControlCodec.readPrefixed(emptyStream))

        // Streams with 1, 2, 3 bytes then EOF
        for (prefixLen in 1..3) {
            val partialStream = ByteArrayInputStream(ByteArray(prefixLen))
            try {
                ControlCodec.readPrefixed(partialStream)
                fail("Expected EOFException for $prefixLen-byte truncated length prefix")
            } catch (_: EOFException) {
                // Expected
            }
        }
    }

    @Test
    fun challengeControlCodecPayloadSizeBoundaries() {
        // 1. Prefix claims length > 65536 (e.g. 65537)
        val prefix65537 = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(65537).array()
        try {
            ControlCodec.readPrefixed(ByteArrayInputStream(prefix65537))
            fail("Expected IOException for length > 65536")
        } catch (_: IOException) {
            // Expected
        }

        // 2. Prefix claims negative length (-1 / 0xFFFFFFFF)
        val prefixNeg = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(-1).array()
        try {
            ControlCodec.readPrefixed(ByteArrayInputStream(prefixNeg))
            fail("Expected IOException for negative length prefix")
        } catch (_: IOException) {
            // Expected
        }

        // 3. Prefix claims 0x80000000 (minimum negative 32-bit int)
        val prefixMinInt = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(Int.MIN_VALUE).array()
        try {
            ControlCodec.readPrefixed(ByteArrayInputStream(prefixMinInt))
            fail("Expected IOException for Int.MIN_VALUE length prefix")
        } catch (_: IOException) {
            // Expected
        }

        // 4. decodePayload with ByteArray > 65536
        val oversizedArray = ByteArray(65537)
        try {
            ControlCodec.decodePayload(oversizedArray)
            fail("Expected IOException for decodePayload with size > 65536")
        } catch (_: IOException) {
            // Expected
        }
    }

    @Test
    fun challengeControlCodecInvalidEnumValues() {
        // Valid Reject Reasons: BadCode, LockedOut, VersionMismatch, SessionFull
        val invalidEnumJsons = listOf(
            """{"type":"Reject","Reason":"NonExistentReason"}""",
            """{"type":"Reject","Reason":"badcode"}""", // wrong case
            """{"type":"Reject","Reason":0}""",          // integer instead of string
            """{"type":"Reject","Reason":null}"""        // null reason
        )

        for (json in invalidEnumJsons) {
            try {
                ControlCodec.decodePayload(json.toByteArray(StandardCharsets.UTF_8))
                fail("Expected IOException for invalid RejectReason: '$json'")
            } catch (_: IOException) {
                // Expected
            }
        }
    }

    @Test
    fun challengeControlCodecIgnoreUnknownKeysResilience() {
        // C# host or future protocol versions might send extra unknown fields
        val helloWithExtras = """
            {
                "type": "Hello",
                "DeviceId": "d000-1111-2222",
                "DeviceName": "Host Phone",
                "PairingCode": "9999",
                "ProtocolVersion": 2,
                "AudioPort": 47802,
                "ExtraFutureField": "IgnoredData",
                "Capabilities": [1, 2, 3]
            }
        """.trimIndent()

        val decoded = ControlCodec.decodePayload(helloWithExtras.toByteArray(StandardCharsets.UTF_8))
        assertTrue(decoded is Hello)
        val hello = decoded as Hello
        assertEquals("d000-1111-2222", hello.DeviceId)
        assertEquals("Host Phone", hello.DeviceName)
        assertEquals("9999", hello.PairingCode)
    }

    @Test
    fun challengeSetDeviceSettingsValidation() {
        // Valid settings
        val valid = SetDeviceSettings(Volume = 0.5f, OffsetMs = 0.0, EqGainsDb = List(10) { 0f })
        assertTrue(valid.isValid())

        // Invalid volumes: < 0, > 1, NaN, Infinity
        assertFalse(valid.copy(Volume = -0.01f).isValid())
        assertFalse(valid.copy(Volume = 1.01f).isValid())
        assertFalse(valid.copy(Volume = Float.NaN).isValid())
        assertFalse(valid.copy(Volume = Float.POSITIVE_INFINITY).isValid())

        // Invalid offsets: < -100, > 100, NaN
        assertFalse(valid.copy(OffsetMs = -100.1).isValid())
        assertFalse(valid.copy(OffsetMs = 100.1).isValid())
        assertFalse(valid.copy(OffsetMs = Double.NaN).isValid())

        // Invalid EQ: wrong band count (e.g. 9 or 11 bands)
        assertFalse(valid.copy(EqGainsDb = List(9) { 0f }).isValid())
        assertFalse(valid.copy(EqGainsDb = List(11) { 0f }).isValid())

        // Invalid EQ: gains beyond [-12, +12]
        assertFalse(valid.copy(EqGainsDb = List(10) { if (it == 0) -13.0f else 0f }).isValid())
        assertFalse(valid.copy(EqGainsDb = List(10) { if (it == 0) 13.0f else 0f }).isValid())
        assertFalse(valid.copy(EqGainsDb = List(10) { if (it == 0) Float.NaN else 0f }).isValid())

        // Null EQ gains (flat) is valid
        assertTrue(valid.copy(EqGainsDb = null).isValid())
    }

    // =========================================================================
    // 3. Pairing Authentication & Lockout Security Tests
    // =========================================================================

    @Test
    fun challengeFixedTimeEqualsExhaustive() {
        // Exact match
        assertTrue(ControlCodec.fixedTimeEquals("1234", "1234"))
        assertTrue(ControlCodec.fixedTimeEquals("0000", "0000"))
        assertTrue(ControlCodec.fixedTimeEquals("", ""))
        assertTrue(ControlCodec.fixedTimeEquals("Hello, World!", "Hello, World!"))

        // Single digit difference at different positions
        assertFalse(ControlCodec.fixedTimeEquals("1234", "9234"))
        assertFalse(ControlCodec.fixedTimeEquals("1234", "1934"))
        assertFalse(ControlCodec.fixedTimeEquals("1234", "1294"))
        assertFalse(ControlCodec.fixedTimeEquals("1234", "1239"))

        // Length difference
        assertFalse(ControlCodec.fixedTimeEquals("1234", "123"))
        assertFalse(ControlCodec.fixedTimeEquals("1234", "12345"))
        assertFalse(ControlCodec.fixedTimeEquals("", "1"))
        assertFalse(ControlCodec.fixedTimeEquals("1", ""))
    }

    @Test
    fun challengeThreeBadCodesDeviceLockoutExactTiming() {
        val lockoutTicks = 300_000_000L // 30s
        val auth = PairingAuthenticator(lockoutDurationTicks = lockoutTicks, maxDeviceAttempts = 3, maxGlobalAttempts = 10)
        val dev = "device-alpha"
        val expected = "5678"
        val bad = "0000"
        val t0 = 1_000_000_000L

        // Attempt 1: bad -> BadCode
        assertEquals(AuthResult.BadCode, auth.authenticate(dev, bad, expected, t0))
        assertFalse(auth.isDeviceLockedOut(dev, t0))

        // Attempt 2: bad -> BadCode
        assertEquals(AuthResult.BadCode, auth.authenticate(dev, bad, expected, t0 + 1_000_000L))
        assertFalse(auth.isDeviceLockedOut(dev, t0 + 1_000_000L))

        // Attempt 3: bad -> triggers device lockout
        assertEquals(AuthResult.LockedOut, auth.authenticate(dev, bad, expected, t0 + 2_000_000L))
        assertTrue(auth.isDeviceLockedOut(dev, t0 + 2_000_000L))

        // Intermediate checks within 30s lockout window:
        // Even with the CORRECT code, authentication must be rejected with LockedOut
        val tMid = t0 + 2_000_000L + 150_000_000L // 15s into lockout
        assertTrue(auth.isDeviceLockedOut(dev, tMid))
        assertEquals(AuthResult.LockedOut, auth.authenticate(dev, expected, expected, tMid))

        // Just before lockout expires (at 29.999s)
        val tJustBefore = t0 + 2_000_000L + lockoutTicks - 1L
        assertTrue(auth.isDeviceLockedOut(dev, tJustBefore))
        assertEquals(AuthResult.LockedOut, auth.authenticate(dev, expected, expected, tJustBefore))

        // At exact expiry (nowTicks == devLockout): nowTicks < devLockout is false -> unlocked!
        val tAtExpiry = t0 + 2_000_000L + lockoutTicks
        assertFalse(auth.isDeviceLockedOut(dev, tAtExpiry))
        assertEquals(AuthResult.Success, auth.authenticate(dev, expected, expected, tAtExpiry))
    }

    @Test
    fun challengeTenBadCodesGlobalLockoutExactTiming() {
        val lockoutTicks = 300_000_000L // 30s
        val auth = PairingAuthenticator(lockoutDurationTicks = lockoutTicks, maxDeviceAttempts = 3, maxGlobalAttempts = 10)
        val expected = "4321"
        val bad = "9999"
        var t = 10_000_000_000L

        // 9 bad attempts spread across 9 distinct devices (1 attempt per device, so no per-device lockout)
        for (i in 1..9) {
            val dev = "device-$i"
            val res = auth.authenticate(dev, bad, expected, t)
            assertEquals(AuthResult.BadCode, res)
            assertFalse(auth.isDeviceLockedOut(dev, t))
            assertFalse(auth.isGlobalLockedOut(t))
            t += 1_000_000L
        }

        // 10th bad attempt from a 10th distinct device -> triggers global lockout
        val tLockoutTrigger = t
        val res10 = auth.authenticate("device-10", bad, expected, tLockoutTrigger)
        assertEquals(AuthResult.LockedOut, res10)
        assertTrue(auth.isGlobalLockedOut(tLockoutTrigger))

        // Any device (even a clean, new device) attempting with CORRECT code is locked out
        val tMid = tLockoutTrigger + 100_000_000L // 10s into global lockout
        assertTrue(auth.isGlobalLockedOut(tMid))
        assertEquals(AuthResult.LockedOut, auth.authenticate("brand-new-device", expected, expected, tMid))

        // At exact expiry of global lockout
        val tExpiry = tLockoutTrigger + lockoutTicks
        assertFalse(auth.isGlobalLockedOut(tExpiry))
        assertEquals(AuthResult.Success, auth.authenticate("brand-new-device", expected, expected, tExpiry))
    }

    @Test
    fun challengePruningOfOldFailedAttemptsOutsideWindow() {
        val lockoutTicks = 300_000_000L // 30s
        val auth = PairingAuthenticator(lockoutDurationTicks = lockoutTicks, maxDeviceAttempts = 3, maxGlobalAttempts = 10)
        val dev = "device-slow-attacker"
        val expected = "1111"
        val bad = "0000"
        var t = 1_000_000L

        // Attempt 1: bad at t
        assertEquals(AuthResult.BadCode, auth.authenticate(dev, bad, expected, t))

        // Attempt 2: bad at t + 35s (first attempt has expired from 30s window)
        t += 350_000_000L
        assertEquals(AuthResult.BadCode, auth.authenticate(dev, bad, expected, t))

        // Attempt 3: bad at t + 35s (second attempt has expired)
        t += 350_000_000L
        assertEquals(AuthResult.BadCode, auth.authenticate(dev, bad, expected, t))

        // Should NOT be locked out because attempts were spaced > 30s apart
        assertFalse(auth.isDeviceLockedOut(dev, t))
    }
}
