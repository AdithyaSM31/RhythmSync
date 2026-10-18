namespace RhythmSync.Core.Config;

/// <summary>Per-device playback settings the host keeps for a client (volume 0..1, offset ±100 ms).</summary>
public sealed record DeviceSettings(float Volume, double OffsetMs);

/// <summary>Persisted application settings (see <see cref="SettingsStore"/>).</summary>
/// <param name="DeviceId">This PC's stable identity in sessions.</param>
/// <param name="TargetLatencyMs">Host target latency, 100–500 ms.</param>
/// <param name="LocalSpeakerDeviceId">Output device the host plays on itself; null for the system default.</param>
/// <param name="Devices">Host side: remembered settings per client device id.</param>
/// <param name="StartMinimized">Start without showing the window (the tray icon still appears). Old settings files lack it and load as false.</param>
public sealed record AppSettings(Guid DeviceId, string DeviceName, string? LastHostName, string? LastPairingCode,
    int TargetLatencyMs, string? LocalSpeakerDeviceId, bool FirewallConfigured, Dictionary<Guid, DeviceSettings> Devices,
    bool StartMinimized = false);
