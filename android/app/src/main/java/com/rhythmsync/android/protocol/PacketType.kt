package com.rhythmsync.android.protocol

enum class PacketType(val value: Byte) {
    Audio(1),
    Ping(2),
    Pong(3),
    Beacon(4);

    companion object {
        fun fromByte(value: Byte): PacketType? = values().firstOrNull { it.value == value }
    }
}
