using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RhythmSync.Core.Audio.Eq;

namespace RhythmSync.App.ViewModels;

/// <summary>
/// One slider of an <see cref="EqViewModel"/>: its frequency label and gain in dB. The gain lives in the owning
/// <see cref="EqViewModel"/>; setting it here is a user change and is reported through that view model.
/// </summary>
public sealed class EqBandViewModel : ObservableObject
{
    private readonly EqViewModel _owner;
    private readonly int _index;

    internal EqBandViewModel(EqViewModel owner, int index)
    {
        _owner = owner;
        _index = index;
        Label = EqBands.Labels[index];
    }

    /// <summary>The band's frequency, e.g. "250" or "1k".</summary>
    public string Label { get; }

    /// <summary>Gain in dB, −12..+12 in steps of 0.5. NaN is ignored; infinities and out-of-range values clamp; others snap to the step.</summary>
    public double GainDb
    {
        get => _owner.Gains[_index];
        set
        {
            if (double.IsNaN(value)) return;
            _owner.SetBandByUser(_index, Math.Clamp(value, -EqBands.MaxGainDb, EqBands.MaxGainDb));
        }
    }

    /// <summary>The gain with an explicit sign, e.g. "+2.5", "0" or "-3".</summary>
    public string GainText
    {
        get
        {
            double db = GainDb;
            return db == 0 ? "0" : (db > 0 ? "+" : "") + db.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>What a screen reader announces for the slider value, e.g. "+2.5 dB".</summary>
    public string AutomationText => GainText + " dB";

    /// <summary>Re-reads the gain from the owner (it was changed there, or snapped back after an edit).</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(GainDb));
        OnPropertyChanged(nameof(GainText));
        OnPropertyChanged(nameof(AutomationText));
    }
}
