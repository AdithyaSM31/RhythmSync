using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RhythmSync.Core.Audio.Eq;

namespace RhythmSync.App.ViewModels;

/// <summary>
/// The EQ of one device: ten band sliders, the preset list and a Flat command. A change made by the user (one band
/// move, one preset pick, one Flat) raises <see cref="Changed"/> once; values taken from the host or the settings store
/// go through <see cref="Adopt"/> and raise nothing. Runs on the UI thread.
/// </summary>
public sealed partial class EqViewModel : ObservableObject
{
    /// <summary>Sample rate the preamp readout is computed at; the real rate only matters at the top band.</summary>
    private const double PreampSampleRate = 48000;

    private EqGains _gains;
    private string _preampText;

    public EqViewModel(EqGains initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _gains = initial;

        var bands = new EqBandViewModel[EqBands.Count];
        for (int i = 0; i < bands.Length; i++) bands[i] = new EqBandViewModel(this, i);
        Bands = Array.AsReadOnly(bands);

        Presets = new ObservableCollection<string>(EqPresets.All.Select(p => p.Name));
        FlatCommand = new RelayCommand(() => Apply(EqGains.Flat, byUser: true));

        _preampText = FormatPreamp(initial);
        SyncCustomEntry();
    }

    /// <summary>Raised on the UI thread once per user change, after the new gains are in place.</summary>
    public event Action? Changed;

    public IReadOnlyList<EqBandViewModel> Bands { get; }

    /// <summary>The current curve.</summary>
    public EqGains Gains => _gains;

    /// <summary>The preset names, plus "Custom" at the end only while the curve matches no preset.</summary>
    public ObservableCollection<string> Presets { get; }

    /// <summary>The matching preset's name, or "Custom". Setting a preset name applies it as one change; "Custom" does nothing.</summary>
    public string SelectedPreset
    {
        get => EqPresets.NameOf(_gains) ?? EqPresets.CustomName;
        set
        {
            if (value is null) return;
            foreach (var (name, gains) in EqPresets.All)
            {
                if (name != value) continue;
                Apply(gains, byUser: true);
                return;
            }
        }
    }

    /// <summary>Sets every band to 0 dB (one user change).</summary>
    public IRelayCommand FlatCommand { get; }

    /// <summary>The preset name for the collapsed header, or "Custom".</summary>
    public string HeaderText => SelectedPreset;

    /// <summary>The preamp the curve needs to avoid clipping, e.g. "Preamp -3.5 dB".</summary>
    public string PreampText => _preampText;

    /// <summary>Whether the band sliders are shown. A view detail: never persisted and never a user change.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Takes a curve the host or the settings store supplied, without reporting a user change.</summary>
    public void Adopt(EqGains gains)
    {
        ArgumentNullException.ThrowIfNull(gains);
        Apply(gains, byUser: false);
    }

    /// <summary>A slider moved: the band is set (already clamped by the band), and the slider is told what it really holds.</summary>
    internal void SetBandByUser(int band, double db)
    {
        Apply(_gains.WithBand(band, db), byUser: true);
        Bands[band].Refresh();   // a value that snapped back to the old one changed nothing, but the slider must follow
    }

    private void Apply(EqGains gains, bool byUser)
    {
        if (gains == _gains) return;
        _gains = gains;
        foreach (var band in Bands) band.Refresh();
        _preampText = FormatPreamp(gains);
        SyncCustomEntry();
        OnPropertyChanged(nameof(Gains));
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(PreampText));
        if (byUser) Changed?.Invoke();
    }

    /// <summary>"Custom" is listed only while the curve matches no preset.</summary>
    private void SyncCustomEntry()
    {
        bool custom = EqPresets.NameOf(_gains) is null;
        bool listed = Presets.Count > EqPresets.All.Count;
        if (custom && !listed) Presets.Add(EqPresets.CustomName);
        else if (!custom && listed) Presets.RemoveAt(Presets.Count - 1);
    }

    private static string FormatPreamp(EqGains gains)
    {
        // "+ 0.0" turns a rounded -0.0 into 0.0 so a negligible preamp never reads "-0.0".
        double db = Math.Round(EqCurve.PreampDb(gains, PreampSampleRate), 1, MidpointRounding.AwayFromZero) + 0.0;
        return string.Create(CultureInfo.InvariantCulture, $"Preamp {db:0.0} dB");
    }
}
