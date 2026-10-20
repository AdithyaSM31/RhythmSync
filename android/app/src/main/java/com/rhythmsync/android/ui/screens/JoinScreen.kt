package com.rhythmsync.android.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.rhythmsync.android.dsp.EqBands
import com.rhythmsync.android.network.DiscoveredHost
import com.rhythmsync.android.sync.SyncState
import com.rhythmsync.android.ui.components.StatusChip
import com.rhythmsync.android.ui.theme.*
import com.rhythmsync.android.ui.viewmodel.MainViewModel

@Composable
fun JoinScreen(
    viewModel: MainViewModel,
    modifier: Modifier = Modifier
) {
    val clientState by viewModel.clientState.collectAsState()
    val discoveredHosts by viewModel.discoveredHosts.collectAsState()
    val connectedHost by viewModel.connectedHost.collectAsState()
    val metrics by viewModel.metrics.collectAsState()
    val volume by viewModel.clientVolume.collectAsState()
    val eqGains by viewModel.clientEqGains.collectAsState()

    var hostToJoin by remember { mutableStateOf<DiscoveredHost?>(null) }
    var enteredPin by remember { mutableStateOf("") }

    if (hostToJoin != null) {
        AlertDialog(
            onDismissRequest = { hostToJoin = null },
            title = {
                Text(
                    text = "Join ${hostToJoin?.name}",
                    style = MaterialTheme.typography.titleLarge,
                    color = TextPrimary
                )
            },
            text = {
                Column {
                    Text(
                        text = "Enter 4-digit pairing code displayed on host:",
                        style = MaterialTheme.typography.bodyMedium,
                        color = TextSecondary
                    )
                    Spacer(modifier = Modifier.height(16.dp))
                    OutlinedTextField(
                        value = enteredPin,
                        onValueChange = { if (it.length <= 4 && it.all { c -> c.isDigit() }) enteredPin = it },
                        modifier = Modifier.fillMaxWidth(),
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        singleLine = true,
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedBorderColor = AccentTeal,
                            unfocusedBorderColor = StudioBorder,
                            focusedTextColor = TextPrimary,
                            unfocusedTextColor = TextPrimary
                        )
                    )
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        val host = hostToJoin
                        if (host != null && enteredPin.length == 4) {
                            viewModel.joinHost(host, enteredPin)
                            hostToJoin = null
                            enteredPin = ""
                        }
                    },
                    enabled = enteredPin.length == 4,
                    colors = ButtonDefaults.buttonColors(containerColor = AccentViolet)
                ) {
                    Text("Join")
                }
            },
            dismissButton = {
                TextButton(onClick = { hostToJoin = null }) {
                    Text("Cancel", color = TextSecondary)
                }
            },
            containerColor = StudioCard
        )
    }

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
                        text = "Synchronized Speaker",
                        style = MaterialTheme.typography.headlineLarge
                    )
                    Text(
                        text = "Client Mode",
                        style = MaterialTheme.typography.bodyMedium
                    )
                }
                StatusChip(state = clientState)
            }
        }

        if (clientState != SyncState.Disconnected && connectedHost != null) {
            item {
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
                                    text = connectedHost?.name ?: "Connected Host",
                                    style = MaterialTheme.typography.titleLarge,
                                    color = TextPrimary
                                )
                                Text(
                                    text = "${connectedHost?.controlEndPoint?.address?.hostAddress}:${connectedHost?.controlEndPoint?.port}",
                                    style = MaterialTheme.typography.labelSmall,
                                    fontFamily = FontFamily.Monospace
                                )
                            }
                            Button(
                                onClick = { viewModel.leaveHost() },
                                colors = ButtonDefaults.buttonColors(containerColor = StatusRed)
                            ) {
                                Text("Leave")
                            }
                        }

                        Spacer(modifier = Modifier.height(16.dp))
                        HorizontalDivider(color = StudioBorder)
                        Spacer(modifier = Modifier.height(16.dp))

                        Text(
                            text = "Real-Time Sync Metrics",
                            style = MaterialTheme.typography.titleMedium,
                            color = AccentTeal
                        )
                        Spacer(modifier = Modifier.height(8.dp))

                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween
                        ) {
                            MetricItem("Offset", String.format("%+.2f ms", metrics.clockOffsetMs))
                            MetricItem("Jitter", String.format("%.2f ms", metrics.jitterResidualMs))
                            MetricItem("Buffer", String.format("%.0f%%", metrics.bufferHealth * 100))
                        }

                        Spacer(modifier = Modifier.height(16.dp))
                        Text(
                            text = "Host Volume: ${(volume * 100).toInt()}%",
                            style = MaterialTheme.typography.bodyMedium,
                            color = TextSecondary
                        )

                        Spacer(modifier = Modifier.height(8.dp))
                        Text(
                            text = "Active Equalizer (Host-Managed)",
                            style = MaterialTheme.typography.bodyMedium,
                            color = TextSecondary
                        )
                        Spacer(modifier = Modifier.height(4.dp))
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(50.dp),
                            horizontalArrangement = Arrangement.SpaceEvenly,
                            verticalAlignment = Alignment.Bottom
                        ) {
                            for (i in 0 until 10) {
                                val g = eqGains.getOrElse(i) { 0f }
                                val normalizedHeight = ((g + 12f) / 24f).coerceIn(0.1f, 1.0f)
                                Column(
                                    horizontalAlignment = Alignment.CenterHorizontally,
                                    verticalArrangement = Arrangement.Bottom,
                                    modifier = Modifier.weight(1f)
                                ) {
                                    Box(
                                        modifier = Modifier
                                            .width(8.dp)
                                            .fillMaxHeight(normalizedHeight)
                                            .clip(RoundedCornerShape(2.dp))
                                            .background(if (g >= 0f) AccentViolet else AccentTeal)
                                    )
                                    Text(
                                        text = EqBands.LABELS[i].replace(" ", ""),
                                        fontSize = 8.sp,
                                        color = TextMuted
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }

        item {
            Text(
                text = "Available Hosts (${discoveredHosts.size})",
                style = MaterialTheme.typography.titleMedium,
                color = TextSecondary
            )
        }

        if (discoveredHosts.isEmpty()) {
            item {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 32.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = "Searching local network for hosts (UDP 47800)...",
                        style = MaterialTheme.typography.bodyMedium,
                        color = TextMuted
                    )
                }
            }
        } else {
            items(discoveredHosts) { host ->
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            if (clientState == SyncState.Disconnected) {
                                hostToJoin = host
                            }
                        },
                    shape = RoundedCornerShape(10.dp),
                    colors = CardDefaults.cardColors(containerColor = StudioCard)
                ) {
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(16.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Column {
                            Text(
                                text = host.name,
                                style = MaterialTheme.typography.titleMedium,
                                color = TextPrimary
                            )
                            Text(
                                text = "${host.controlEndPoint.address.hostAddress}:${host.controlEndPoint.port}",
                                style = MaterialTheme.typography.labelSmall,
                                fontFamily = FontFamily.Monospace
                            )
                        }
                        Button(
                            onClick = { hostToJoin = host },
                            enabled = clientState == SyncState.Disconnected,
                            colors = ButtonDefaults.buttonColors(containerColor = AccentViolet)
                        ) {
                            Text("Join")
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun MetricItem(label: String, value: String) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Text(text = label, style = MaterialTheme.typography.labelSmall, color = TextMuted)
        Text(
            text = value,
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.Bold,
            color = AccentTeal
        )
    }
}
