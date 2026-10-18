using System.Globalization;
using System.Windows.Data;
using RhythmSync.Core.Clock;

namespace RhythmSync.App.Controls;

/// <summary>
/// Binds a <see cref="StatusChip"/>'s Kind to a client's <see cref="SyncState"/> (never the hosting kind).
/// A missing or unknown value shows as not connected. Registered as the resource <c>SyncStateToKind</c>.
/// </summary>
public sealed class SyncStateToKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusKinds.From(value as SyncState?, hosting: false);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
