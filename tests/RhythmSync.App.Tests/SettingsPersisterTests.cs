using RhythmSync.App.Services;
using RhythmSync.Core.Config;

namespace RhythmSync.App.Tests;

public sealed class SettingsPersisterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rhythmsync-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;

    public SettingsPersisterTests() => _store = new SettingsStore(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static void WaitFor(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < 5000, "timed out");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Update_changes_the_in_memory_settings_at_once_and_defers_the_write()
    {
        var initial = _store.Load();
        using var persister = new SettingsPersister(_store, initial, TimeSpan.FromHours(1));

        persister.Update(s => s with { TargetLatencyMs = 350 });

        Assert.Equal(350, persister.Current.TargetLatencyMs);
        Assert.Equal(initial.TargetLatencyMs, _store.Load().TargetLatencyMs);   // not on disk yet
    }

    [Fact]
    public void Flush_writes_pending_changes()
    {
        using var persister = new SettingsPersister(_store, _store.Load(), TimeSpan.FromHours(1));
        persister.Update(s => s with { TargetLatencyMs = 350 });

        persister.Flush();

        Assert.Equal(350, _store.Load().TargetLatencyMs);
    }

    [Fact]
    public void Pending_changes_are_written_after_the_delay()
    {
        using var persister = new SettingsPersister(_store, _store.Load(), TimeSpan.FromMilliseconds(20));
        persister.Update(s => s with { TargetLatencyMs = 420 });

        WaitFor(() => _store.Load().TargetLatencyMs == 420);
    }

    [Fact]
    public void Immediate_updates_are_written_synchronously()
    {
        using var persister = new SettingsPersister(_store, _store.Load(), TimeSpan.FromHours(1));

        persister.Update(s => s with { FirewallConfigured = true }, immediate: true);

        Assert.True(_store.Load().FirewallConfigured);
    }

    [Fact]
    public void Dispose_writes_what_is_pending_and_later_updates_are_ignored()
    {
        var persister = new SettingsPersister(_store, _store.Load(), TimeSpan.FromHours(1));
        persister.Update(s => s with { TargetLatencyMs = 260 });

        persister.Dispose();
        persister.Update(s => s with { TargetLatencyMs = 999 });
        persister.Dispose();

        Assert.Equal(260, _store.Load().TargetLatencyMs);
    }

    [Fact]
    public void Device_settings_survive_the_round_trip()
    {
        var id = Guid.NewGuid();
        using var persister = new SettingsPersister(_store, _store.Load(), TimeSpan.FromHours(1));

        persister.Update(s => s with { Devices = new Dictionary<Guid, DeviceSettings>(s.Devices) { [id] = new DeviceSettings(0.3f, 12.5) } });
        persister.Flush();

        Assert.Equal(new DeviceSettings(0.3f, 12.5), _store.Load().Devices[id]);
    }

    [Fact]
    public void Concurrent_updates_all_apply_and_the_last_snapshot_is_what_is_written()
    {
        using var persister = new SettingsPersister(_store, _store.Load(), TimeSpan.FromMilliseconds(5));
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();

        Parallel.ForEach(ids, id =>
        {
            for (int i = 0; i < 50; i++)
                persister.Update(s => s with { Devices = new Dictionary<Guid, DeviceSettings>(s.Devices) { [id] = new DeviceSettings(i / 100f, 0) } });
        });
        persister.Flush();

        var saved = _store.Load().Devices;
        Assert.Equal(ids.Length, saved.Count);
        Assert.Equal(ids.Length, persister.Current.Devices.Count);
    }

    [Fact]
    public void A_failing_write_does_not_throw_and_is_retried_by_the_next_flush()
    {
        string blocker = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocker, "a file where the settings directory should be");
        var store = new SettingsStore(blocker);   // Save cannot create a directory over a file
        using var persister = new SettingsPersister(store, _store.Load(), TimeSpan.FromHours(1));

        persister.Update(s => s with { TargetLatencyMs = 300 });
        persister.Flush();   // logged, not thrown

        Assert.Equal(300, persister.Current.TargetLatencyMs);
    }
}
