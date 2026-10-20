package com.rhythmsync.android.protocol

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.json.Json
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets

object ControlCodec {
    const val MAX_MESSAGE_BYTES = 65536 // 64 KB

    @OptIn(ExperimentalSerializationApi::class)
    val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
        allowSpecialFloatingPointValues = true
        isLenient = true
    }

    /**
     * Serializes a [ControlMessage] into UTF-8 JSON bytes prefixed by a 4-byte little-endian length.
     */
    fun encode(message: ControlMessage): ByteArray {
        val jsonString = json.encodeToString(ControlMessage.serializer(), message)
        val payloadBytes = jsonString.toByteArray(StandardCharsets.UTF_8)
        if (payloadBytes.size > MAX_MESSAGE_BYTES) {
            throw IOException("Control message exceeds maximum size of $MAX_MESSAGE_BYTES bytes: ${payloadBytes.size}")
        }
        val buffer = ByteBuffer.allocate(4 + payloadBytes.size).order(ByteOrder.LITTLE_ENDIAN)
        buffer.putInt(payloadBytes.size)
        buffer.put(payloadBytes)
        return buffer.array()
    }

    /**
     * Encodes and writes a [ControlMessage] to an [OutputStream].
     */
    fun writePrefixed(message: ControlMessage, outputStream: OutputStream) {
        val bytes = encode(message)
        outputStream.write(bytes)
        outputStream.flush()
    }

    /**
     * Deserializes a [ControlMessage] from raw UTF-8 JSON bytes (without 4-byte prefix).
     */
    fun decodePayload(payloadBytes: ByteArray): ControlMessage {
        if (payloadBytes.size > MAX_MESSAGE_BYTES) {
            throw IOException("Payload size ${payloadBytes.size} exceeds maximum $MAX_MESSAGE_BYTES bytes")
        }
        val jsonString = String(payloadBytes, StandardCharsets.UTF_8)
        return try {
            json.decodeFromString(ControlMessage.serializer(), jsonString)
        } catch (e: Exception) {
            throw IOException("Failed to deserialize control message: ${e.message}", e)
        }
    }

    /**
     * Reads a 4-byte LE length-prefixed [ControlMessage] from [InputStream].
     * Returns null on clean stream closure at message boundary.
     * Throws [IOException] on truncation, oversized messages, or syntax errors.
     */
    fun readPrefixed(inputStream: InputStream): ControlMessage? {
        val lengthBuffer = ByteArray(4)
        var totalRead = 0
        while (totalRead < 4) {
            val read = inputStream.read(lengthBuffer, totalRead, 4 - totalRead)
            if (read == -1) {
                if (totalRead == 0) return null // Clean EOF at message boundary
                throw EOFException("Stream closed unexpectedly while reading message length prefix ($totalRead of 4 bytes read)")
            }
            totalRead += read
        }

        val length = ByteBuffer.wrap(lengthBuffer).order(ByteOrder.LITTLE_ENDIAN).int
        if (length < 0 || length > MAX_MESSAGE_BYTES) {
            throw IOException("Invalid control message length: $length (max allowed is $MAX_MESSAGE_BYTES)")
        }

        val payload = ByteArray(length)
        var payloadRead = 0
        while (payloadRead < length) {
            val read = inputStream.read(payload, payloadRead, length - payloadRead)
            if (read == -1) {
                throw EOFException("Stream closed unexpectedly while reading message payload ($payloadRead of $length bytes read)")
            }
            payloadRead += read
        }

        return decodePayload(payload)
    }

    /**
     * Constant-time comparison between two strings to prevent timing side-channel attacks.
     */
    fun fixedTimeEquals(a: String, b: String): Boolean {
        val aBytes = a.toByteArray(StandardCharsets.UTF_8)
        val bBytes = b.toByteArray(StandardCharsets.UTF_8)
        var result = 0
        val len = maxOf(aBytes.size, bBytes.size)
        for (i in 0 until len) {
            val bA = if (i < aBytes.size) aBytes[i].toInt() else 0
            val bB = if (i < bBytes.size) bBytes[i].toInt() else 0
            result = result or (bA xor bB)
        }
        result = result or (aBytes.size xor bBytes.size)
        return result == 0
    }
}

enum class AuthResult {
    Success,
    BadCode,
    LockedOut
}

/**
 * Enforces rate limiting and timing protection for 4-digit pairing authentication:
 * - 3 incorrect attempts for the same deviceId triggers a 30s device lockout.
 * - 10 incorrect attempts globally triggers a 30s global lockout.
 */
class PairingAuthenticator(
    val lockoutDurationTicks: Long = 300_000_000L, // 30 seconds in 100ns ticks
    val maxDeviceAttempts: Int = 3,
    val maxGlobalAttempts: Int = 10
) {
    private val deviceBadAttempts = mutableMapOf<String, MutableList<Long>>()
    private val deviceLockouts = mutableMapOf<String, Long>()
    private val globalBadAttempts = mutableListOf<Long>()
    private var globalLockoutUntil: Long = 0L

    @Synchronized
    fun authenticate(
        deviceId: String,
        providedCode: String,
        expectedCode: String,
        nowTicks: Long
    ): AuthResult {
        // Prune old timestamps older than lockoutDurationTicks
        val cutoff = nowTicks - lockoutDurationTicks

        val devLockout = deviceLockouts[deviceId] ?: 0L
        if (nowTicks < devLockout || nowTicks < globalLockoutUntil) {
            return AuthResult.LockedOut
        }

        val matches = ControlCodec.fixedTimeEquals(providedCode, expectedCode)
        if (matches) {
            deviceBadAttempts.remove(deviceId)
            deviceLockouts.remove(deviceId)
            return AuthResult.Success
        }

        // Record failed attempt
        val devAttempts = deviceBadAttempts.getOrPut(deviceId) { mutableListOf() }
        devAttempts.removeAll { it < cutoff }
        devAttempts.add(nowTicks)

        globalBadAttempts.removeAll { it < cutoff }
        globalBadAttempts.add(nowTicks)

        if (devAttempts.size >= maxDeviceAttempts) {
            deviceLockouts[deviceId] = nowTicks + lockoutDurationTicks
        }
        if (globalBadAttempts.size >= maxGlobalAttempts) {
            globalLockoutUntil = nowTicks + lockoutDurationTicks
        }

        val isLocked = (deviceLockouts[deviceId] ?: 0L) > nowTicks || globalLockoutUntil > nowTicks
        return if (isLocked) AuthResult.LockedOut else AuthResult.BadCode
    }

    @Synchronized
    fun isDeviceLockedOut(deviceId: String, nowTicks: Long): Boolean {
        val devLockout = deviceLockouts[deviceId] ?: 0L
        return nowTicks < devLockout || nowTicks < globalLockoutUntil
    }

    @Synchronized
    fun isGlobalLockedOut(nowTicks: Long): Boolean {
        return nowTicks < globalLockoutUntil
    }
}
