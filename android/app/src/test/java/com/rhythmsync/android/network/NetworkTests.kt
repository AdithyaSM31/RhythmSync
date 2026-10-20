package com.rhythmsync.android.network

import com.rhythmsync.android.protocol.*
import com.rhythmsync.android.sync.ClockEstimator
import com.rhythmsync.android.sync.JitterBuffer
import org.junit.Assert.*
import org.junit.Test
import java.net.InetSocketAddress

class NetworkTests {

    @Test
    fun testStatsWindowCalculations() {
        val window = StatsWindow(windowTicks = 100_000_000L) // 10s

        // T = 0s: initial sample
        window.add(nowTicks = 0L, received = 0L, late = 0L, concealed = 0L)
        assertEquals(0.0, window.latePct, 0.001)
        assertEquals(0.0, window.lossPct, 0.001)

        // T = 5s: received 100, late 5, concealed 10
        window.add(nowTicks = 50_000_000L, received = 100L, late = 5L, concealed = 10L)
        // Late% = 100 * 5 / 100 = 5.0%
        assertEquals(5.0, window.latePct, 0.001)
        // Loss% = 100 * 10 / (100 + 10) = 9.09%
        assertEquals(100.0 * 10.0 / 110.0, window.lossPct, 0.001)

        // T = 15s: old T=0s sample pruned
        window.add(nowTicks = 150_000_000L, received = 200L, late = 10L, concealed = 20L)
        // Window from T=5s to T=15s: dRec = 100, dLate = 5, dConc = 10
        assertEquals(5.0, window.latePct, 0.001)
        assertEquals(100.0 * 10.0 / 110.0, window.lossPct, 0.001)
    }

    @Test
    fun testDiscoveryBeaconExchangeAndExpiry() {
        var currentTicks = 10_000_000L
        val txChannel = UdpDatagramChannel(InetSocketAddress(0))
        val rxChannel = UdpDatagramChannel(InetSocketAddress(0))

        val listener = DiscoveryListener(
            channel = rxChannel,
            clockNowTicks = { currentTicks },
            expiryTicks = 30_000_000L // 3.0s
        )
        listener.start()

        val broadcaster = DiscoveryBroadcaster(
            channel = txChannel,
            targetBroadcast = InetSocketAddress("127.0.0.1", rxChannel.localPort),
            clockNowTicks = { currentTicks },
            intervalTicks = 5_000_000L,
            beaconSupplier = {
                BeaconPacket(
                    sessionId = 12345L,
                    controlPort = 47801,
                    protocolVersion = 2,
                    name = "TestHost"
                )
            }
        )

        // Tick broadcaster to send beacon
        broadcaster.tick()
        Thread.sleep(100)
        listener.tick()

        val hosts = listener.currentHosts
        assertEquals(1, hosts.size)
        assertEquals("TestHost", hosts[0].name)
        assertEquals(12345L, hosts[0].sessionId)
        assertEquals(47801, hosts[0].controlEndPoint.port)

        // Advance clock past 3.0s expiry (e.g. +4.0s)
        currentTicks += 40_000_000L
        listener.tick()
        assertEquals(0, listener.currentHosts.size)

        broadcaster.close()
        listener.close()
    }

    @Test
    fun testClockSyncPingPongExchange() {
        var clockTicks = 50_000_000L
        val serverChannel = UdpDatagramChannel(InetSocketAddress(0))
        val clientChannel = UdpDatagramChannel(InetSocketAddress(0))

        val server = ClockSyncServer(serverChannel, sessionId = 999L) { clockTicks }
        server.start()

        val estimator = ClockEstimator()
        val client = ClockSyncClient(
            channel = clientChannel,
            serverAddress = InetSocketAddress("127.0.0.1", serverChannel.localPort),
            sessionId = 999L,
            estimator = estimator,
            clockNowTicks = { clockTicks }
        )
        client.start()

        // Send 25 pings
        for (i in 1..25) {
            client.tick()
            Thread.sleep(10)
            clockTicks += 500_000L // 50ms advance
        }

        assertTrue("Client must have sent pings", client.totalPingsSent >= 20)
        assertTrue("Estimator must have received samples", estimator.sampleCount > 0)

        client.close()
        server.close()
    }

    @Test
    fun testAudioStreamerAndReceiverJitterBuffer() {
        val streamerChannel = UdpDatagramChannel(InetSocketAddress(0))
        val receiverChannel = UdpDatagramChannel(InetSocketAddress(0))

        val streamer = AudioStreamer(streamerChannel, sessionId = 777L)
        val jitterBuffer = JitterBuffer(capacity = 256)

        val receiver = AudioReceiver(
            channel = receiverChannel,
            expectedHostAddress = InetSocketAddress("127.0.0.1", 0).address,
            sessionId = 777L,
            jitterBuffer = jitterBuffer
        )
        receiver.start()

        val receiverEndpoint = InetSocketAddress("127.0.0.1", receiverChannel.localPort)
        streamer.addClient("test-client-1", receiverEndpoint)

        // Transmit 5 audio frames
        val testPcm = ShortArray(480) { (it * 10).toShort() }
        for (seq in 0L..4L) {
            streamer.onFrame(seq = seq, playAt = 100_000_000L + seq * 50_000L, pcm = testPcm)
            Thread.sleep(10)
        }

        Thread.sleep(50)
        assertTrue("Jitter buffer must have received frames", jitterBuffer.getPresentFrameCount() > 0)
        assertEquals(0L, jitterBuffer.getOldestPresentSeq())

        receiver.close()
        streamerChannel.close()
    }

    @Test
    fun testHostSessionAndClientSessionHandshakeAndSettingsSync() {
        val hostStreamerChannel = UdpDatagramChannel(InetSocketAddress(0))
        val streamer = AudioStreamer(hostStreamerChannel, sessionId = 888L)

        val host = HostSession(
            name = "Test Host",
            sessionId = 888L,
            pairingCode = "1234",
            controlPort = 0, // OS assigned ephemeral port
            clockPort = 47803,
            audioPort = 47802,
            audioStreamer = streamer
        )
        host.start()
        val hostPort = host.connectedClients.let {
            // Find bound port
            val ssField = HostSession::class.java.getDeclaredField("serverSocket")
            ssField.isAccessible = true
            val ss = ssField.get(host) as java.net.ServerSocket
            ss.localPort
        }

        val client = ClientSession(
            deviceId = "test-client-guid-1",
            deviceName = "Android Speaker",
            preferredAudioPort = 0 // ephemeral
        )

        // 1. Test bad pairing code -> Reject(BadCode)
        client.join(InetSocketAddress("127.0.0.1", hostPort), "9999")
        Thread.sleep(200)
        assertEquals(0, host.activeClientCount)

        // 2. Test correct pairing code -> Welcome
        client.join(InetSocketAddress("127.0.0.1", hostPort), "1234")
        Thread.sleep(300)

        // Host should now have 1 connected client
        assertEquals(1, host.activeClientCount)
        val joinedClient = host.connectedClients[0]
        assertEquals("test-client-guid-1", joinedClient.deviceId)
        assertEquals("Android Speaker", joinedClient.name)

        // 3. Test dynamic parameter sync: set volume, offset, and EQ
        val newEq = List(10) { 3.5f }
        host.setDeviceSettings("test-client-guid-1", volume = 0.75f, offsetMs = 12.5, eqGainsDb = newEq)
        Thread.sleep(200)

        assertEquals(0.75f, client.volume, 0.01f)
        assertEquals(12.5, client.offsetMs, 0.01)
        assertEquals(3.5f, client.eqGainsDb[0], 0.01f)

        // 4. Test client leave / disconnect
        client.leave()
        Thread.sleep(200)
        assertEquals(0, host.activeClientCount)

        client.close()
        host.close()
        hostStreamerChannel.close()
    }
}
