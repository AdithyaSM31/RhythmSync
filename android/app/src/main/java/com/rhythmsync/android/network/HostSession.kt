package com.rhythmsync.android.network

import com.rhythmsync.android.protocol.*
import java.io.Closeable
import java.io.IOException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import kotlin.concurrent.thread
import kotlin.random.Random

data class HostClientInfo(
    val deviceId: String,
    val name: String,
    val address: InetSocketAddress,
    val audioPort: Int,
    var volume: Float = 1.0f,
    var offsetMs: Double = 0.0,
    var eqGainsDb: List<Float> = List(10) { 0f },
    var lastSeenTicks: Long = 0L,
    var stats: HeartbeatStats? = null,
    var isReady: Boolean = false
)

class HostSession(
    val name: String,
    val sessionId: Long = Random.nextLong(1, Long.MAX_VALUE),
    val pairingCode: String = String.format("%04d", Random.nextInt(10000)),
    val controlPort: Int = 47801,
    val clockPort: Int = 47803,
    val audioPort: Int = 47802,
    var targetLatencyMs: Int = 200,
    val maxClients: Int = 8,
    val clockNowTicks: () -> Long = { System.nanoTime() / 100L },
    val audioStreamer: AudioStreamer? = null
) : Closeable {

    companion object {
        const val PROTOCOL_VERSION = 2
        const val MAX_PENDING_CONNECTIONS = 32
        const val SILENCE_LIMIT_TICKS = 30_000_000L // 3.0 seconds in 100ns ticks
        const val HEARTBEAT_INTERVAL_TICKS = 10_000_000L // 1.0 second in 100ns ticks
    }

    private val isClosed = AtomicBoolean(false)
    private val isStarted = AtomicBoolean(false)
    private val authenticator = PairingAuthenticator()

    private var serverSocket: ServerSocket? = null
    private var acceptThread: Thread? = null

    private val clientConnections = ConcurrentHashMap<String, ClientConnection>()
    private val pendingCount = AtomicInteger(0)
    private var lastHeartbeatTicks: Long = 0L

    val connectedClients: List<HostClientInfo>
        get() = clientConnections.values.map { it.info }

    val activeClientCount: Int
        get() = clientConnections.size

    var onClientListChanged: (() -> Unit)? = null

    private inner class ClientConnection(
        val socket: Socket,
        val info: HostClientInfo
    ) {
        val isConnectionClosed = AtomicBoolean(false)
        private var rxThread: Thread? = null

        fun startReading() {
            rxThread = thread(start = true, isDaemon = true, name = "host-client-rx-${info.deviceId}") {
                try {
                    val input = socket.getInputStream()
                    while (!isClosed.get() && !isConnectionClosed.get()) {
                        val msg = ControlCodec.readPrefixed(input) ?: break
                        handleClientMessage(this, msg)
                    }
                } catch (_: Exception) {
                } finally {
                    closeConnection()
                }
            }
        }

        fun send(msg: ControlMessage) {
            if (isConnectionClosed.get() || isClosed.get()) return
            try {
                synchronized(socket) {
                    ControlCodec.writePrefixed(msg, socket.getOutputStream())
                }
            } catch (_: Exception) {
                closeConnection()
            }
        }

        fun closeConnection() {
            if (isConnectionClosed.getAndSet(true)) return
            try { socket.close() } catch (_: Exception) {}
            clientConnections.remove(info.deviceId)
            audioStreamer?.removeClient(info.deviceId)
            onClientListChanged?.invoke()
        }
    }

    fun start() {
        check(!isStarted.getAndSet(true)) { "HostSession already started" }
        check(!isClosed.get()) { "HostSession already closed" }

        val ss = ServerSocket()
        ss.reuseAddress = true
        ss.bind(InetSocketAddress(controlPort))
        serverSocket = ss

        acceptThread = thread(start = true, isDaemon = true, name = "host-tcp-accept") {
            while (!isClosed.get()) {
                try {
                    val clientSocket = ss.accept()
                    if (pendingCount.get() >= MAX_PENDING_CONNECTIONS) {
                        try { clientSocket.close() } catch (_: Exception) {}
                        continue
                    }
                    pendingCount.incrementAndGet()
                    thread(start = true, isDaemon = true, name = "host-handshake") {
                        try {
                            handleHandshake(clientSocket)
                        } finally {
                            pendingCount.decrementAndGet()
                        }
                    }
                } catch (_: Exception) {
                    if (isClosed.get()) break
                }
            }
        }
    }

    private fun handleHandshake(socket: Socket) {
        try {
            socket.soTimeout = 5000 // 5.0s handshake timeout
            val input = socket.getInputStream()
            val msg = ControlCodec.readPrefixed(input)
            val now = clockNowTicks()

            if (msg !is Hello) {
                socket.close()
                return
            }

            if (msg.ProtocolVersion != PROTOCOL_VERSION) {
                ControlCodec.writePrefixed(Reject(RejectReason.VersionMismatch), socket.getOutputStream())
                socket.close()
                return
            }

            val authResult = authenticator.authenticate(msg.DeviceId, msg.PairingCode, pairingCode, now)
            when (authResult) {
                AuthResult.LockedOut -> {
                    ControlCodec.writePrefixed(Reject(RejectReason.LockedOut), socket.getOutputStream())
                    socket.close()
                    return
                }
                AuthResult.BadCode -> {
                    ControlCodec.writePrefixed(Reject(RejectReason.BadCode), socket.getOutputStream())
                    socket.close()
                    return
                }
                AuthResult.Success -> {
                    // Proceed
                }
            }

            synchronized(clientConnections) {
                if (clientConnections.size >= maxClients && !clientConnections.containsKey(msg.DeviceId)) {
                    ControlCodec.writePrefixed(Reject(RejectReason.SessionFull), socket.getOutputStream())
                    socket.close()
                    return
                }

                // If reconnecting with same deviceId, replace existing
                clientConnections[msg.DeviceId]?.closeConnection()

                val remoteAddress = socket.remoteSocketAddress as InetSocketAddress
                val clientInfo = HostClientInfo(
                    deviceId = msg.DeviceId,
                    name = msg.DeviceName.trim().take(64).ifEmpty { "Client" },
                    address = remoteAddress,
                    audioPort = msg.AudioPort,
                    lastSeenTicks = now
                )

                val conn = ClientConnection(socket, clientInfo)
                clientConnections[msg.DeviceId] = conn

                // Send Welcome
                val welcome = Welcome(
                    SessionId = sessionId,
                    SampleRate = 48000,
                    Channels = 2,
                    FrameSamples = 240,
                    TargetLatencyMs = targetLatencyMs,
                    AudioPort = audioPort,
                    ClockPort = clockPort
                )
                ControlCodec.writePrefixed(welcome, socket.getOutputStream())
                socket.soTimeout = 0 // Remove timeout for persistent connection
                conn.startReading()
                onClientListChanged?.invoke()
            }
        } catch (_: Exception) {
            try { socket.close() } catch (_: Exception) {}
        }
    }

    private fun handleClientMessage(conn: ClientConnection, msg: ControlMessage) {
        val now = clockNowTicks()
        conn.info.lastSeenTicks = now

        when (msg) {
            is Ready -> {
                conn.info.isReady = true
                val audioTarget = InetSocketAddress(conn.info.address.address, conn.info.audioPort)
                audioStreamer?.addClient(conn.info.deviceId, audioTarget)
                onClientListChanged?.invoke()
            }
            is Heartbeat -> {
                conn.info.stats = msg.Stats
            }
            is Bye -> {
                conn.closeConnection()
            }
            else -> {
                // Ignore unhandled messages
            }
        }
    }

    fun tick() {
        if (isClosed.get()) return
        val now = clockNowTicks()

        // Check silent clients
        for ((_, conn) in clientConnections) {
            if (now - conn.info.lastSeenTicks > SILENCE_LIMIT_TICKS) {
                conn.closeConnection()
            }
        }

        // Periodic Heartbeat
        if (now - lastHeartbeatTicks >= HEARTBEAT_INTERVAL_TICKS) {
            lastHeartbeatTicks = now
            val hb = Heartbeat(null)
            for ((_, conn) in clientConnections) {
                conn.send(hb)
            }
        }
    }

    fun setDeviceSettings(deviceId: String, volume: Float, offsetMs: Double, eqGainsDb: List<Float>?) {
        val conn = clientConnections[deviceId] ?: return
        val clampedVol = volume.coerceIn(0f, 1f)
        val clampedOffset = offsetMs.coerceIn(-100.0, 100.0)
        val validEq = if (eqGainsDb != null && eqGainsDb.size == 10) {
            eqGainsDb.map { it.coerceIn(-12f, 12f) }
        } else {
            conn.info.eqGainsDb
        }

        conn.info.volume = clampedVol
        conn.info.offsetMs = clampedOffset
        conn.info.eqGainsDb = validEq

        val msg = SetDeviceSettings(clampedVol, clampedOffset, validEq)
        conn.send(msg)
        onClientListChanged?.invoke()
    }

    fun setLatency(latencyMs: Int) {
        val clamped = latencyMs.coerceIn(100, 500)
        targetLatencyMs = clamped
        val msg = SetLatency(clamped)
        for ((_, conn) in clientConnections) {
            conn.send(msg)
        }
    }

    override fun close() {
        if (isClosed.getAndSet(true)) return
        try { serverSocket?.close() } catch (_: Exception) {}
        for ((_, conn) in clientConnections) {
            conn.closeConnection()
        }
        clientConnections.clear()
    }
}
