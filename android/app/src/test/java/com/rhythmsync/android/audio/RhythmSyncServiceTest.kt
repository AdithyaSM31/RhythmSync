package com.rhythmsync.android.audio

import com.rhythmsync.android.service.RhythmSyncService
import com.rhythmsync.android.service.ServiceMode
import com.rhythmsync.android.service.ServiceStatus
import org.junit.Assert.*
import org.junit.Test

class RhythmSyncServiceTest {

    @Test
    fun testServiceConstants() {
        assertEquals("rhythmsync_playback", RhythmSyncService.CHANNEL_ID)
        assertEquals(47800, RhythmSyncService.NOTIFICATION_ID)
        assertEquals("com.rhythmsync.android.action.START_HOST", RhythmSyncService.ACTION_START_HOST)
        assertEquals("com.rhythmsync.android.action.START_CLIENT", RhythmSyncService.ACTION_START_CLIENT)
        assertEquals("com.rhythmsync.android.action.STOP", RhythmSyncService.ACTION_STOP)
        assertEquals("com.rhythmsync.android.action.UPDATE_STATUS", RhythmSyncService.ACTION_UPDATE_STATUS)
    }

    @Test
    fun testServiceStatusDataModel() {
        val defaultStatus = ServiceStatus()
        assertEquals(ServiceMode.Idle, defaultStatus.mode)
        assertFalse(defaultStatus.isRunning)
        assertEquals("Idle", defaultStatus.details)

        val hostStatus = ServiceStatus(ServiceMode.Host, true, "Streaming to 3 clients")
        assertEquals(ServiceMode.Host, hostStatus.mode)
        assertTrue(hostStatus.isRunning)
        assertEquals("Streaming to 3 clients", hostStatus.details)

        val clientStatus = ServiceStatus(ServiceMode.Client, true, "In Sync (+0.12 ms)")
        assertEquals(ServiceMode.Client, clientStatus.mode)
        assertTrue(clientStatus.isRunning)
        assertEquals("In Sync (+0.12 ms)", clientStatus.details)
    }
}
