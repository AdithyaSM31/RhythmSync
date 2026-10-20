package com.rhythmsync.android.ui.screens

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.rhythmsync.android.dsp.EqBands
import com.rhythmsync.android.network.HostClientInfo
import com.rhythmsync.android.ui.components.AccentSlider
import com.rhythmsync.android.ui.components.EqBandSlider
import com.rhythmsync.android.ui.components.PinBox
import com.rhythmsync.android.ui.theme.*
import com.rhythmsync.android.ui.viewmodel.MainViewModel

@Composable
fun HostScreen(
    viewModel: MainViewModel,
    modifier: Modifier = Modifier
) {
    val isHosting by viewModel.isHosting.collectAsState()
    val pin by viewModel.hostPairingCode.collectAsState()
    val clients by viewModel.hostConnectedClients.collectAsState()

    LazyColumn(
        modifier = modifier
            .fillMaxSize()
            .background(StudioBackground)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        item {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column {
                    Text(
                        text = "Audio Streamer",
                        style = MaterialTheme.typography.headlineLarge
                    )
                    Text(
                        text = "Host Mode",
                        style = MaterialTheme.typography.bodyMedium
                    )
                }
                Button(
                    onClick = {
                        if (isHosting) viewModel.stopHosting() else viewModel.startHosting()
                    },
                    colors = ButtonDefaults.buttonColors(
                        containerColor = if (isHosting) StatusRed else AccentViolet
                    )
                ) {
                    Text(if (isHosting) "Stop Hosting" else "Start Hosting")
                }
            }
        }

        if (isHosting) {
            item {
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(12.dp),
                    colors = CardDefaults.cardColors(containerColor = StudioCard)
                ) {
                    Column(
                        modifier = Modifier.padding(16.dp),
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        Text(
                            text = "Pairing Code",
                            style = MaterialTheme.typography.titleMedium,
                            color = TextSecondary
                        )
                        Spacer(modifier = Modifier.height(10.dp))
                        PinBox(pin = pin)
                        Spacer(modifier = Modifier.height(10.dp))
                        Text(
                            text = "Broadcasting discovery beacons on UDP 47800",
                            style = MaterialTheme.typography.labelSmall,
                            color = AccentTeal
                        )
                    }
                }
            }

            item {
                Text(
                    text = "Connected Speakers (${clients.size})",
                    style = MaterialTheme.typography.titleMedium,
                    color = TextSecondary
                )
            }

            if (clients.isEmpty()) {
                item {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(vertical = 24.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            text = "No speakers connected yet. Waiting for clients...",
                            style = MaterialTheme.typography.bodyMedium,
                            color = TextMuted
                        )
                    }
                }
            } else {
                items(clients) { client ->
                    ClientControlCard(
                        client = client,
                        onVolumeChange = { vol -> viewModel.updateClientVolume(client.deviceId, vol) },
                        onOffsetChange = { offset -> viewModel.updateClientOffset(client.deviceId, offset) },
                        onEqChange = { gains -> viewModel.updateClientEq(client.deviceId, gains) },
                        onPresetSelect = { preset -> viewModel.applyEqPreset(client.deviceId, preset) }
                    )
                }
            }
        } else {
            item {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 48.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = "Tap 'Start Hosting' to begin streaming audio to other devices",
                        style = MaterialTheme.typography.bodyMedium,
                        color = TextMuted
                    )
                }
            }
        }
    }
}

@Composable
private fun ClientControlCard(
    client: HostClientInfo,
    onVolumeChange: (Float) -> Unit,
    onOffsetChange: (Double) -> Unit,
    onEqChange: (List<Float>) -> Unit,
    onPresetSelect: (String) -> Unit
) {
    var expandedEq by remember { mutableStateOf(false) }

    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.cardColors(containerColor = StudioCard)
    ) {
        Column(modifier = Modifier.padding(16.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column {
                    Text(
                        text = client.name,
                        style = MaterialTheme.typography.titleMedium,
                        color = TextPrimary
                    )
                    Text(
                        text = "${client.address.address.hostAddress}:${client.audioPort}",
                        style = MaterialTheme.typography.labelSmall,
                        fontFamily = FontFamily.Monospace
                    )
                }
                Box(
                    modifier = Modifier
                        .clip(RoundedCornerShape(8.dp))
                        .background(if (client.isReady) StatusGreen.copy(alpha = 0.2f) else StatusYellow.copy(alpha = 0.2f))
                        .padding(horizontal = 8.dp, vertical = 4.dp)
                ) {
                    Text(
                        text = if (client.isReady) "Streaming" else "Syncing",
                        color = if (client.isReady) StatusGreen else StatusYellow,
                        fontSize = 11.sp
                    )
                }
            }

            Spacer(modifier = Modifier.height(12.dp))

            // Volume Slider
            AccentSlider(
                label = "Volume",
                value = client.volume,
                onValueChange = onVolumeChange,
                valueRange = 0f..1f,
                valueText = "${(client.volume * 100).toInt()}%"
            )

            // Timing Offset Slider (+/- 100 ms)
            AccentSlider(
                label = "Timing Offset",
                value = client.offsetMs.toFloat(),
                onValueChange = { onOffsetChange(it.toDouble()) },
                valueRange = -100f..100f,
                valueText = String.format("%+.1f ms", client.offsetMs)
            )

            // Expandable 10-Band EQ
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 8.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "10-Band Equalizer",
                    style = MaterialTheme.typography.titleSmall,
                    color = AccentTeal
                )
                IconButton(onClick = { expandedEq = !expandedEq }) {
                    Icon(
                        imageVector = if (expandedEq) Icons.Default.ExpandLess else Icons.Default.ExpandMore,
                        contentDescription = "Expand EQ",
                        tint = AccentTeal
                    )
                }
            }

            AnimatedVisibility(visible = expandedEq) {
                Column(modifier = Modifier.fillMaxWidth()) {
                    // Presets Row
                    LazyRow(
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        modifier = Modifier.padding(vertical = 8.dp)
                    ) {
                        val presets = listOf("Flat", "Bass boost", "Treble cut", "Laptop speakers")
                        items(presets) { preset ->
                            SuggestionChip(
                                onClick = { onPresetSelect(preset) },
                                label = { Text(preset, fontSize = 11.sp) },
                                colors = SuggestionChipDefaults.suggestionChipColors(
                                    containerColor = StudioSurface,
                                    labelColor = TextSecondary
                                )
                            )
                        }
                    }

                    // 10 Sliders
                    for (i in 0 until 10) {
                        val gain = client.eqGainsDb.getOrElse(i) { 0f }
                        EqBandSlider(
                            freqLabel = EqBands.LABELS[i],
                            gainDb = gain,
                            onGainChange = { newGain ->
                                val next = client.eqGainsDb.toMutableList()
                                next[i] = newGain
                                onEqChange(next)
                            }
                        )
                    }
                }
            }
        }
    }
}
