package com.rhythmsync.android.protocol

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.IOException

class ControlCodecTest {

    @Test
    fun testAllControlMessagesRoundTrip() {
        val messages: List<ControlMessage> = listOf(
            Hello(
                DeviceId = "e0b5b111-6677-4c07-b353-2bf9b4862dc7",
                DeviceName = "Living Room Android",
                PairingCode = "4821",
                ProtocolVersion = 2,
                AudioPort = 47802
            ),
            Welcome(
                SessionId = 0x8899AABBL,
                SampleRate = 48000,
                Channels = 2,
                FrameSamples = 240,
                TargetLatencyMs = 250,
                AudioPort = 47802,
                ClockPort = 47803
            ),
            Reject(Reason = RejectReason.BadCode),
            Reject(Reason = RejectReason.LockedOut),
            Reject(Reason = RejectReason.VersionMismatch),
            Reject(Reason = RejectReason.SessionFull),
            Ready(),
            Heartbeat(Stats = null),
            Heartbeat(
                Stats = HeartbeatStats(
                    LossPct = 0.5,
                    LatePct = 0.1,
                    DriftPpm = -14.2,
                    FitResidualMs = 0.18,
                    SyncErrorMs = 0.32,
                    State = "InSync"
                )
            ),
            SetDeviceSettings(
                Volume = 0.75f,
                OffsetMs = -12.5,
                EqGainsDb = listOf(-9f, -6f, -1f, 2f, 1f, 0f, 0f, 1f, 2f, 1f)
            ),
            SetLatency(TargetLatencyMs = 300),
            Bye()
        )

        val bos = ByteArrayOutputStream()
        for (msg in messages) {
            ControlCodec.writePrefixed(msg, bos)
        }

        val bis = ByteArrayInputStream(bos.toByteArray())
        for (expected in messages) {
            val decoded = ControlCodec.readPrefixed(bis)
            assertNotNull("Expected message but got null", decoded)
            assertEquals("Message type mismatch", expected::class, decoded!!::class)

            when (expected) {
                is Hello -> assertEquals(expected, decoded)
                is Welcome -> assertEquals(expected, decoded)
                is Reject -> assertEquals(expected, decoded)
                is Ready -> assertTrue(decoded is Ready)
                is Heartbeat -> {
                    val actual = decoded as Heartbeat
                    if (expected.Stats == null) {
                        assertNull(actual.Stats)
                    } else {
                        assertNotNull(actual.Stats)
                        assertEquals(expected.Stats!!.LossPct, actual.Stats!!.LossPct, 1e-6)
                        assertEquals(expected.Stats!!.LatePct, actual.Stats!!.LatePct, 1e-6)
                        assertEquals(expected.Stats!!.DriftPpm, actual.Stats!!.DriftPpm, 1e-6)
                        assertEquals(expected.Stats!!.FitResidualMs, actual.Stats!!.FitResidualMs, 1e-6)
                        assertEquals(expected.Stats!!.SyncErrorMs, actual.Stats!!.SyncErrorMs, 1e-6)
                        assertEquals(expected.Stats!!.State, actual.Stats!!.State)
                    }
                }
                is SetDeviceSettings -> assertEquals(expected, decoded)
                is SetLatency -> assertEquals(expected, decoded)
                is Bye -> assertTrue(decoded is Bye)
            }
        }

        // Next read should be null on clean stream end
        assertNull(ControlCodec.readPrefixed(bis))
    }

    @Test
    fun testHeartbeatSpecialFloatingPointValues() {
        val hb = Heartbeat(
            Stats = HeartbeatStats(
                LossPct = Double.NaN,
                LatePct = Double.NaN,
                DriftPpm = 0.0,
                FitResidualMs = 0.0,
                SyncErrorMs = Double.NaN,
                State = "Syncing"
            )
        )
        val bytes = ControlCodec.encode(hb)
        val decoded = ControlCodec.readPrefixed(ByteArrayInputStream(bytes)) as Heartbeat
        assertNotNull(decoded.Stats)
        assertTrue(decoded.Stats!!.LossPct.isNaN())
        assertTrue(decoded.Stats!!.LatePct.isNaN())
        assertTrue(decoded.Stats!!.SyncErrorMs.isNaN())
    }

    @Test
    fun testMaxMessageSizeExceededThrows() {
        // Construct prefix claiming length 70,000 bytes
        val buffer = java.nio.ByteBuffer.allocate(4).order(java.nio.ByteOrder.LITTLE_ENDIAN)
        buffer.putInt(70000)
        val stream = ByteArrayInputStream(buffer.array())

        try {
            ControlCodec.readPrefixed(stream)
            fail("Expected IOException for oversized message")
        } catch (_: IOException) {
            // Success
        }
    }

    @Test
    fun testTruncatedStreamThrowsEOFException() {
        val hello = Hello("dev1", "Phone", "1234", 2, 47802)
        val full = ControlCodec.encode(hello)

        // Truncate payload
        val truncated = full.copyOfRange(0, full.size - 5)
        try {
            ControlCodec.readPrefixed(ByteArrayInputStream(truncated))
            fail("Expected EOFException on truncated stream")
        } catch (_: EOFException) {
            // Success
        }
    }

    @Test
    fun testFixedTimeEquals() {
        assertTrue(ControlCodec.fixedTimeEquals("4821", "4821"))
        assertTrue(ControlCodec.fixedTimeEquals("", ""))
        assertFalse(ControlCodec.fixedTimeEquals("4821", "4820"))
        assertFalse(ControlCodec.fixedTimeEquals("4821", "48210"))
        assertFalse(ControlCodec.fixedTimeEquals("482", "4821"))
    }

    @Test
    fun testPairingLockoutPerDevice() {
        val auth = PairingAuthenticator(lockoutDurationTicks = 300_000_000L) // 30s
        val dev1 = "device-1"
        val dev2 = "device-2"
        val correctCode = "1234"
        val wrongCode = "9999"
        val t0 = 100_000_000L

        // Attempt 1: BadCode
        assertEquals(AuthResult.BadCode, auth.authenticate(dev1, wrongCode, correctCode, t0))
        // Attempt 2: BadCode
        assertEquals(AuthResult.BadCode, auth.authenticate(dev1, wrongCode, correctCode, t0 + 10_000_000L))
        // Attempt 3: BadCode -> now locked out
        assertEquals(AuthResult.LockedOut, auth.authenticate(dev1, wrongCode, correctCode, t0 + 20_000_000L))

        // Subsequent attempt with correct code while locked out must still be LockedOut
        assertEquals(AuthResult.LockedOut, auth.authenticate(dev1, correctCode, correctCode, t0 + 30_000_000L))

        // Device 2 is NOT locked out
        assertEquals(AuthResult.Success, auth.authenticate(dev2, correctCode, correctCode, t0 + 30_000_000L))

        // After 30s passes, dev1 can authenticate with correct code
        val tAfterLockout = t0 + 350_000_000L
        assertEquals(AuthResult.Success, auth.authenticate(dev1, correctCode, correctCode, tAfterLockout))
    }

    @Test
    fun testPairingLockoutGlobal() {
        val auth = PairingAuthenticator(lockoutDurationTicks = 300_000_000L, maxDeviceAttempts = 3, maxGlobalAttempts = 10)
        val correctCode = "7777"
        val wrongCode = "0000"
        var time = 1_000_000L

        // 9 attempts across 5 devices (each device < 3 attempts)
        for (i in 1..9) {
            val dev = "device-${i % 5}"
            val res = auth.authenticate(dev, wrongCode, correctCode, time)
            assertEquals(AuthResult.BadCode, res)
            time += 1_000_000L
        }

        // 10th attempt across devices triggers global lockout
        val res10 = auth.authenticate("device-99", wrongCode, correctCode, time)
        assertEquals(AuthResult.LockedOut, res10)

        // Any device trying correct code is now globally locked out
        assertEquals(AuthResult.LockedOut, auth.authenticate("brand-new-device", correctCode, correctCode, time + 1_000_000L))
    }
}
