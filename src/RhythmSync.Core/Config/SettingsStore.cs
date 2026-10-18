using System.Text.Json;

namespace RhythmSync.Core.Config;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as <c>settings.json</c> in one directory (the app passes
/// <c>%AppData%\RhythmSync</c>), which is created when missing. Saves are atomic: the file is written to
/// <c>settings.json.tmp</c> and moved over the old one.
/// </summary>
public sealed class SettingsStore
{
    public const int DefaultTargetLatencyMs = 200;
    private const int MinLatencyMs = 100, MaxLatencyMs = 500;
    private const double MaxOffsetMs = 100;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _path;

    public SettingsStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        _path = Path.Combine(directory, "settings.json");
    }

    /// <summary>
    /// Reads the settings. A missing file gives defaults (new device id, <see cref="Environment.MachineName"/>, 200 ms),
    /// which are saved at once so the device id stays stable even if the caller never saves. A corrupt file is renamed to
    /// <c>settings.json.bak</c> and defaults are returned (and saved). Members that are missing or out of range are
    /// replaced by defaults or clamped.
    /// </summary>
    public AppSettings Load()
    {
        Directory.CreateDirectory(_directory);

        AppSettings? loaded = null;
        bool exists = File.Exists(_path);
        if (exists)
        {
            try
            {
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(_path), Options);
            }
            catch (JsonException)
            {
                File.Move(_path, _path + ".bak", overwrite: true);
                exists = false;
            }
        }

        var settings = Normalize(loaded);
        if (!exists) TrySave(settings);
        return settings;
    }

    public void Save(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        Directory.CreateDirectory(_directory);
        string tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(s, Options));
        File.Move(tmp, _path, overwrite: true);
    }

    private void TrySave(AppSettings s)
    {
        // Loading must still work from a read-only or locked location; the defaults are just not persisted then.
        try { Save(s); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static AppSettings Normalize(AppSettings? s)
    {
        var devices = new Dictionary<Guid, DeviceSettings>();
        if (s?.Devices is { } stored)
        {
            foreach (var (id, d) in stored)
            {
                if (d is null || !float.IsFinite(d.Volume) || !double.IsFinite(d.OffsetMs)) continue;
                devices[id] = new DeviceSettings(Math.Clamp(d.Volume, 0f, 1f), Math.Clamp(d.OffsetMs, -MaxOffsetMs, MaxOffsetMs));
            }
        }

        int latency = s?.TargetLatencyMs ?? 0;
        return new AppSettings(
            DeviceId: s is null || s.DeviceId == Guid.Empty ? Guid.NewGuid() : s.DeviceId,
            DeviceName: string.IsNullOrWhiteSpace(s?.DeviceName) ? Environment.MachineName : s.DeviceName.Trim(),
            LastHostName: s?.LastHostName,
            LastPairingCode: s?.LastPairingCode,
            TargetLatencyMs: latency == 0 ? DefaultTargetLatencyMs : Math.Clamp(latency, MinLatencyMs, MaxLatencyMs),
            LocalSpeakerDeviceId: s?.LocalSpeakerDeviceId,
            FirewallConfigured: s?.FirewallConfigured ?? false,
            Devices: devices,
            StartMinimized: s?.StartMinimized ?? false);
    }
}
