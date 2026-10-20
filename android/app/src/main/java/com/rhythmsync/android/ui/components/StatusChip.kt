package com.rhythmsync.android.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.rhythmsync.android.sync.SyncState
import com.rhythmsync.android.ui.theme.*

@Composable
fun StatusChip(
    state: SyncState,
    modifier: Modifier = Modifier
) {
    val (bgColor, dotColor, text) = when (state) {
        SyncState.InSync -> Triple(StatusGreen.copy(alpha = 0.15f), StatusGreen, "In Sync")
        SyncState.Syncing -> Triple(StatusYellow.copy(alpha = 0.15f), StatusYellow, "Syncing")
        SyncState.Connecting -> Triple(StatusBlue.copy(alpha = 0.15f), StatusBlue, "Connecting")
        SyncState.Degraded -> Triple(StatusYellow.copy(alpha = 0.2f), StatusYellow, "Degraded")
        SyncState.Disconnected -> Triple(StudioBorder, TextMuted, "Disconnected")
    }

    Row(
        modifier = modifier
            .clip(RoundedCornerShape(12.dp))
            .background(bgColor)
            .padding(horizontal = 10.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            modifier = Modifier
                .size(7.dp)
                .clip(CircleShape)
                .background(dotColor)
        )
        Spacer(modifier = Modifier.width(6.dp))
        Text(
            text = text,
            color = dotColor,
            fontSize = 12.sp,
            style = MaterialTheme.typography.labelSmall
        )
    }
}
