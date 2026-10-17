using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RhythmSync.Core.Config;
using RhythmSync.Core.Host;
using RhythmSync.Platform.Windows;
using Serilog;

namespace RhythmSync.App.ViewModels;

/// <summary>
/// The running host: pairing code, session name, latency, the audio-routing banner and one row per connected client.
/// Everything here runs on the UI thread; the owner marshals host callbacks (client list changes, device changes) onto
/// it. Calls into the host are fire-and-forget and their failures are logged, never thrown into the UI.
/// </summary>
public sealed partial class HostViewModel : ObservableObject
{
    public const string VbCableMissingBanner = "VB-CABLE not found – this PC will only send audio. Mute its speakers or install VB-CABLE.";
    public const string VbCableNotDefaultBanner = "Set 'CABLE Input' as the default playback device so this PC plays in sync.";
    public const string LocalSpeakerIsCapturedBanner = "Pick real speakers for local playback – not CABLE Input.";

    public const int MinLatencyMs = 100;
    public const int MaxLatencyMs = 500;
    public const int LatencyStepMs = 10;

    private readonly IHostControl _host;
    private readonly Func<HostAudioStatus> _audioStatus;
    private readonly Func<Guid, DeviceSettings?> _storedSettings;
    private readonly Action<Guid, DeviceSettings> _saveSettings;

    private string _sessionName;
    private int _latencyMs;
    private HostAudioStatus _status = HostAudioStatus.Ok;

    /// <param name="host">What the host session offers.</param>
    /// <param name="audioStatus">Evaluates the audio routing; called at construction and by <see cref="RefreshAudioStatus"/>.</param>
    /// <param name="storedSettings">Remembered settings for a client device, or null.</param>
    /// <param name="saveSettings">Persists a client's settings after the user changed them (the owner should keep this cheap).</param>
    /// <param name="pairingCode">The code clients type to join.</param>
    /// <param name="sessionName">The name announced to the LAN.</param>
    /// <param name="latencyMs">The host's current target latency.</param>
    /// <param name="openSoundSettings">Opens the Windows sound settings; defaults to <c>ms-settings:sound</c>.</param>
    public HostViewModel(IHostControl host, Func<HostAudioStatus> audioStatus, Func<Guid, DeviceSettings?> storedSettings,
        Action<Guid, DeviceSettings> saveSettings, string pairingCode = "", string sessionName = "", int latencyMs = 200,
        Action? openSoundSettings = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _audioStatus = audioStatus ?? throw new ArgumentNullException(nameof(audioStatus));
        _storedSettings = storedSettings ?? throw new ArgumentNullException(nameof(storedSettings));
        _saveSettings = saveSettings ?? throw new ArgumentNullException(nameof(saveSettings));
        PairingCode = pairingCode;
        _sessionName = sessionName;
        _latencyMs = NormalizeLatency(latencyMs);

        var open = openSoundSettings ?? OpenWindowsSoundSettings;
        OpenSoundSettingsCommand = new RelayCommand(() =>
        {
            try { open(); }
            catch (Exception e) { Log.Warning(e, "Could not open the Windows sound settings"); }
        });

        Clients.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoClients));
        RefreshAudioStatus();
    }

    /// <summary>Raised on the UI thread after the user changed the latency, with the value that was sent to the host.</summary>
    public event Action<int>? LatencyChanged;

    public string PairingCode { get; }

    public ObservableCollection<ClientRowViewModel> Clients { get; } = new();

    /// <summary>True while no client is connected.</summary>
    public bool HasNoClients => Clients.Count == 0;

    /// <summary>The name shown to clients in their host list. Safe to read from any thread (the discovery beacon reads it).</summary>
    public string SessionName
    {
        get => Volatile.Read(ref _sessionName);
        set
        {
            value ??= "";
            if (value == Volatile.Read(ref _sessionName)) return;
            Volatile.Write(ref _sessionName, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Target latency in ms: 100..500 in steps of 10. A change is sent to the host and its clients.</summary>
    public int LatencyMs
    {
        get => _latencyMs;
        set
        {
            int v = NormalizeLatency(value);
            if (!SetProperty(ref _latencyMs, v)) return;
            OnPropertyChanged(nameof(LatencyText));
            LatencyChanged?.Invoke(v);
            Send(() => _host.SetLatencyAsync(v), "setting the latency");
        }
    }

    public string LatencyText => $"Latency: {_latencyMs} ms";

    [ObservableProperty]
    private bool _showStats;

    // ---- audio routing -----------------------------------------------------------------------------------------------

    public HostAudioStatus AudioStatus
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(AudioBanner));
            OnPropertyChanged(nameof(HasAudioBanner));
        }
    }

    /// <summary>The routing problem to show, or null when the audio setup is fine.</summary>
    public string? AudioBanner => _status switch
    {
        HostAudioStatus.VbCableMissing => VbCableMissingBanner,
        HostAudioStatus.VbCableNotDefault => VbCableNotDefaultBanner,
        HostAudioStatus.LocalSpeakerIsCaptured => LocalSpeakerIsCapturedBanner,
        _ => null,
    };

    public bool HasAudioBanner => AudioBanner is not null;

    /// <summary>Opens the Windows sound settings.</summary>
    public IRelayCommand OpenSoundSettingsCommand { get; }

    /// <summary>Re-evaluates the audio routing (call when the default playback device changes). A failing check keeps the last result.</summary>
    public void RefreshAudioStatus()
    {
        try
        {
            AudioStatus = _audioStatus();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Checking the host audio routing failed");
        }
    }

    private static void OpenWindowsSoundSettings()
        => Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true })?.Dispose();

    // ---- clients -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Brings the rows in line with the host's client list: rows of departed clients are removed, new clients get a row
    /// (and their remembered volume/offset are sent to the host once), and existing rows are updated in place.
    /// </summary>
    public void UpdateClients(IReadOnlyList<ConnectedClient> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);

        var present = new Dictionary<Guid, ConnectedClient>();
        foreach (var c in clients) present[c.DeviceId] = c;   // a repeated id keeps the last entry

        for (int i = Clients.Count - 1; i >= 0; i--)
            if (!present.ContainsKey(Clients[i].DeviceId)) Clients.RemoveAt(i);

        foreach (var (id, client) in present)
        {
            var row = Clients.FirstOrDefault(r => r.DeviceId == id);
            if (row is not null)
            {
                row.Apply(client);
                // The rows are the source of truth for volume and offset: only this view model changes them on the host. A
                // difference therefore means the client reconnected (a new connection starts at volume 1, offset 0): push ours again.
                if (client.Volume != row.Volume || client.OffsetMs != row.OffsetMs)
                {
                    var stale = row;
                    Send(() => _host.SetDeviceSettingsAsync(id, stale.Volume, stale.OffsetMs), "re-sending a reconnected client's settings");
                }
                continue;
            }

            row = new ClientRowViewModel(client, OnRowChangedByUser);
            if (SafeStored(id) is { } stored)
            {
                row.Adopt(stored.Volume, stored.OffsetMs);
                var added = row;
                Send(() => _host.SetDeviceSettingsAsync(id, added.Volume, added.OffsetMs), "restoring a client's settings");
            }
            Clients.Add(row);
        }
    }

    private DeviceSettings? SafeStored(Guid id)
    {
        try { return _storedSettings(id); }
        catch (Exception e)
        {
            Log.Warning(e, "Reading the stored settings of {Device} failed", id);
            return null;
        }
    }

    private void OnRowChangedByUser(ClientRowViewModel row)
    {
        Guid id = row.DeviceId;
        float volume = row.Volume;
        double offset = row.OffsetMs;
        Send(() => _host.SetDeviceSettingsAsync(id, volume, offset), "sending a client's settings");
        try { _saveSettings(id, new DeviceSettings(volume, offset)); }
        catch (Exception e) { Log.Warning(e, "Saving the settings of {Device} failed", id); }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static int NormalizeLatency(int ms)
        => Math.Clamp((int)Math.Round(ms / (double)LatencyStepMs, MidpointRounding.AwayFromZero) * LatencyStepMs, MinLatencyMs, MaxLatencyMs);

    /// <summary>Starts a host call without waiting for it; a synchronous throw or an asynchronous fault is logged.</summary>
    private static void Send(Func<Task> call, string what)
    {
        try
        {
            call().ContinueWith(t => Log.Warning(t.Exception, "Host call failed while {What}", what),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Host call failed while {What}", what);
        }
    }
}
