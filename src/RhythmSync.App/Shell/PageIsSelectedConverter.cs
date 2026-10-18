using System.Globalization;
using System.Windows.Data;
using RhythmSync.App.ViewModels;

namespace RhythmSync.App.Shell;

/// <summary>
/// Binds a sidebar nav item's IsChecked to <see cref="MainViewModel.SelectedPage"/>: checked when the selected page
/// equals the ConverterParameter, and checking an item selects its page (unchecking selects nothing).
/// </summary>
public sealed class PageIsSelectedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Page page && parameter is Page item && page == item;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is Page item ? item : Binding.DoNothing;
}
