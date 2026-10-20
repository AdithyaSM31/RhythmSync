package com.rhythmsync.android.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Binder
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.rhythmsync.android.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

enum class ServiceMode {
    Idle,
    Host,
    Client
}

data class ServiceStatus(
    val mode: ServiceMode = ServiceMode.Idle,
    val isRunning: Boolean = false,
    val details: String = "Idle"
)

/**
 * Android Foreground Service managing audio playback and streaming lifecycles.
 *
 * Holds:
 * 1. [PowerManager.PARTIAL_WAKE_LOCK] ensuring the CPU stays active while screen is off (held for as long as the
 *    service runs; released by [ACTION_STOP] and in [onDestroy]).
 * 2. [WifiManager.WifiLock] ([WifiManager.WIFI_MODE_FULL_LOW_LATENCY]) reducing network jitter.
 * 3. [WifiManager.MulticastLock] allowing discovery beacon reception while device is locked.
 * 4. Ongoing media notification on channel 'rhythmsync_playback'.
 */
class RhythmSyncService : Service() {

    companion object {
        private const val TAG = "RhythmSyncService"
        const val CHANNEL_ID = "rhythmsync_playback"
        const val NOTIFICATION_ID = 47800

        const val ACTION_START_HOST = "com.rhythmsync.android.action.START_HOST"
        const val ACTION_START_CLIENT = "com.rhythmsync.android.action.START_CLIENT"
        const val ACTION_STOP = "com.rhythmsync.android.action.STOP"
        const val ACTION_UPDATE_STATUS = "com.rhythmsync.android.action.UPDATE_STATUS"

        /**
         * The foreground-service type to declare. Playback is always a media-playback service; host mode also
         * records audio, which on Android 14+ needs the microphone type, and that type may only be requested while
         * RECORD_AUDIO is granted (otherwise starting the service throws a SecurityException).
         */
        fun foregroundServiceType(mode: ServiceMode, hasRecordAudioPermission: Boolean): Int {
            var type = ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
            if (mode == ServiceMode.Host && hasRecordAudioPermission) {
                type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
            }
            return type
        }

        fun startHost(context: Context) {
            val intent = Intent(context, RhythmSyncService::class.java).apply {
                action = ACTION_START_HOST
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }

        fun startClient(context: Context) {
            val intent = Intent(context, RhythmSyncService::class.java).apply {
                action = ACTION_START_CLIENT
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }

        fun stop(context: Context) {
            val intent = Intent(context, RhythmSyncService::class.java).apply {
                action = ACTION_STOP
            }
            context.startService(intent)
        }
    }

    private val binder = LocalBinder()
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    private val _serviceStatus = MutableStateFlow(ServiceStatus())
    val serviceStatus: StateFlow<ServiceStatus> = _serviceStatus.asStateFlow()

    private object SafeLog {
        fun i(tag: String, msg: String) {
            try {
                Log.i(tag, msg)
            } catch (_: Throwable) {
                println("[$tag] $msg")
            }
        }

        fun d(tag: String, msg: String) {
            try {
                Log.d(tag, msg)
            } catch (_: Throwable) {
                println("[$tag] $msg")
            }
        }
    }

    inner class LocalBinder : Binder() {
        fun getService(): RhythmSyncService = this@RhythmSyncService
    }

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        initLocks()
        SafeLog.i(TAG, "RhythmSyncService created")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val action = intent?.action ?: return START_NOT_STICKY

        when (action) {
            ACTION_START_HOST -> {
                acquireLocks()
                _serviceStatus.value = ServiceStatus(ServiceMode.Host, true, "Host: Streaming")
                startForegroundWithNotification("Host Mode", "Streaming to clients...", ServiceMode.Host)
            }
            ACTION_START_CLIENT -> {
                acquireLocks()
                _serviceStatus.value = ServiceStatus(ServiceMode.Client, true, "Client: Synchronizing")
                startForegroundWithNotification("Client Mode", "Connected and in sync", ServiceMode.Client)
            }
            ACTION_STOP -> {
                stopForegroundAndSelf()
            }
            ACTION_UPDATE_STATUS -> {
                val details = intent.getStringExtra("details") ?: "Active"
                val currentMode = _serviceStatus.value.mode
                _serviceStatus.value = ServiceStatus(currentMode, true, details)
                updateNotification(
                    if (currentMode == ServiceMode.Host) "Host Mode" else "Client Mode",
                    details
                )
            }
        }

        return START_NOT_STICKY
    }

    private fun initLocks() {
        try {
            val powerManager = getSystemService(Context.POWER_SERVICE) as? PowerManager
            wakeLock = powerManager?.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "RhythmSync::WakeLock")?.apply {
                setReferenceCounted(false)
            }
        } catch (_: Throwable) {}

        try {
            val wifiManager = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            if (wifiManager != null) {
                val wifiMode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    WifiManager.WIFI_MODE_FULL_LOW_LATENCY
                } else {
                    @Suppress("DEPRECATION")
                    WifiManager.WIFI_MODE_FULL_HIGH_PERF
                }
                wifiLock = wifiManager.createWifiLock(wifiMode, "RhythmSync::WifiLock")?.apply {
                    setReferenceCounted(false)
                }

                multicastLock = wifiManager.createMulticastLock("RhythmSync::MulticastLock")?.apply {
                    setReferenceCounted(false)
                }
            }
        } catch (_: Throwable) {}
    }

    private fun acquireLocks() {
        try {
            // No timeout: a timed lock would drop after minutes and stop audio with the screen off.
            // It is released by releaseLocks() on stop and in onDestroy().
            wakeLock?.let { if (!it.isHeld) it.acquire() }
        } catch (_: Throwable) {}

        try {
            wifiLock?.let { if (!it.isHeld) it.acquire() }
        } catch (_: Throwable) {}

        try {
            multicastLock?.let { if (!it.isHeld) it.acquire() }
        } catch (_: Throwable) {}

        SafeLog.d(TAG, "Locks acquired (WakeLock, WifiLock, MulticastLock)")
    }

    private fun releaseLocks() {
        try {
            wakeLock?.let { if (it.isHeld) it.release() }
        } catch (_: Throwable) {}

        try {
            wifiLock?.let { if (it.isHeld) it.release() }
        } catch (_: Throwable) {}

        try {
            multicastLock?.let { if (it.isHeld) it.release() }
        } catch (_: Throwable) {}

        SafeLog.d(TAG, "Locks released")
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "RhythmSync Audio Service",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Synchronized low-latency multi-room audio playback"
                setShowBadge(false)
            }
            val manager = getSystemService(NotificationManager::class.java)
            manager?.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(title: String, content: String): Notification {
        val stopIntent = Intent(this, RhythmSyncService::class.java).apply {
            action = ACTION_STOP
        }
        val stopPendingIntent = PendingIntent.getService(
            this,
            0,
            stopIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(title)
            .setContentText(content)
            .setSmallIcon(R.drawable.ic_notification)
            .setOngoing(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .addAction(android.R.drawable.ic_media_pause, "Stop", stopPendingIntent)
            .build()
    }

    private fun startForegroundWithNotification(title: String, content: String, mode: ServiceMode) {
        val notification = buildNotification(title, content)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val hasMic = ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) ==
                PackageManager.PERMISSION_GRANTED
            ServiceCompat.startForeground(
                this,
                NOTIFICATION_ID,
                notification,
                foregroundServiceType(mode, hasMic)
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private fun updateNotification(title: String, content: String) {
        val notification = buildNotification(title, content)
        val manager = getSystemService(NotificationManager::class.java)
        manager?.notify(NOTIFICATION_ID, notification)
    }

    private fun stopForegroundAndSelf() {
        releaseLocks()
        _serviceStatus.value = ServiceStatus(ServiceMode.Idle, false, "Stopped")
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            stopForeground(STOP_FOREGROUND_REMOVE)
        } else {
            @Suppress("DEPRECATION")
            stopForeground(true)
        }
        stopSelf()
        SafeLog.i(TAG, "RhythmSyncService stopped and removed from foreground")
    }

    override fun onDestroy() {
        super.onDestroy()
        releaseLocks()
        serviceScope.cancel()
        SafeLog.i(TAG, "RhythmSyncService destroyed")
    }
}
