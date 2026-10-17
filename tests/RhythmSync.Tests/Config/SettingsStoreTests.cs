using RhythmSync.Core.Config;

namespace RhythmSync.Tests.Config;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RhythmSyncTests", Guid.NewGuid().ToString("N"));

    /// <summary>A directory that does not exist yet (the store must create it).</summary>
    private string Dir => Path.Combine(_root, "RhythmSync");

    private string SettingsPath => Path.Combine(Dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void Missing_settings_give_defaults_in_a_created_directory()
    {
        var s = new SettingsStore(Dir).Load();

        Assert.NotEqual(Guid.Empty, s.DeviceId);
        Assert.Equal(Environment.MachineName, s.DeviceName);
        Assert.Equal(200, s.TargetLatencyMs);
        Assert.Null(s.LastHostName);
        Assert.Null(s.LastPairingCode);
        Assert.Null(s.LocalSpeakerDeviceId);
        Assert.False(s.FirewallConfigured);
        Assert.Empty(s.Devices);
        Assert.True(Directory.Exists(Dir));
    }

    [Fact]
    public void Settings_round_trip_and_keep_device_id()
    {
        var store = new SettingsStore(Dir);
        var first = store.Load();
        Assert.Equal(first.DeviceId, new SettingsStore(Dir).Load().DeviceId);   // defaults are persisted: the id is stable

        var device = Guid.NewGuid();
        var edited = first with
        {
            DeviceName = "Kitchen PC",
            LastHostName = "Living room",
            LastPairingCode = "0420",
            TargetLatencyMs = 300,
            LocalSpeakerDeviceId = "{0.0.0.00000000}.{abc}",
            FirewallConfigured = true,
            Devices = new Dictionary<Guid, DeviceSettings> { [device] = new(0.25f, -42.5) },
        };
        store.Save(edited);

        var loaded = new SettingsStore(Dir).Load();
        Assert.Equal(first.DeviceId, loaded.DeviceId);
        Assert.Equal("Kitchen PC", loaded.DeviceName);
        Assert.Equal("Living room", loaded.LastHostName);
        Assert.Equal("0420", loaded.LastPairingCode);
        Assert.Equal(300, loaded.TargetLatencyMs);
        Assert.Equal("{0.0.0.00000000}.{abc}", loaded.LocalSpeakerDeviceId);
        Assert.True(loaded.FirewallConfigured);
        Assert.Equal(new DeviceSettings(0.25f, -42.5), Assert.Single(loaded.Devices, kv => kv.Key == device).Value);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Corrupt_settings_backed_up_and_defaulted()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(SettingsPath, "{ \"DeviceId\": \"not-a-guid\", ");

        var s = new SettingsStore(Dir).Load();

        Assert.NotEqual(Guid.Empty, s.DeviceId);
        Assert.Equal(200, s.TargetLatencyMs);
        Assert.Equal("{ \"DeviceId\": \"not-a-guid\", ", File.ReadAllText(SettingsPath + ".bak"));
    }

    [Fact]
    public void Partial_or_out_of_range_values_are_normalized()
    {
        Directory.CreateDirectory(Dir);
        var device = Guid.NewGuid();
        File.WriteAllText(SettingsPath,
            $$"""{ "DeviceName": "  ", "TargetLatencyMs": 9000, "Devices": { "{{device}}": { "Volume": 3, "OffsetMs": -500 } } }""");

        var s = new SettingsStore(Dir).Load();

        Assert.NotEqual(Guid.Empty, s.DeviceId);
        Assert.Equal(Environment.MachineName, s.DeviceName);
        Assert.Equal(500, s.TargetLatencyMs);
        Assert.Equal(new DeviceSettings(1f, -100), s.Devices[device]);
    }
}
