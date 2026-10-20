package com.rhythmsync.android.protocol

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonClassDiscriminator

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

@Serializable
data class HeartbeatStats(
    val LossPct: Double = Double.NaN,
    val LatePct: Double = Double.NaN,
    val DriftPpm: Double = 0.0,
    val FitResidualMs: Double = 0.0,
    val SyncErrorMs: Double = Double.NaN,
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
