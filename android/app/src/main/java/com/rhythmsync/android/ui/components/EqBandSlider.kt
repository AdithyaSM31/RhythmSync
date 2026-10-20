package com.rhythmsync.android.ui.components

import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.rhythmsync.android.ui.theme.*

@Composable
fun EqBandSlider(
    freqLabel: String,
    gainDb: Float,
    onGainChange: (Float) -> Unit,
    modifier: Modifier = Modifier
) {
    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            text = freqLabel,
            modifier = Modifier.width(68.dp),
            style = MaterialTheme.typography.bodyMedium,
            color = TextSecondary,
            fontSize = 12.sp
        )
        Slider(
            value = gainDb,
            onValueChange = onGainChange,
            valueRange = -12f..12f,
            modifier = Modifier.weight(1f),
            colors = SliderDefaults.colors(
                thumbColor = if (gainDb >= 0f) AccentViolet else AccentTeal,
                activeTrackColor = if (gainDb >= 0f) AccentViolet else AccentTeal,
                inactiveTrackColor = StudioBorder
            )
        )
        Text(
            text = String.format("%+.1f dB", gainDb),
            modifier = Modifier.width(60.dp),
            style = MaterialTheme.typography.bodyMedium,
            color = if (gainDb == 0f) TextMuted else AccentTeal,
            fontSize = 12.sp
        )
    }
}
