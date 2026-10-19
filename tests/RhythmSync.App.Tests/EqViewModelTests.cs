using System.Globalization;
using RhythmSync.App.ViewModels;
using RhythmSync.Core.Audio.Eq;

namespace RhythmSync.App.Tests;

public class EqViewModelTests
{
    private static readonly EqGains Laptop = EqPresets.All.First(p => p.Name == "Laptop speakers").Gains;
    private static readonly EqGains Bass = EqPresets.All.First(p => p.Name == "Bass boost").Gains;

    private sealed class Counter { public int Count; }

    private static (EqViewModel Vm, List<string?> Props, Counter Changes) Make(EqGains? initial = null)
    {
        var vm = new EqViewModel(initial ?? EqGains.Flat);
        var props = new List<string?>();
        vm.PropertyChanged += (_, e) => props.Add(e.PropertyName);
        var counter = new Counter();
        vm.Changed += () => counter.Count++;
        return (vm, props, counter);
    }

    private static readonly string[] FourNames = { "Flat", "Laptop speakers", "Bass boost", "Treble cut" };
    private static readonly string[] FourNamesAndCustom = { "Flat", "Laptop speakers", "Bass boost", "Treble cut", "Custom" };

    [Fact]
    public void Starts_with_ten_labelled_bands_and_the_given_gains()
    {
        var vm = new EqViewModel(Laptop);

        Assert.Equal(EqBands.Labels, vm.Bands.Select(b => b.Label));
        Assert.Equal(Laptop, vm.Gains);
        Assert.Equal(Laptop.ToArray().Select(g => (double)g), vm.Bands.Select(b => b.GainDb));
        Assert.Equal("Laptop speakers", vm.HeaderText);
        Assert.Equal("Laptop speakers", vm.SelectedPreset);
        Assert.False(vm.IsExpanded);
    }

    [Fact]
    public void Choosing_a_preset_sets_all_bands_and_raises_Changed_once()
    {
        var (vm, props, changes) = Make();

        vm.SelectedPreset = "Bass boost";

        Assert.Equal(Bass, vm.Gains);
        Assert.Equal(1, changes.Count);
        Assert.Equal(Bass.ToArray().Select(g => (double)g), vm.Bands.Select(b => b.GainDb));
        Assert.Equal("Bass boost", vm.HeaderText);
        Assert.Contains(nameof(EqViewModel.PreampText), props);
        Assert.Contains(nameof(EqViewModel.HeaderText), props);
    }

    [Fact]
    public void Choosing_the_preset_already_applied_changes_nothing()
    {
        var (vm, _, changes) = Make(Bass);
        vm.SelectedPreset = "Bass boost";
        Assert.Equal(0, changes.Count);
    }

    [Fact]
    public void Flat_raises_Changed_once()
    {
        var (vm, _, changes) = Make(Laptop);

        vm.FlatCommand.Execute(null);

        Assert.True(vm.Gains.IsFlat);
        Assert.Equal(1, changes.Count);
        Assert.Equal("Flat", vm.HeaderText);
        Assert.All(vm.Bands, b => Assert.Equal(0, b.GainDb));
    }

    [Fact]
    public void Presets_lists_the_four_names()
    {
        var vm = new EqViewModel(EqGains.Flat);
        Assert.Equal(FourNames, vm.Presets);
    }

    [Fact]
    public void Moving_a_band_off_a_preset_shows_Custom_in_the_list_and_header()
    {
        var (vm, props, changes) = Make(Bass);

        vm.Bands[9].GainDb = 3;

        Assert.Equal(1, changes.Count);
        Assert.Equal("Custom", vm.HeaderText);
        Assert.Equal("Custom", vm.SelectedPreset);
        Assert.Equal(FourNamesAndCustom, vm.Presets);
        Assert.Contains(nameof(EqViewModel.SelectedPreset), props);

        vm.Bands[9].GainDb = 0;

        Assert.Equal(2, changes.Count);
        Assert.Equal("Bass boost", vm.HeaderText);
        Assert.Equal("Bass boost", vm.SelectedPreset);
        Assert.Equal(FourNames, vm.Presets);
    }

    [Fact]
    public void Choosing_Custom_is_a_no_op()
    {
        var (vm, _, changes) = Make(Bass);
        vm.Bands[0].GainDb = 1;
        var before = vm.Gains;

        vm.SelectedPreset = "Custom";
        vm.SelectedPreset = "No such preset";
        vm.SelectedPreset = null!;

        Assert.Equal(before, vm.Gains);
        Assert.Equal(1, changes.Count);
    }

    [Fact]
    public void Adopt_raises_nothing()
    {
        var (vm, props, changes) = Make();

        vm.Adopt(Laptop);

        Assert.Equal(0, changes.Count);
        Assert.Equal(Laptop, vm.Gains);
        Assert.Equal("Laptop speakers", vm.HeaderText);
        Assert.Equal(Laptop.ToArray().Select(g => (double)g), vm.Bands.Select(b => b.GainDb));
        Assert.Contains(nameof(EqViewModel.Gains), props);

        vm.Adopt(EqGains.Flat.WithBand(2, 1));
        Assert.Equal(0, changes.Count);
        Assert.Equal(FourNamesAndCustom, vm.Presets);
    }

    [Fact]
    public void IsExpanded_is_plain_and_raises_no_Changed()
    {
        var (vm, props, changes) = Make();
        vm.IsExpanded = true;
        Assert.True(vm.IsExpanded);
        Assert.Contains(nameof(EqViewModel.IsExpanded), props);
        Assert.Equal(0, changes.Count);
    }

    [Fact]
    public void Bands_snap_and_ignore_NaN()
    {
        var (vm, _, changes) = Make();

        vm.Bands[3].GainDb = 2.3;
        Assert.Equal(2.5, vm.Bands[3].GainDb);
        Assert.Equal(2.5, vm.Gains[3]);

        vm.Bands[3].GainDb = double.NaN;
        Assert.Equal(2.5, vm.Bands[3].GainDb);
        Assert.Equal(1, changes.Count);
    }

    [Fact]
    public void Bands_clamp_infinities_and_out_of_range_values()
    {
        var (vm, _, changes) = Make();

        vm.Bands[0].GainDb = double.PositiveInfinity;
        vm.Bands[1].GainDb = double.NegativeInfinity;
        vm.Bands[2].GainDb = 99;

        Assert.Equal(12, vm.Bands[0].GainDb);
        Assert.Equal(-12, vm.Bands[1].GainDb);
        Assert.Equal(12, vm.Bands[2].GainDb);
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void Setting_a_band_to_its_current_value_raises_nothing()
    {
        var (vm, _, changes) = Make();
        vm.Bands[4].GainDb = 0;
        vm.Bands[4].GainDb = 0.2;   // snaps back to 0
        Assert.Equal(0, changes.Count);
    }

    [Fact]
    public void A_band_move_notifies_the_band_and_its_texts()
    {
        var (vm, _, _) = Make();
        var props = new List<string?>();
        vm.Bands[2].PropertyChanged += (_, e) => props.Add(e.PropertyName);

        vm.Bands[2].GainDb = 1.5;

        Assert.Contains(nameof(EqBandViewModel.GainDb), props);
        Assert.Contains(nameof(EqBandViewModel.GainText), props);
        Assert.Contains(nameof(EqBandViewModel.AutomationText), props);
    }

    [Fact]
    public void PreampText_formats_one_decimal()
    {
        var all12 = EqGains.Create(Enumerable.Repeat(12f, EqBands.Count).ToArray());
        Assert.Equal("Preamp -18.4 dB", new EqViewModel(all12).PreampText);
        Assert.Equal("Preamp 0.0 dB", new EqViewModel(EqGains.Flat).PreampText);

        var (vm, props, _) = Make();
        vm.SelectedPreset = "Bass boost";
        Assert.Contains(nameof(EqViewModel.PreampText), props);
        Assert.Equal($"Preamp {EqCurve.PreampDb(Bass, 48000):0.0} dB", vm.PreampText);
    }

    [Fact]
    public void GainText_formats_signs()
    {
        var vm = new EqViewModel(EqGains.Flat);
        var band = vm.Bands[0];

        Assert.Equal("0", band.GainText);
        Assert.Equal("0 dB", band.AutomationText);

        band.GainDb = 2.5;
        Assert.Equal("+2.5", band.GainText);
        Assert.Equal("+2.5 dB", band.AutomationText);

        band.GainDb = -3;
        Assert.Equal("-3", band.GainText);

        band.GainDb = 12;
        Assert.Equal("+12", band.GainText);

        band.GainDb = -0.5;
        Assert.Equal("-0.5", band.GainText);
    }

    [Fact]
    public void Texts_ignore_the_current_culture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var vm = new EqViewModel(EqGains.Flat.WithBand(0, 2.5));
            Assert.Equal("+2.5", vm.Bands[0].GainText);
            Assert.DoesNotContain(",", vm.PreampText);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }
}
