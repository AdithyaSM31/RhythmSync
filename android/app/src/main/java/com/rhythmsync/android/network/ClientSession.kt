package com.rhythmsync.android.network

import com.rhythmsync.android.audio.AudioTrackPlayer
import com.rhythmsync.android.dsp.EqProcessor
import com.rhythmsync.android.protocol.*
import com.rhythmsync.android.sync.ClockEstimator
import com.rhythmsync.android.sync.JitterBuffer
import com.rhythmsync.android.sync.PlaybackScheduler
import com.rhythmsync.android.sync.SyncState
import java.io.Closeable
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import kotlin.concurrent.thread
import kotlin.random.Random

class ClientSession(
    val deviceId: String = "client-" + Random.nextLong(1, 1_000_000_000).toString(16),
    val deviceName: String = "Android Device",
    val preferredAudioPort: Int = 47802,
    val clockNowTicks: () -> Long = { System.nanoTime() / 100L }
) : Closeable {

    companion object {
        const val PROTOCOL_VERSION = 2
        const val SILENCE_LIMIT_TICKS = 30_000_000L // 3.0 seconds in 100ns ticks
        const val HEARTBEAT_INTERVAL_TICKS = 10_000_000L // 1.0 second in 100ns ticks
        const val RECONNECT_RETRY_TICKS = 20_000_000L // 2.0 seconds
        const val RECONNECT_GIVE_UP_TICKS = 600_000_000L // 60.0 seconds
    }

    private val isDisposed = AtomicBoolean(false)
    private val stateRef = AtomicReference(SyncState.Disconnected)

    val state: SyncState get() = stateRef.get()

    var onStateChanged: ((SyncState) -> Unit)? = null
    var onSettingsChanged: (() -> Unit)? = null

    var volume: Float = 1.0f
        private set
    var offsetMs: Double = 0.0
        private set
    var eqGainsDb: List<Float> = List(10) { 0f }
        private set

    var currentHost: DiscoveredHost? = null
        private set

    var jitterBufferHealth: Double = 1.0
        private set
    var clockOffsetMs: Double = 0.0
        private set
    var jitterResidualMs: Double = 0.0
        private set

    // Internal network and audio components
    private var tcpSocket: Socket? = null
    private var rxThread: Thread? = null

    private var audioChannel: IDatagramChannel? = null
    private var clockChannel: IDatagramChannel? = null

    private var clockClient: ClockSyncClient? = null
    private var clockEstimator: ClockEstimator? = null
    private var jitterBuffer: JitterBuffer? = null
    private var audioReceiver: AudioReceiver? = null
    private var scheduler: PlaybackScheduler? = null
    private var audioPlayer: AudioTrackPlayer? = null
    private var eqProcessor: EqProcessor? = null

    private var lastHeardTicks: Long = 0L
    private var lastHeartbeatTicks: Long = 0L
    private var disconnectTicks: Long = 0L

    private var rememberedHost: InetSocketAddress? = null
    private var rememberedPin: String? = null
    private var isReconnecting = false

    private fun setState(newState: SyncState) {
        val old = stateRef.getAndSet(newState)
        if (old != newState) {
            onStateChanged?.invoke(newState)
        }
    }

    fun join(hostAddress: InetSocketAddress, pin: String) {
        check(!isDisposed.get()) { "ClientSession disposed" }
        rememberedHost = hostAddress
        rememberedPin = pin
        isReconnecting = false

        setState(SyncState.Connecting)

        thread(start = true, isDaemon = true, name = "client-connect") {
            performConnect(hostAddress, pin)
        }
    }

    private fun performConnect(hostAddress: InetSocketAddress, pin: String) {
        try {
            cleanupActiveSession()

            // 1. Bind audio channel (prefer 47802, fallback to ephemeral port 0)
            val audioChan = try {
                UdpDatagramChannel(InetSocketAddress(preferredAudioPort))
            } catch (_: Exception) {
                UdpDatagramChannel(InetSocketAddress(0))
            }
            audioChannel = audioChan

            // 2. Connect TCP socket
            val socket = Socket()
            socket.soTimeout = 5000 // 5.0s handshake timeout
            socket.connect(hostAddress, 5000)
            tcpSocket = socket

            // 3. Send Hello
            val hello = Hello(
                DeviceId = deviceId,
                DeviceName = deviceName,
                PairingCode = pin,
                ProtocolVersion = PROTOCOL_VERSION,
                AudioPort = audioChan.localPort
            )
            ControlCodec.writePrefixed(hello, socket.getOutputStream())

            // 4. Read Welcome or Reject
            val response = ControlCodec.readPrefixed(socket.getInputStream())
            if (response is Reject) {
                socket.close()
                if (response.Reason == RejectReason.BadCode || response.Reason == RejectReason.LockedOut) {
                    rememberedHost = null
                    rememberedPin = null
                }
                setState(SyncState.Disconnected)
                return
            }

            if (response !is Welcome) {
                socket.close()
                setState(SyncState.Disconnected)
                return
            }

            // 5. Successful Welcome
            val welcome = response
            val now = clockNowTicks()
            lastHeardTicks = now
            lastHeartbeatTicks = now

            val est = ClockEstimator()
            clockEstimator = est

            val clockChan = UdpDatagramChannel(InetSocketAddress(0))
            clockChannel = clockChan
            val clockServerAddr = InetSocketAddress(hostAddress.address, welcome.ClockPort)
            val cClient = ClockSyncClient(clockChan, clockServerAddr, welcome.SessionId, est, clockNowTicks)
            cClient.start()
            clockClient = cClient

            val jb = JitterBuffer(capacity = 256)
            jitterBuffer = jb

            val aReceiver = AudioReceiver(audioChan, hostAddress.address, welcome.SessionId, jb)
            aReceiver.start()
            audioReceiver = aReceiver

            val eq = EqProcessor(sampleRate = welcome.SampleRate, channels = welcome.Channels)
            eqProcessor = eq

            val sched = PlaybackScheduler(
                deviceSampleRate = welcome.SampleRate,
                targetLatencyMs = welcome.TargetLatencyMs
            )
            scheduler = sched

            val player = AudioTrackPlayer(
                jitterBuffer = jb,
                scheduler = sched,
                eqProcessor = eq,
                clockEstimator = est,
                targetLatencyMs = welcome.TargetLatencyMs
            )
            audioPlayer = player

            socket.soTimeout = 0 // Persistent connection
            startReading(socket)

            setState(SyncState.Syncing)

        } catch (_: Exception) {
            handleConnectionLoss()
        }
    }

    private fun startReading(socket: Socket) {
        rxThread = thread(start = true, isDaemon = true, name = "client-tcp-rx") {
            try {
                val input = socket.getInputStream()
                while (!isDisposed.get()) {
                    val msg = ControlCodec.readPrefixed(input) ?: break
                    lastHeardTicks = clockNowTicks()
                    handleHostMessage(msg)
                }
            } catch (_: Exception) {
            } finally {
                handleConnectionLoss()
            }
        }
    }

    private fun handleHostMessage(msg: ControlMessage) {
        when (msg) {
            is SetDeviceSettings -> {
                if (msg.isValid()) {
                    volume = msg.Volume
                    offsetMs = msg.OffsetMs
                    audioPlayer?.volume = volume
                    scheduler?.setOffsetMs(offsetMs)

                    if (msg.EqGainsDb != null) {
                        eqGainsDb = msg.EqGainsDb
                        eqProcessor?.setGains(msg.EqGainsDb.toFloatArray())
                    }
                    onSettingsChanged?.invoke()
                }
            }
            is SetLatency -> {
                if (msg.isValid()) {
                    scheduler?.targetLatencyMs = msg.TargetLatencyMs
                }
            }
            is Heartbeat -> {
                // Heartbeat from host resets silence timer
            }
            is Bye -> {
                leave()
            }
            else -> {
                // Ignore other messages
            }
        }
    }

    fun tick() {
        if (isDisposed.get()) return
        val now = clockNowTicks()

        // 1. Check silence limit if connected
        val sock = tcpSocket
        if (sock != null && sock.isConnected && !sock.isClosed) {
            if (now - lastHeardTicks > SILENCE_LIMIT_TICKS) {
                handleConnectionLoss()
                return
            }

            // 2. Tick clock synchronization
            clockClient?.tick()

            // 3. Update metrics
            clockEstimator?.let { est ->
                clockOffsetMs = est.clientToHost(now).let { h -> (h - now) / 10000.0 }
                jitterResidualMs = est.fitResidualMs
            }
            jitterBuffer?.let { jb ->
                val count = jb.getPresentFrameCount()
                jitterBufferHealth = if (count > 0) (count.toDouble() / 256.0).coerceIn(0.0, 1.0) else 0.0
            }

            // 4. Evaluate convergence and start playback
            val est = clockEstimator
            val player = audioPlayer
            val sched = scheduler
            if (est != null && est.isConverged && player != null && !player.isPlaying) {
                player.start()
                sendControl(Ready())
                setState(SyncState.InSync)
            }

            // 5. Update sync state
            if (player != null && player.isPlaying && est != null) {
                val nextState = when {
                    est.state == SyncState.InSync -> SyncState.InSync
                    else -> SyncState.Degraded
                }
                if (stateRef.get() != nextState) {
                    setState(nextState)
                }
            }

            // 6. Send periodic heartbeat
            if (now - lastHeartbeatTicks >= HEARTBEAT_INTERVAL_TICKS) {
                lastHeartbeatTicks = now
                val stats = HeartbeatStats(
                    LossPct = 0.0,
                    LatePct = (jitterBuffer?.lateCount?.toDouble() ?: 0.0),
                    DriftPpm = est?.driftPpm ?: 0.0,
                    FitResidualMs = est?.fitResidualMs ?: 0.0,
                    SyncErrorMs = sched?.lastSyncError ?: 0.0,
                    State = stateRef.get().name
                )
                sendControl(Heartbeat(stats))
            }
        } else if (isReconnecting && rememberedHost != null && rememberedPin != null) {
            // Reconnect loop
            if (now - disconnectTicks > RECONNECT_GIVE_UP_TICKS) {
                isReconnecting = false
                setState(SyncState.Disconnected)
            } else if (now - lastHeardTicks > RECONNECT_RETRY_TICKS) {
                lastHeardTicks = now
                thread(start = true, isDaemon = true, name = "client-reconnect") {
                    performConnect(rememberedHost!!, rememberedPin!!)
                }
            }
        }
    }

    private fun sendControl(msg: ControlMessage) {
        val sock = tcpSocket ?: return
        if (sock.isClosed) return
        try {
            synchronized(sock) {
                ControlCodec.writePrefixed(msg, sock.getOutputStream())
            }
        } catch (_: Exception) {
            handleConnectionLoss()
        }
    }

    private fun handleConnectionLoss() {
        cleanupActiveSession()

        if (rememberedHost != null && rememberedPin != null) {
            isReconnecting = true
            disconnectTicks = clockNowTicks()
            setState(SyncState.Connecting)
        } else {
            setState(SyncState.Disconnected)
        }
    }

    private fun cleanupActiveSession() {
        try { audioPlayer?.stop() } catch (_: Exception) {}
        try { tcpSocket?.close() } catch (_: Exception) {}
        try { audioReceiver?.close() } catch (_: Exception) {}
        try { audioChannel?.close() } catch (_: Exception) {}
        try { clockClient?.close() } catch (_: Exception) {}
        try { clockChannel?.close() } catch (_: Exception) {}

        audioPlayer = null
        tcpSocket = null
        audioReceiver = null
        audioChannel = null
        clockClient = null
        clockChannel = null
        scheduler = null
        jitterBuffer = null
        clockEstimator = null
        eqProcessor = null
    }

    fun leave() {
        rememberedHost = null
        rememberedPin = null
        isReconnecting = false
        sendControl(Bye())
        cleanupActiveSession()
        setState(SyncState.Disconnected)
    }

    override fun close() {
        if (isDisposed.getAndSet(true)) return
        leave()
    }
}
