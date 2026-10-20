package com.rhythmsync.android.protocol

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.SerializationException
import kotlinx.serialization.Serializable
import kotlinx.serialization.KSerializer
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.JsonClassDiscriminator
import kotlinx.serialization.json.JsonDecoder
import kotlinx.serialization.json.JsonEncoder
import kotlinx.serialization.json.JsonPrimitive

@OptIn(ExperimentalSerializationApi::class)
@Serializable
@JsonClassDiscriminator("type")
sealed class ControlMessage

@Serializable
@SerialName("Hello")
data class Hello(
    val DeviceId: String,
    val DeviceName: String,
    val PairingCode: String,
    val ProtocolVersion: Int = 2,
    val AudioPort: Int = 47802
) : ControlMessage()

@Serializable
@SerialName("Welcome")
data class Welcome(
    val SessionId: Long,
    val SampleRate: Int = 48000,
    val Channels: Int = 2,
    val FrameSamples: Int = 240,
    val TargetLatencyMs: Int = 200,
    val AudioPort: Int = 47802,
    val ClockPort: Int = 47803
) : ControlMessage()

@Serializable
enum class RejectReason {
    BadCode,
    LockedOut,
    VersionMismatch,
    SessionFull
}

@Serializable
@SerialName("Reject")
data class Reject(
    val Reason: RejectReason
) : ControlMessage()

@Serializable
@SerialName("Ready")
class Ready : ControlMessage() {
    override fun equals(other: Any?): Boolean = other is Ready
    override fun hashCode(): Int = javaClass.hashCode()
    override fun toString(): String = "Ready()"
}

/**
 * Writes a [Double] the way the Windows codec (System.Text.Json, AllowNamedFloatingPointLiterals) does: finite values
 * as plain JSON numbers, NaN and the infinities as the quoted strings "NaN", "Infinity" and "-Infinity". Reads both
 * numbers and quoted numbers/literals (and, leniently, bare NaN/Infinity tokens).
 */
object NonFiniteDoubleSerializer : KSerializer<Double> {
    override val descriptor: SerialDescriptor = PrimitiveSerialDescriptor("NonFiniteDouble", PrimitiveKind.DOUBLE)

    override fun serialize(encoder: Encoder, value: Double) {
        when {
            value.isNaN() -> writeLiteral(encoder, "NaN")
            value == Double.POSITIVE_INFINITY -> writeLiteral(encoder, "Infinity")
            value == Double.NEGATIVE_INFINITY -> writeLiteral(encoder, "-Infinity")
            else -> encoder.encodeDouble(value)
        }
    }

    private fun writeLiteral(encoder: Encoder, literal: String) {
        if (encoder is JsonEncoder) encoder.encodeJsonElement(JsonPrimitive(literal)) else encoder.encodeString(literal)
    }

    override fun deserialize(decoder: Decoder): Double {
        if (decoder !is JsonDecoder) return decoder.decodeDouble()
        val primitive = decoder.decodeJsonElement() as? JsonPrimitive
            ?: throw SerializationException("Expected a number or \"NaN\"/\"Infinity\"/\"-Infinity\"")
        return parse(primitive.content)
            ?: throw SerializationException("Not a number: '${primitive.content}'")
    }

    private fun parse(text: String): Double? = when (text) {
        "NaN" -> Double.NaN
        "Infinity", "+Infinity" -> Double.POSITIVE_INFINITY
        "-Infinity" -> Double.NEGATIVE_INFINITY
        else -> text.toDoubleOrNull()
    }
}

@Serializable
data class HeartbeatStats(
    @Serializable(with = NonFiniteDoubleSerializer::class) val LossPct: Double = Double.NaN,
    @Serializable(with = NonFiniteDoubleSerializer::class) val LatePct: Double = Double.NaN,
    @Serializable(with = NonFiniteDoubleSerializer::class) val DriftPpm: Double = 0.0,
    @Serializable(with = NonFiniteDoubleSerializer::class) val FitResidualMs: Double = 0.0,
    @Serializable(with = NonFiniteDoubleSerializer::class) val SyncErrorMs: Double = Double.NaN,
    val State: String = "InSync"
)

@Serializable
@SerialName("Heartbeat")
data class Heartbeat(
    val Stats: HeartbeatStats? = null
) : ControlMessage()

@Serializable
@SerialName("SetDeviceSettings")
data class SetDeviceSettings(
    val Volume: Float,
    val OffsetMs: Double,
    val EqGainsDb: List<Float>? = null
) : ControlMessage() {
    fun isValid(): Boolean {
        if (!Volume.isFinite() || Volume < 0f || Volume > 1f) return false
        if (!OffsetMs.isFinite() || OffsetMs < -100.0 || OffsetMs > 100.0) return false
        if (EqGainsDb != null) {
            if (EqGainsDb.size != 10) return false
            if (EqGainsDb.any { !it.isFinite() || it < -12.001f || it > 12.001f }) return false
        }
        return true
    }
}

@Serializable
@SerialName("SetLatency")
data class SetLatency(
    val TargetLatencyMs: Int
) : ControlMessage() {
    fun isValid(): Boolean = TargetLatencyMs in 100..500
}

@Serializable
@SerialName("Bye")
class Bye : ControlMessage() {
    override fun equals(other: Any?): Boolean = other is Bye
    override fun hashCode(): Int = javaClass.hashCode()
    override fun toString(): String = "Bye()"
}
