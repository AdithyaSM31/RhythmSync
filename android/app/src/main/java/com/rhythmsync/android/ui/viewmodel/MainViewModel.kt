package com.rhythmsync.android.ui.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.rhythmsync.android.audio.AudioCapture
import com.rhythmsync.android.dsp.EqBands
import com.rhythmsync.android.dsp.EqPresets
import com.rhythmsync.android.network.*
import com.rhythmsync.android.sync.SyncState
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.InetSocketAddress

data class ClientMetricsState(
    val clockOffsetMs: Double = 0.0,
    val jitterResidualMs: Double = 0.0,
    val bufferHealth: Double = 0.0
)

class MainViewModel : ViewModel() {

    // --- Client State ---
    private val _clientState = MutableStateFlow(SyncState.Disconnected)
    val clientState: StateFlow<SyncState> = _clientState.asStateFlow()

    private val _discoveredHosts = MutableStateFlow<List<DiscoveredHost>>(emptyList())
    val discoveredHosts: StateFlow<List<DiscoveredHost>> = _discoveredHosts.asStateFlow()

    private val _metrics = MutableStateFlow(ClientMetricsState())
    val metrics: StateFlow<ClientMetricsState> = _metrics.asStateFlow()

    private val _clientVolume = MutableStateFlow(1.0f)
    val clientVolume: StateFlow<Float> = _clientVolume.asStateFlow()

    private val _clientEqGains = MutableStateFlow(List(10) { 0f })
    val clientEqGains: StateFlow<List<Float>> = _clientEqGains.asStateFlow()

    private val _connectedHost = MutableStateFlow<DiscoveredHost?>(null)
    val connectedHost: StateFlow<DiscoveredHost?> = _connectedHost.asStateFlow()

    // --- Host State ---
    private val _isHosting = MutableStateFlow(false)
    val isHosting: StateFlow<Boolean> = _isHosting.asStateFlow()

    private val _hostPairingCode = MutableStateFlow("----")
    val hostPairingCode: StateFlow<String> = _hostPairingCode.asStateFlow()

    private val _hostConnectedClients = MutableStateFlow<List<HostClientInfo>>(emptyList())
    val hostConnectedClients: StateFlow<List<HostClientInfo>> = _hostConnectedClients.asStateFlow()

    // --- Settings State ---
    val deviceName = MutableStateFlow("Android Speaker")
    val targetLatencyMs = MutableStateFlow(200)

    private val _logs = MutableStateFlow<List<String>>(emptyList())
    val logs: StateFlow<List<String>> = _logs.asStateFlow()

    // Internal engines
    private var discoveryListener: DiscoveryListener? = null
    private var discoveryChannel: IDatagramChannel? = null
    private var clientSession: ClientSession? = null

    private var hostSession: HostSession? = null
    private var hostAudioChannel: IDatagramChannel? = null
    private var hostStreamer: AudioStreamer? = null
    private var audioCapture: AudioCapture? = null

    init {
        startDiscovery()
        startPeriodicTicker()
    }

    private fun addLog(message: String) {
        val next = _logs.value.takeLast(99).toMutableList()
        next.add("[${System.currentTimeMillis() % 100000}] $message")
        _logs.value = next
    }

    private fun startDiscovery() {
        try {
            val chan = UdpDatagramChannel(InetSocketAddress(47800), broadcast = true)
            discoveryChannel = chan
            val listener = DiscoveryListener(
                channel = chan,
                clockNowTicks = { System.nanoTime() / 100L }
            )
            listener.onChanged = {
                _discoveredHosts.value = listener.currentHosts
            }
            listener.start()
            discoveryListener = listener
            addLog("Discovery listener active on UDP 47800")
        } catch (e: Exception) {
            addLog("Discovery listener init error: ${e.message}")
        }
    }

    private fun startPeriodicTicker() {
        viewModelScope.launch(Dispatchers.IO) {
            while (isActive) {
                try {
                    discoveryListener?.tick()
                    _discoveredHosts.value = discoveryListener?.currentHosts ?: emptyList()

                    clientSession?.let { cs ->
                        cs.tick()
                        _clientState.value = cs.state
                        _clientVolume.value = cs.volume
                        _clientEqGains.value = cs.eqGainsDb
                        _metrics.value = ClientMetricsState(
                            clockOffsetMs = cs.clockOffsetMs,
                            jitterResidualMs = cs.jitterResidualMs,
                            bufferHealth = cs.jitterBufferHealth
                        )
                    }

                    hostSession?.let { hs ->
                        hs.tick()
                        _hostConnectedClients.value = hs.connectedClients
                    }
                } catch (_: Exception) {}
                delay(50)
            }
        }
    }

    // --- Client Actions ---

    fun joinHost(host: DiscoveredHost, pin: String) {
        viewModelScope.launch(Dispatchers.IO) {
            clientSession?.leave()
            addLog("Joining host ${host.name} (${host.controlEndPoint}) with PIN $pin")
            _connectedHost.value = host

            val cs = ClientSession(
                deviceName = deviceName.value,
                preferredAudioPort = 47802
            )
            cs.onStateChanged = { state ->
                _clientState.value = state
                addLog("Client state -> $state")
            }
            cs.onSettingsChanged = {
                _clientVolume.value = cs.volume
                _clientEqGains.value = cs.eqGainsDb
            }
            clientSession = cs
            cs.join(host.controlEndPoint, pin)
        }
    }

    fun leaveHost() {
        viewModelScope.launch(Dispatchers.IO) {
            clientSession?.leave()
            clientSession = null
            _connectedHost.value = null
            _clientState.value = SyncState.Disconnected
            addLog("Left host session")
        }
    }

    // --- Host Actions ---

    fun startHosting() {
        if (_isHosting.value) return
        viewModelScope.launch(Dispatchers.IO) {
            try {
                addLog("Starting Host mode...")
                val audioChan = UdpDatagramChannel(InetSocketAddress(0))
                hostAudioChannel = audioChan

                val streamer = AudioStreamer(audioChan, sessionId = System.currentTimeMillis())
                hostStreamer = streamer

                val host = HostSession(
                    name = deviceName.value,
                    targetLatencyMs = targetLatencyMs.value,
                    audioStreamer = streamer
                )
                host.onClientListChanged = {
                    _hostConnectedClients.value = host.connectedClients
                }
                host.start()
                hostSession = host

                _hostPairingCode.value = host.pairingCode
                _isHosting.value = true

                // Start audio capture (with automatic fallback to continuous 440 Hz tone)
                val capture = AudioCapture(
                    sessionId = host.sessionId,
                    targetLatencyMs = targetLatencyMs.value
                ) { packet ->
                    streamer.onFrame(packet.sequence, packet.playAt, packet.pcmData)
                }
                capture.start()
                audioCapture = capture

                addLog("Hosting started. PIN: ${host.pairingCode}, Control: ${host.controlPort}")
            } catch (e: Exception) {
                addLog("Failed to start host: ${e.message}")
                stopHosting()
            }
        }
    }

    fun stopHosting() {
        viewModelScope.launch(Dispatchers.IO) {
            audioCapture?.stop()
            hostSession?.close()
            hostAudioChannel?.close()

            audioCapture = null
            hostSession = null
            hostAudioChannel = null
            hostStreamer = null

            _isHosting.value = false
            _hostPairingCode.value = "----"
            _hostConnectedClients.value = emptyList()
            addLog("Hosting stopped")
        }
    }

    fun updateClientVolume(deviceId: String, volume: Float) {
        hostSession?.let { hs ->
            val client = hs.connectedClients.find { it.deviceId == deviceId } ?: return@let
            hs.setDeviceSettings(deviceId, volume, client.offsetMs, client.eqGainsDb)
        }
    }

    fun updateClientOffset(deviceId: String, offsetMs: Double) {
        hostSession?.let { hs ->
            val client = hs.connectedClients.find { it.deviceId == deviceId } ?: return@let
            hs.setDeviceSettings(deviceId, client.volume, offsetMs, client.eqGainsDb)
        }
    }

    fun updateClientEq(deviceId: String, gainsDb: List<Float>) {
        hostSession?.let { hs ->
            val client = hs.connectedClients.find { it.deviceId == deviceId } ?: return@let
            hs.setDeviceSettings(deviceId, client.volume, client.offsetMs, gainsDb)
        }
    }

    fun applyEqPreset(deviceId: String, presetName: String) {
        val preset = when (presetName) {
            "Bass boost" -> EqPresets.BASS_BOOST
            "Treble cut" -> EqPresets.TREBLE_CUT
            "Laptop speakers" -> EqPresets.LAPTOP_SPEAKERS
            else -> EqPresets.FLAT
        }
        updateClientEq(deviceId, preset.toList())
    }

    override fun onCleared() {
        super.onCleared()
        clientSession?.close()
        discoveryListener?.close()
        discoveryChannel?.close()
        stopHosting()
    }
}
