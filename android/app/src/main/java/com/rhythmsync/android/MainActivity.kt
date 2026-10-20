package com.rhythmsync.android

import android.content.Intent
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.viewModels
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CellTower
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Speaker
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.rhythmsync.android.service.RhythmSyncService
import com.rhythmsync.android.ui.screens.HostScreen
import com.rhythmsync.android.ui.screens.JoinScreen
import com.rhythmsync.android.ui.screens.SettingsScreen
import com.rhythmsync.android.ui.theme.*
import com.rhythmsync.android.ui.viewmodel.MainViewModel

sealed class Screen(val route: String, val title: String, val icon: ImageVector) {
    object Join : Screen("join", "Join", Icons.Default.Speaker)
    object Host : Screen("host", "Host", Icons.Default.CellTower)
    object Settings : Screen("settings", "Settings", Icons.Default.Settings)
}

class MainActivity : ComponentActivity() {

    private val viewModel: MainViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Start Foreground Service to keep network locks and audio alive
        startRhythmSyncService()

        setContent {
            RhythmSyncTheme {
                val navController = rememberNavController()
                val navBackStackEntry by navController.currentBackStackEntryAsState()
                val currentRoute = navBackStackEntry?.destination?.route

                Scaffold(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(StudioBackground),
                    containerColor = StudioBackground,
                    bottomBar = {
                        NavigationBar(
                            containerColor = StudioSurface,
                            contentColor = TextPrimary
                        ) {
                            val items = listOf(Screen.Join, Screen.Host, Screen.Settings)
                            items.forEach { screen ->
                                NavigationBarItem(
                                    icon = { Icon(screen.icon, contentDescription = screen.title) },
                                    label = { Text(screen.title) },
                                    selected = currentRoute == screen.route,
                                    onClick = {
                                        if (currentRoute != screen.route) {
                                            navController.navigate(screen.route) {
                                                popUpTo(navController.graph.startDestinationId) {
                                                    saveState = true
                                                }
                                                launchSingleTop = true
                                                restoreState = true
                                            }
                                        }
                                    },
                                    colors = NavigationBarItemDefaults.colors(
                                        selectedIconColor = StudioBackground,
                                        selectedTextColor = AccentTeal,
                                        indicatorColor = AccentTeal,
                                        unselectedIconColor = TextSecondary,
                                        unselectedTextColor = TextSecondary
                                    )
                                )
                            }
                        }
                    }
                ) { innerPadding ->
                    NavHost(
                        navController = navController,
                        startDestination = Screen.Join.route,
                        modifier = Modifier.padding(innerPadding)
                    ) {
                        composable(Screen.Join.route) {
                            JoinScreen(viewModel = viewModel)
                        }
                        composable(Screen.Host.route) {
                            HostScreen(viewModel = viewModel)
                        }
                        composable(Screen.Settings.route) {
                            SettingsScreen(viewModel = viewModel)
                        }
                    }
                }
            }
        }
    }

    private fun startRhythmSyncService() {
        RhythmSyncService.startClient(this)
    }

    override fun onDestroy() {
        super.onDestroy()
        RhythmSyncService.stop(this)
    }
}
