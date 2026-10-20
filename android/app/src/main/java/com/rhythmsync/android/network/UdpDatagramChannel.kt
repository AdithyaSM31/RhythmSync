package com.rhythmsync.android.network

import java.io.Closeable
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.SocketException
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import kotlin.concurrent.thread

typealias DatagramHandler = (data: ByteArray, length: Int, from: InetSocketAddress) -> Unit

interface IDatagramChannel : Closeable {
    val localPort: Int
    val localAddress: InetSocketAddress
    val sendErrors: Long
    val receiveErrors: Long
    fun send(data: ByteArray, offset: Int, length: Int, target: InetSocketAddress)
    fun send(data: ByteArray, target: InetSocketAddress) {
        send(data, 0, data.size, target)
    }
    fun start(handler: DatagramHandler)
}

interface IDatagramChannelFactory {
    fun create(bindAddress: InetSocketAddress, broadcast: Boolean = false): IDatagramChannel
}

class DefaultDatagramChannelFactory : IDatagramChannelFactory {
    override fun create(bindAddress: InetSocketAddress, broadcast: Boolean): IDatagramChannel =
        UdpDatagramChannel(bindAddress, broadcast)
}

class UdpDatagramChannel(
    bindAddress: InetSocketAddress = InetSocketAddress(0),
    broadcast: Boolean = false
) : IDatagramChannel {

    private val socket = DatagramSocket(null).apply {
        reuseAddress = true
        if (broadcast) {
            this.broadcast = true
        }
        bind(bindAddress)
    }

    override val localPort: Int get() = socket.localPort

    override val localAddress: InetSocketAddress
        get() = InetSocketAddress(socket.localAddress, socket.localPort)

    private val _sendErrors = AtomicLong(0)
    override val sendErrors: Long get() = _sendErrors.get()

    private val _receiveErrors = AtomicLong(0)
    override val receiveErrors: Long get() = _receiveErrors.get()

    private val isClosed = AtomicBoolean(false)
    private val isStarted = AtomicBoolean(false)
    private var rxThread: Thread? = null

    override fun send(data: ByteArray, offset: Int, length: Int, target: InetSocketAddress) {
        if (isClosed.get()) {
            _sendErrors.incrementAndGet()
            return
        }
        try {
            val packet = DatagramPacket(data, offset, length, target)
            socket.send(packet)
        } catch (_: Exception) {
            _sendErrors.incrementAndGet()
        }
    }

    override fun start(handler: DatagramHandler) {
        check(!isStarted.getAndSet(true)) { "Channel already started." }
        check(!isClosed.get()) { "Channel already closed." }

        rxThread = thread(start = true, isDaemon = true, name = "udp-rx-$localPort") {
            val rxBuffer = ByteArray(2048)
            val packet = DatagramPacket(rxBuffer, rxBuffer.size)
            while (!isClosed.get()) {
                try {
                    packet.setData(rxBuffer, 0, rxBuffer.size)
                    socket.receive(packet)
                    val sender = InetSocketAddress(packet.address, packet.port)
                    handler(packet.data, packet.length, sender)
                } catch (e: SocketException) {
                    if (isClosed.get()) break
                    _receiveErrors.incrementAndGet()
                    try {
                        Thread.sleep(1)
                    } catch (_: InterruptedException) {
                        break
                    }
                } catch (_: Exception) {
                    if (isClosed.get()) break
                    _receiveErrors.incrementAndGet()
                }
            }
        }
    }

    override fun close() {
        if (isClosed.getAndSet(true)) return
        try {
            socket.close()
        } catch (_: Exception) {}
        val currentThread = Thread.currentThread()
        val threadToJoin = rxThread
        if (threadToJoin != null && currentThread != threadToJoin) {
            try {
                threadToJoin.join(1000)
            } catch (_: InterruptedException) {}
        }
    }
}
