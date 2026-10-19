using System.Net;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Config;
using RhythmSync.Core.Audio.Eq;
using RhythmSync.Core.Host;
using RhythmSync.Core.Protocol;
using RhythmSync.Platform.Windows;

namespace RhythmSync.App.Tests;

public class HostViewModelTests
{
    private static readonly Guid IdA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid IdB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private sealed class FakeHost : IHostControl
    {
        public readonly List<int> Latencies = new();
        public readonly List<(Guid Id, float Volume, double Offset, EqGains Eq)> Settings = new();
        public Exception? Throw;

        public Task SetLatencyAsync(int ms)
        {
            Latencies.Add(ms);
            return Throw is null ? Task.CompletedTask : Task.FromException(Throw);
        }

        public Task SetDeviceSettingsAsync(Guid deviceId, float volume, double offsetMs, EqGains eq)
        {
            Settings.Add((deviceId, volume, offsetMs, eq));
            if (Throw is not null) throw Throw;
            return Task.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public readonly FakeHost Host = new();
        public HostAudioStatus Status = HostAudioStatus.Ok;
        public readonly Dictionary<Guid, DeviceSettings> Stored = new();
        public readonly List<(Guid Id, DeviceSettings Settings)> Saved = new();
        public int SoundSettingsOpened;
        public readonly List<EqGains> LocalEqs = new();
        public HostViewModel Vm = null!;

        public Rig(string code = "4729", string name = "Living room", int latency = 200, Guid localDeviceId = default,
            bool withSetLocalEq = true, Action<Dictionary<Guid, DeviceSettings>>? seedStored = null)
        {
            seedStored?.Invoke(Stored);
            Vm = new HostViewModel(Host, () => Status, id => Stored.TryGetValue(id, out var s) ? s : null,
                (id, s) => Saved.Add((id, s)), code, name, latency, () => SoundSettingsOpened++,
                withSetLocalEq ? LocalEqs.Add : null, localDeviceId);
        }
    }

    private static ConnectedClient Client(Guid id, string name = "Kitchen", bool ready = true, HeartbeatStats? stats = null,
        float volume = 1f, double offset = 0, EqGains? eq = null)
        => new(id, name, new IPEndPoint(IPAddress.Loopback, 5000), ready, stats, volume, offset, eq ?? EqGains.Flat);

    private static HeartbeatStats Stats(double late = 0, double loss = 0, double syncError = 0.3, double drift = 12,
        double fit = 0.05, SyncState state = SyncState.InSync)
        => new(loss, late, drift, fit, syncError, state);

    // ---- audio banner ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HostAudioStatus.Ok, null)]
    [InlineData(HostAudioStatus.VbCableMissing, "VB-CABLE not found – this PC will only send audio. Mute its speakers or install VB-CABLE.")]
    [InlineData(HostAudioStatus.VbCableNotDefault, "Set 'CABLE Input' as the default playback device so this PC plays in sync.")]
    [InlineData(HostAudioStatus.LocalSpeakerIsCaptured, "Pick real speakers for local playback – not CABLE Input.")]
    public void Each_audio_status_has_its_exact_banner(HostAudioStatus status, string? banner)
    {
        var rig = new Rig { Status = status };
        rig.Vm.RefreshAudioStatus();

        Assert.Equal(banner, rig.Vm.AudioBanner);
        Assert.Equal(banner is not null, rig.Vm.HasAudioBanner);
        Assert.Equal(status, rig.Vm.AudioStatus);
    }

    [Fact]
    public void Banner_is_read_from_the_status_func_at_construction()
    {
        var host = new FakeHost();
        var vm = new HostViewModel(host, () => HostAudioStatus.VbCableNotDefault, _ => null, (_, _) => { });
        Assert.Equal(HostViewModel.VbCableNotDefaultBanner, vm.AudioBanner);
    }

    [Fact]
    public void Refresh_updates_banner()
    {
        var rig = new Rig();
        Assert.Null(rig.Vm.AudioBanner);

        var changed = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        rig.Status = HostAudioStatus.VbCableNotDefault;
        rig.Vm.RefreshAudioStatus();

        Assert.Equal("Set 'CABLE Input' as the default playback device so this PC plays in sync.", rig.Vm.AudioBanner);
        Assert.Contains(nameof(HostViewModel.AudioBanner), changed);
    }

    [Fact]
    public void A_throwing_status_func_keeps_the_previous_banner()
    {
        bool fail = false;
        var vm = new HostViewModel(new FakeHost(), () => fail ? throw new InvalidOperationException("COM") : HostAudioStatus.VbCableMissing,
            _ => null, (_, _) => { });
        fail = true;

        vm.RefreshAudioStatus();

        Assert.Equal(HostViewModel.VbCableMissingBanner, vm.AudioBanner);
    }

    [Fact]
    public void Open_sound_settings_command_invokes_the_opener()
    {
        var rig = new Rig();
        rig.Vm.OpenSoundSettingsCommand.Execute(null);
        Assert.Equal(1, rig.SoundSettingsOpened);
    }

    // ---- latency and session name ------------------------------------------------------------------------------------

    [Fact]
    public async Task Latency_change_is_sent()
    {
        var rig = new Rig();
        rig.Vm.LatencyMs = 300;
        await Task.Yield();

        Assert.Equal(new[] { 300 }, rig.Host.Latencies);
        Assert.Equal(300, rig.Vm.LatencyMs);
    }

    [Theory]
    [InlineData(50, 100)]
    [InlineData(900, 500)]
    [InlineData(237, 240)]
    [InlineData(100, 100)]
    public void Latency_is_clamped_to_100_to_500_in_steps_of_10(int requested, int expected)
    {
        var rig = new Rig(latency: 200);
        rig.Vm.LatencyMs = requested;
        Assert.Equal(expected, rig.Vm.LatencyMs);
    }

    [Fact]
    public void Setting_the_same_latency_sends_nothing_and_construction_sends_nothing()
    {
        var rig = new Rig(latency: 200);
        rig.Vm.LatencyMs = 200;
        rig.Vm.LatencyMs = 204;   // rounds to the current value

        Assert.Empty(rig.Host.Latencies);
    }

    [Fact]
    public void Latency_change_is_reported_for_persistence_with_the_normalized_value()
    {
        var rig = new Rig();
        var reported = new List<int>();
        rig.Vm.LatencyChanged += reported.Add;

        rig.Vm.LatencyMs = 333;

        Assert.Equal(new[] { 330 }, reported);
    }

    [Fact]
    public void A_failing_latency_send_does_not_throw_into_the_ui()
    {
        var rig = new Rig();
        rig.Host.Throw = new InvalidOperationException("host gone");
        rig.Vm.LatencyMs = 300;
        Assert.Equal(300, rig.Vm.LatencyMs);
    }

    [Fact]
    public void Initial_values_are_exposed()
    {
        var rig = new Rig("1234", "Den", 250);
        Assert.Equal("1234", rig.Vm.PairingCode);
        Assert.Equal("Den", rig.Vm.SessionName);
        Assert.Equal(250, rig.Vm.LatencyMs);
        Assert.False(rig.Vm.ShowStats);
    }

    [Fact]
    public void Session_name_can_be_changed_and_read()
    {
        var rig = new Rig();
        rig.Vm.SessionName = "Patio";
        Assert.Equal("Patio", rig.Vm.SessionName);
    }

    [Fact]
    public void Show_stats_toggles()
    {
        var rig = new Rig();
        rig.Vm.ShowStats = true;
        Assert.True(rig.Vm.ShowStats);
    }

    // ---- client rows -------------------------------------------------------------------------------------------------

    [Fact]
    public void UpdateClients_adds_updates_and_removes_rows()
    {
        var rig = new Rig();

        rig.Vm.UpdateClients(new[] { Client(IdA, "Kitchen"), Client(IdB, "Bedroom") });
        Assert.Equal(new[] { IdA, IdB }, rig.Vm.Clients.Select(c => c.DeviceId));

        var rowA = rig.Vm.Clients[0];
        rig.Vm.UpdateClients(new[] { Client(IdA, "Kitchen PC", stats: Stats(syncError: 0.8)), Client(IdB, "Bedroom") });
        Assert.Same(rowA, rig.Vm.Clients[0]);   // updated in place, not replaced
        Assert.Equal("Kitchen PC", rowA.Name);
        Assert.Equal(0.8, rowA.SyncErrorMs);
        Assert.Equal(SyncState.InSync, rowA.State);

        rig.Vm.UpdateClients(new[] { Client(IdB, "Bedroom") });
        Assert.Equal(new[] { IdB }, rig.Vm.Clients.Select(c => c.DeviceId));

        rig.Vm.UpdateClients(Array.Empty<ConnectedClient>());
        Assert.Empty(rig.Vm.Clients);
    }

    [Fact]
    public void Row_state_without_stats_is_connecting_until_ready_then_syncing()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, ready: false) });
        Assert.Equal(SyncState.Connecting, rig.Vm.Clients[0].State);
        Assert.True(double.IsNaN(rig.Vm.Clients[0].SyncErrorMs));

        rig.Vm.UpdateClients(new[] { Client(IdA, ready: true) });
        Assert.Equal(SyncState.Syncing, rig.Vm.Clients[0].State);
    }

    [Fact]
    public void Row_exposes_the_heartbeat_stats()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, stats: Stats(late: 1.5, loss: 0.25, syncError: 0.4, drift: -30, fit: 0.07, state: SyncState.Degraded)) });

        var row = rig.Vm.Clients[0];
        Assert.Equal(1.5, row.LatePct);
        Assert.Equal(0.25, row.LossPct);
        Assert.Equal(0.4, row.SyncErrorMs);
        Assert.Equal(-30, row.DriftPpm);
        Assert.Equal(0.07, row.FitResidualMs);
        Assert.Equal(SyncState.Degraded, row.State);
    }

    [Fact]
    public void Duplicate_device_ids_in_a_snapshot_do_not_throw_or_duplicate_rows()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, "one"), Client(IdA, "two") });
        Assert.Single(rig.Vm.Clients);
    }

    // ---- congestion --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(6.0, true)]
    [InlineData(5.01, true)]
    [InlineData(5.0, false)]
    [InlineData(0.0, false)]
    [InlineData(double.NaN, false)]
    public void Congested_when_late_over_5_percent(double late, bool congested)
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, stats: Stats(late: late)) });

        var row = rig.Vm.Clients[0];
        Assert.Equal(congested, row.Congested);
        Assert.Equal(congested ? "Wi-Fi congested – try higher latency" : "", row.CongestedText);
    }

    [Fact]
    public void Congested_is_false_without_stats()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA) });
        Assert.False(rig.Vm.Clients[0].Congested);
    }

    [Fact]
    public void NaN_stats_display_as_a_dash()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, stats: Stats(late: double.NaN, loss: double.NaN, syncError: double.NaN, drift: double.NaN, fit: double.NaN)) });

        var row = rig.Vm.Clients[0];
        Assert.Equal("—", row.LossText);
        Assert.Equal("—", row.LateText);
        Assert.Equal("—", row.SyncErrorText);
        Assert.Equal("—", row.DriftText);
        Assert.Equal("—", row.FitResidualText);
        Assert.DoesNotContain("NaN", row.SyncErrorText + row.LossText + row.LateText + row.DriftText + row.FitResidualText);
    }

    [Fact]
    public void Missing_stats_display_as_a_dash()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA) });
        Assert.Equal("—", rig.Vm.Clients[0].SyncErrorText);
        Assert.Equal("—", rig.Vm.Clients[0].LossText);
    }

    [Fact]
    public void Finite_stats_are_formatted_with_units()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, stats: Stats(late: 1.234, loss: 0.5, syncError: 0.4567, drift: 12.4, fit: 0.071)) });

        var row = rig.Vm.Clients[0];
        Assert.Equal("0.46 ms", row.SyncErrorText);
        Assert.Equal("0.5 %", row.LossText);
        Assert.Equal("1.2 %", row.LateText);
        Assert.Equal("12 ppm", row.DriftText);
        Assert.Equal("0.07 ms", row.FitResidualText);
    }

    // ---- per-client settings -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Row_offset_change_is_sent()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA) });
        await Task.Yield();

        rig.Vm.Clients[0].OffsetMs = 12.5;

        Assert.Equal((IdA, 1f, 12.5, EqGains.Flat), Assert.Single(rig.Host.Settings));
    }

    [Fact]
    public async Task Row_volume_change_sends_the_current_offset_too()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, offset: -5) });
        await Task.Yield();

        rig.Vm.Clients[0].Volume = 0.4f;

        Assert.Equal((IdA, 0.4f, -5.0, EqGains.Flat), Assert.Single(rig.Host.Settings));
    }

    [Fact]
    public async Task New_client_gets_stored_settings()
    {
        var rig = new Rig();
        rig.Stored[IdA] = new DeviceSettings(0.7f, -5);

        rig.Vm.UpdateClients(new[] { Client(IdA) });
        await Task.Yield();

        Assert.Equal((IdA, 0.7f, -5.0, EqGains.Flat), Assert.Single(rig.Host.Settings));
        Assert.Equal(0.7f, rig.Vm.Clients[0].Volume);
        Assert.Equal(-5, rig.Vm.Clients[0].OffsetMs);
        Assert.Empty(rig.Saved);   // restoring a stored value is not a user change
    }

    [Fact]
    public void Stored_settings_are_sent_once_not_on_every_update()
    {
        var rig = new Rig();
        rig.Stored[IdA] = new DeviceSettings(0.7f, -5);

        rig.Vm.UpdateClients(new[] { Client(IdA) });
        rig.Vm.UpdateClients(new[] { Client(IdA, volume: 0.7f, offset: -5) });
        rig.Vm.UpdateClients(new[] { Client(IdA, volume: 0.7f, offset: -5) });

        Assert.Single(rig.Host.Settings);
    }

    [Fact]
    public void A_client_that_leaves_and_returns_gets_its_stored_settings_again()
    {
        var rig = new Rig();
        rig.Stored[IdA] = new DeviceSettings(0.7f, -5);

        rig.Vm.UpdateClients(new[] { Client(IdA) });
        rig.Vm.UpdateClients(Array.Empty<ConnectedClient>());
        rig.Vm.UpdateClients(new[] { Client(IdA) });

        Assert.Equal(2, rig.Host.Settings.Count);
    }

    [Fact]
    public void New_client_without_stored_settings_sends_nothing_and_shows_the_hosts_values()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, volume: 0.6f, offset: 3) });

        Assert.Empty(rig.Host.Settings);
        Assert.Equal(0.6f, rig.Vm.Clients[0].Volume);
        Assert.Equal(3, rig.Vm.Clients[0].OffsetMs);
    }

    [Fact]
    public void Row_change_is_saved()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, offset: 8) });

        rig.Vm.Clients[0].Volume = 0.3f;

        var (id, settings) = Assert.Single(rig.Saved);
        Assert.Equal(IdA, id);
        Assert.Equal(new DeviceSettings(0.3f, 8), settings);
    }

    [Fact]
    public void A_snapshot_that_lost_the_row_values_after_a_reconnect_gets_them_pushed_again_without_saving()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA) });
        rig.Vm.Clients[0].Volume = 0.25f;
        rig.Vm.Clients[0].OffsetMs = 40;
        rig.Host.Settings.Clear();
        rig.Saved.Clear();

        // The client reconnected: the host-side values are back at volume 1, offset 0.
        rig.Vm.UpdateClients(new[] { Client(IdA, ready: false) });

        Assert.Equal((IdA, 0.25f, 40.0, EqGains.Flat), Assert.Single(rig.Host.Settings));
        Assert.Equal(0.25f, rig.Vm.Clients[0].Volume);
        Assert.Equal(40, rig.Vm.Clients[0].OffsetMs);
        Assert.Empty(rig.Saved);

        // Once the host reports the same values nothing more is sent.
        rig.Vm.UpdateClients(new[] { Client(IdA, volume: 0.25f, offset: 40) });
        Assert.Single(rig.Host.Settings);
    }

    [Fact]
    public void Row_values_are_clamped_before_reaching_the_host()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA) });
        var row = rig.Vm.Clients[0];

        row.Volume = 7f;
        row.OffsetMs = -500;
        row.Volume = -1f;
        row.OffsetMs = 500;

        Assert.All(rig.Host.Settings, s =>
        {
            Assert.InRange(s.Volume, 0f, 1f);
            Assert.InRange(s.Offset, -100.0, 100.0);
        });
        Assert.Equal(0f, row.Volume);
        Assert.Equal(100, row.OffsetMs);
    }

    [Fact]
    public void NaN_and_infinity_never_reach_the_host()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA) });
        var row = rig.Vm.Clients[0];

        row.Volume = float.NaN;
        row.OffsetMs = double.NaN;
        row.Volume = float.PositiveInfinity;   // clamps to 1 (already 1)
        row.OffsetMs = double.NegativeInfinity;   // clamps to -100

        Assert.All(rig.Host.Settings, s =>
        {
            Assert.True(float.IsFinite(s.Volume));
            Assert.True(double.IsFinite(s.Offset));
        });
        Assert.Equal(1f, row.Volume);
        Assert.Equal(-100, row.OffsetMs);
    }

    [Fact]
    public void A_throwing_host_or_save_does_not_throw_into_the_ui()
    {
        var host = new FakeHost { Throw = new InvalidOperationException("gone") };
        var vm = new HostViewModel(host, () => HostAudioStatus.Ok, _ => null, (_, _) => throw new IOException("disk"));
        vm.UpdateClients(new[] { Client(IdA) });

        vm.Clients[0].Volume = 0.5f;

        Assert.Equal(0.5f, vm.Clients[0].Volume);
    }

    [Fact]
    public void A_throwing_stored_settings_lookup_still_adds_the_row()
    {
        var vm = new HostViewModel(new FakeHost(), () => HostAudioStatus.Ok, _ => throw new IOException("disk"), (_, _) => { });
        vm.UpdateClients(new[] { Client(IdA) });
        Assert.Single(vm.Clients);
    }

    [Fact]
    public void Row_texts_show_percent_and_signed_offset()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, volume: 0.5f, offset: 12.4) });

        var row = rig.Vm.Clients[0];
        Assert.Equal("Volume: 50%", row.VolumeText);
        Assert.Equal("Offset: +12 ms", row.OffsetText);
    }

    [Theory]
    [InlineData(SyncState.Connecting, "Connecting…")]
    [InlineData(SyncState.Syncing, "Syncing…")]
    [InlineData(SyncState.InSync, "In sync")]
    [InlineData(SyncState.Degraded, "Sync is degraded")]
    [InlineData(SyncState.Disconnected, "Not connected")]
    public void Row_status_text_follows_the_clients_reported_state(SyncState state, string text)
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, stats: Stats(state: state)) });
        Assert.Equal(text, rig.Vm.Clients[0].StatusText);
    }

    // ---- EQ ----------------------------------------------------------------------------------------------------------

    private static readonly EqGains Laptop = EqPresets.All.First(p => p.Name == "Laptop speakers").Gains;
    private static readonly EqGains Bass = EqPresets.All.First(p => p.Name == "Bass boost").Gains;
    private static readonly Guid LocalId = Guid.Parse("00000000-0000-0000-0000-0000000000cc");

    [Fact]
    public void A_reconnected_client_with_a_different_eq_gets_a_resend()
    {
        var rig = new Rig();
        rig.Stored[IdA] = new DeviceSettings(1f, 0, Laptop.ToArray());
        rig.Vm.UpdateClients(new[] { Client(IdA) });
        rig.Host.Settings.Clear();
        rig.Saved.Clear();

        // Same volume and offset, but the new connection starts with a flat EQ.
        rig.Vm.UpdateClients(new[] { Client(IdA, ready: false, eq: EqGains.Flat) });

        Assert.Equal((IdA, 1f, 0.0, Laptop), Assert.Single(rig.Host.Settings));
        Assert.Equal(Laptop, rig.Vm.Clients[0].Eq.Gains);
        Assert.Empty(rig.Saved);

        // Once the host reports the same EQ nothing more is sent.
        rig.Vm.UpdateClients(new[] { Client(IdA, eq: Laptop) });
        Assert.Single(rig.Host.Settings);
    }

    [Fact]
    public void A_new_row_adopts_and_sends_its_stored_eq()
    {
        var rig = new Rig();
        rig.Stored[IdA] = new DeviceSettings(0.7f, -5, Bass.ToArray());

        rig.Vm.UpdateClients(new[] { Client(IdA) });

        Assert.Equal(Bass, rig.Vm.Clients[0].Eq.Gains);
        Assert.Equal((IdA, 0.7f, -5.0, Bass), Assert.Single(rig.Host.Settings));
        Assert.Empty(rig.Saved);
    }

    [Fact]
    public void A_new_row_without_stored_settings_shows_the_hosts_eq()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, eq: Bass) });

        Assert.Equal(Bass, rig.Vm.Clients[0].Eq.Gains);
        Assert.Empty(rig.Host.Settings);
    }

    [Fact]
    public void A_stored_eq_that_is_invalid_becomes_flat()
    {
        var rig = new Rig();
        rig.Stored[IdA] = new DeviceSettings(0.7f, 0, new float[] { 1, 2, 3 });

        rig.Vm.UpdateClients(new[] { Client(IdA) });

        Assert.True(rig.Vm.Clients[0].Eq.Gains.IsFlat);
    }

    [Fact]
    public void A_user_eq_change_sends_and_saves_null_for_flat_and_values_otherwise()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA, volume: 0.8f, offset: 4) });

        rig.Vm.Clients[0].Eq.SelectedPreset = "Bass boost";

        Assert.Equal((IdA, 0.8f, 4.0, Bass), Assert.Single(rig.Host.Settings));
        var (id, saved) = Assert.Single(rig.Saved);
        Assert.Equal(IdA, id);
        Assert.Equal(0.8f, saved.Volume);
        Assert.Equal(4, saved.OffsetMs);
        Assert.Equal(Bass.ToArray(), saved.EqGainsDb);

        rig.Vm.Clients[0].Eq.FlatCommand.Execute(null);

        Assert.Equal(2, rig.Host.Settings.Count);
        Assert.Equal(EqGains.Flat, rig.Host.Settings[1].Eq);
        Assert.Equal(new DeviceSettings(0.8f, 4, null), rig.Saved[1].Settings);
        Assert.Null(rig.Saved[1].Settings.EqGainsDb);
    }

    [Fact]
    public void A_band_move_sends_once()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA) });

        rig.Vm.Clients[0].Eq.Bands[3].GainDb = 2.5;

        var sent = Assert.Single(rig.Host.Settings);
        Assert.Equal(2.5f, sent.Eq[3]);
    }

    [Fact]
    public void Volume_change_after_an_eq_change_resends_the_same_eq()
    {
        var rig = new Rig();
        rig.Vm.UpdateClients(new[] { Client(IdA) });
        rig.Vm.Clients[0].Eq.Bands[2].GainDb = 3;
        var eq = rig.Vm.Clients[0].Eq.Gains;

        rig.Vm.Clients[0].Volume = 0.5f;

        Assert.Equal(2, rig.Host.Settings.Count);
        Assert.Equal((IdA, 0.5f, 0.0, eq), rig.Host.Settings[^1]);
        Assert.Equal(eq.ToArray(), rig.Saved[^1].Settings.EqGainsDb);
    }

    [Fact]
    public void This_pc_loads_and_saves_under_the_local_device_id_and_calls_setLocalEq()
    {
        var rig = new Rig(localDeviceId: LocalId, seedStored: stored => stored[LocalId] = new DeviceSettings(1f, 0, Laptop.ToArray()));

        Assert.Equal(Laptop, rig.Vm.LocalSpeaker.Eq.Gains);
        Assert.Empty(rig.LocalEqs);   // adopting the stored curve is not a change
        Assert.Empty(rig.Saved);

        rig.Vm.LocalSpeaker.Eq.SelectedPreset = "Bass boost";

        Assert.Equal(Bass, Assert.Single(rig.LocalEqs));
        var (id, saved) = Assert.Single(rig.Saved);
        Assert.Equal(LocalId, id);
        Assert.Equal(1f, saved.Volume);
        Assert.Equal(0, saved.OffsetMs);
        Assert.Equal(Bass.ToArray(), saved.EqGainsDb);
        Assert.Empty(rig.Host.Settings);   // this PC is not a remote client

        rig.Vm.LocalSpeaker.Eq.FlatCommand.Execute(null);

        Assert.Equal(2, rig.LocalEqs.Count);
        Assert.Equal(EqGains.Flat, rig.LocalEqs[1]);
        Assert.Null(rig.Saved[1].Settings.EqGainsDb);
    }

    [Fact]
    public void This_pc_does_not_save_without_a_local_device_id_but_still_applies()
    {
        var rig = new Rig();   // Guid.Empty

        rig.Vm.LocalSpeaker.Eq.SelectedPreset = "Treble cut";

        Assert.Single(rig.LocalEqs);
        Assert.Empty(rig.Saved);
    }

    [Fact]
    public void This_pc_without_setLocalEq_still_has_a_row_and_changes_call_nothing()
    {
        var rig = new Rig(localDeviceId: LocalId, withSetLocalEq: false);

        rig.Vm.LocalSpeaker.Eq.SelectedPreset = "Bass boost";

        Assert.Empty(rig.LocalEqs);
        Assert.Single(rig.Saved);
    }

    [Fact]
    public void This_pc_survives_setLocalEq_and_save_failures()
    {
        var vm = new HostViewModel(new FakeHost(), () => HostAudioStatus.Ok, _ => null,
            (_, _) => throw new IOException("disk"), setLocalEq: _ => throw new InvalidOperationException("pipeline"),
            localDeviceId: LocalId);

        var ex = Record.Exception(() => vm.LocalSpeaker.Eq.SelectedPreset = "Bass boost");

        Assert.Null(ex);
        Assert.Equal(Bass, vm.LocalSpeaker.Eq.Gains);
    }

    [Fact]
    public void This_pc_ignores_a_throwing_stored_settings_read()
    {
        var vm = new HostViewModel(new FakeHost(), () => HostAudioStatus.Ok, _ => throw new IOException("x"), (_, _) => { },
            localDeviceId: LocalId);

        Assert.True(vm.LocalSpeaker.Eq.Gains.IsFlat);
    }

    [Theory]
    [InlineData(HostAudioStatus.Ok, true)]
    [InlineData(HostAudioStatus.VbCableMissing, false)]
    [InlineData(HostAudioStatus.VbCableNotDefault, false)]
    [InlineData(HostAudioStatus.LocalSpeakerIsCaptured, false)]
    public void This_pc_shows_not_playing_unless_audio_status_is_ok(HostAudioStatus status, bool playing)
    {
        var rig = new Rig { Status = status };
        rig.Vm.RefreshAudioStatus();

        Assert.Equal(playing, rig.Vm.LocalSpeaker.IsPlaying);
        Assert.Equal(playing ? "Playing here" : "Not playing on this PC", rig.Vm.LocalSpeaker.StatusText);
        Assert.Equal(playing ? RhythmSync.App.Controls.StatusKind.Hosting : RhythmSync.App.Controls.StatusKind.NotConnected,
            rig.Vm.LocalSpeaker.Kind);

        // And it follows later changes.
        rig.Status = playing ? HostAudioStatus.VbCableMissing : HostAudioStatus.Ok;
        rig.Vm.RefreshAudioStatus();
        Assert.Equal(!playing, rig.Vm.LocalSpeaker.IsPlaying);
    }

    [Fact]
    public void This_pc_follows_actual_local_playback_when_the_owner_reports_it()
    {
        bool playing = false;
        var vm = new HostViewModel(new FakeHost(), () => HostAudioStatus.Ok, _ => null, (_, _) => { }, isPlayingLocally: () => playing);

        // The routing is fine, but nothing plays (no speaker resolves, or the output failed and is retrying).
        Assert.False(vm.LocalSpeaker.IsPlaying);
        Assert.Equal("Not playing on this PC", vm.LocalSpeaker.StatusText);

        playing = true;
        vm.RefreshLocalPlayback();
        Assert.True(vm.LocalSpeaker.IsPlaying);
        Assert.Equal("Playing here", vm.LocalSpeaker.StatusText);

        playing = false;
        vm.RefreshAudioStatus();   // the status did not change; the chip does not move on its own
        Assert.True(vm.LocalSpeaker.IsPlaying);
        vm.RefreshLocalPlayback();
        Assert.False(vm.LocalSpeaker.IsPlaying);
    }

    [Fact]
    public void This_pc_playing_callback_is_read_when_the_status_changes_and_a_failing_one_keeps_the_chip()
    {
        bool playing = true;
        bool fail = false;
        var status = HostAudioStatus.Ok;
        var vm = new HostViewModel(new FakeHost(), () => status, _ => null, (_, _) => { },
            isPlayingLocally: () => fail ? throw new InvalidOperationException("gone") : playing);
        Assert.True(vm.LocalSpeaker.IsPlaying);

        playing = false;
        status = HostAudioStatus.VbCableMissing;
        vm.RefreshAudioStatus();
        Assert.False(vm.LocalSpeaker.IsPlaying);

        playing = true;
        fail = true;
        vm.RefreshLocalPlayback();   // logged; the last value stays
        Assert.False(vm.LocalSpeaker.IsPlaying);
    }

    [Fact]
    public void This_pc_copy_is_exact()
    {
        Assert.Equal("This PC", LocalSpeakerRowViewModel.NameText);
        Assert.Equal("Playing here", LocalSpeakerRowViewModel.PlayingText);
        Assert.Equal("Not playing on this PC", LocalSpeakerRowViewModel.NotPlayingText);
    }

    [Fact]
    public void This_pc_is_not_counted_as_a_client()
    {
        var rig = new Rig();

        Assert.Empty(rig.Vm.Clients);
        Assert.True(rig.Vm.HasNoClients);

        rig.Vm.UpdateClients(new[] { Client(IdA) });
        Assert.Single(rig.Vm.Clients);
    }
}
