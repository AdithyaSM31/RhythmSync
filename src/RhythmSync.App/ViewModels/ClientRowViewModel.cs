using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RhythmSync.Core.Audio.Eq;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Host;

namespace RhythmSync.App.ViewModels;

/// <summary>
/// One connected client as the host sees it: name, sync state, the statistics from its heartbeats, and the volume,
/// offset and EQ the host can set for it. Created and updated by <see cref="HostViewModel"/> on the UI thread. A value
/// change made by the user (not one adopted from the host) is reported through the callback given at construction.
/// </summary>
public sealed partial class ClientRowViewModel : ObservableObject
{
    /// <summary>Late-packet percentage above which the row warns about the Wi-Fi.</summary>
    public const double CongestedLatePct = 5;

    public const string CongestedMessage = "Wi-Fi congested – try higher latency";
    public const float MaxVolume = 1f;
    public const double MaxOffsetMs = 100;

    private readonly Action<ClientRowViewModel> _userChanged;
    private float _volume = MaxVolume;
    private double _offsetMs;
    private string _offsetInput = "0";
    private bool _adopting;

    internal ClientRowViewModel(ConnectedClient client, Action<ClientRowViewModel> userChanged)
    {
        ArgumentNullException.ThrowIfNull(client);
        _userChanged = userChanged ?? throw new ArgumentNullException(nameof(userChanged));
        DeviceId = client.DeviceId;
        Eq = new EqViewModel(client.Eq);
        Eq.Changed += () => _userChanged(this);
        Apply(client);
        Adopt(client.Volume, client.OffsetMs, client.Eq);
    }

    public Guid DeviceId { get; }

    /// <summary>This client's EQ. A user change is reported like a volume or offset change.</summary>
    public EqViewModel Eq { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private SyncState _state = SyncState.Connecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncErrorText))]
    private double _syncErrorMs = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LossText))]
    private double _lossPct = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LateText), nameof(Congested), nameof(CongestedText))]
    private double _latePct = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriftText))]
    private double _driftPpm = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FitResidualText))]
    private double _fitResidualMs = double.NaN;

    /// <summary>More than <see cref="CongestedLatePct"/> % of packets arrived late. Unknown (NaN) is not congested.</summary>
    public bool Congested => LatePct > CongestedLatePct;

    /// <summary>The warning for a congested row; empty otherwise.</summary>
    public string CongestedText => Congested ? CongestedMessage : "";

    public string StatusText => State switch
    {
        SyncState.Connecting => "Connecting…",
        SyncState.Syncing => "Syncing…",
        SyncState.InSync => "In sync",
        SyncState.Degraded => "Sync is degraded",
        _ => "Not connected",
    };

    public string SyncErrorText => StatFormat.WithUnit(SyncErrorMs, 2, "ms");
    public string LossText => StatFormat.WithUnit(LossPct, 1, "%");
    public string LateText => StatFormat.WithUnit(LatePct, 1, "%");
    public string DriftText => StatFormat.WithUnit(DriftPpm, 0, "ppm");
    public string FitResidualText => StatFormat.WithUnit(FitResidualMs, 2, "ms");

    // ---- volume and offset -------------------------------------------------------------------------------------------

    /// <summary>Linear volume, 0..1. NaN is ignored; infinities clamp.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            if (float.IsNaN(value)) return;
            float v = Math.Clamp(value, 0f, MaxVolume);
            if (!SetProperty(ref _volume, v)) return;
            OnPropertyChanged(nameof(VolumeText));
            if (!_adopting) _userChanged(this);
        }
    }

    /// <summary>Per-client offset in ms, −100..100 (positive plays later). NaN is ignored; infinities clamp.</summary>
    public double OffsetMs
    {
        get => _offsetMs;
        set
        {
            if (double.IsNaN(value)) return;
            double v = Math.Clamp(value, -MaxOffsetMs, MaxOffsetMs);
            if (!SetProperty(ref _offsetMs, v)) return;
            OnPropertyChanged(nameof(OffsetText));
            ShowOffsetInput();
            if (!_adopting) _userChanged(this);
        }
    }

    /// <summary>
    /// The text of the offset box: the exact offset (to 0.1 ms) while it follows <see cref="OffsetMs"/>, or what the user
    /// has typed so far. <see cref="CommitOffsetInputCommand"/> applies it.
    /// </summary>
    public string OffsetInput
    {
        get => _offsetInput;
        set => SetProperty(ref _offsetInput, value);
    }

    /// <summary>Moves the offset 1 ms earlier: from its value rounded to a whole ms, clamped to ±100.</summary>
    [RelayCommand]
    private void DecreaseOffset() => OffsetMs = Math.Round(OffsetMs) - 1;

    /// <summary>Moves the offset 1 ms later: from its value rounded to a whole ms, clamped to ±100.</summary>
    [RelayCommand]
    private void IncreaseOffset() => OffsetMs = Math.Round(OffsetMs) + 1;

    /// <summary>
    /// Applies the text in the offset box (Enter or leaving the box). Text that is not a number is ignored; either way
    /// the box then shows the current offset again.
    /// </summary>
    [RelayCommand]
    private void CommitOffsetInput()
    {
        if (TryParseOffset(OffsetInput, out double ms))
            OffsetMs = ms;
        ShowOffsetInput();
    }

    /// <summary>
    /// Parses a typed offset: invariant culture, an optional sign, digits with an optional decimal point and an optional
    /// "ms" suffix, spaces around. Rounds to 0.1 ms and clamps to ±100. Anything else (including NaN and infinities) fails.
    /// </summary>
    internal static bool TryParseOffset(string? text, out double ms)
    {
        ms = 0;
        string t = (text ?? "").Trim();
        if (t.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            t = t[..^2].TrimEnd();
        if (t.Length == 0 ||
            !double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v) ||
            !double.IsFinite(v))
            return false;
        ms = Math.Clamp(Math.Round(v, 1, MidpointRounding.AwayFromZero), -MaxOffsetMs, MaxOffsetMs) + 0.0;   // + 0.0: no negative zero
        return true;
    }

    private void ShowOffsetInput()
    {
        string text = Math.Round(_offsetMs, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);
        // Raised even when the text is unchanged: the box must revert what was typed.
        if (!SetProperty(ref _offsetInput, text == "-0" ? "0" : text, nameof(OffsetInput)))
            OnPropertyChanged(nameof(OffsetInput));
    }

    public string VolumeText => $"Volume: {Math.Round(_volume * 100).ToString("F0", CultureInfo.InvariantCulture)}%";

    public string OffsetText
    {
        get
        {
            long ms = (long)Math.Round(_offsetMs);
            return $"Offset: {(ms > 0 ? "+" : "")}{ms} ms";
        }
    }

    // ---- updates from the host ---------------------------------------------------------------------------------------

    /// <summary>Takes the name, readiness and latest heartbeat statistics from the host's snapshot.</summary>
    internal void Apply(ConnectedClient client)
    {
        Name = client.Name;
        if (client.LastStats is { } s)
        {
            State = s.State;
            SyncErrorMs = s.SyncErrorMs;
            LossPct = s.LossPct;
            LatePct = s.LatePct;
            DriftPpm = s.DriftPpm;
            FitResidualMs = s.FitResidualMs;
        }
        else
        {
            State = client.Ready ? SyncState.Syncing : SyncState.Connecting;
            SyncErrorMs = LossPct = LatePct = DriftPpm = FitResidualMs = double.NaN;
        }
    }

    /// <summary>Sets volume, offset and EQ without reporting them as a user change (values the host or the settings store supplied).</summary>
    internal void Adopt(float volume, double offsetMs, EqGains eq)
    {
        _adopting = true;
        try
        {
            Volume = volume;
            OffsetMs = offsetMs;
            Eq.Adopt(eq);
        }
        finally
        {
            _adopting = false;
        }
    }
}
