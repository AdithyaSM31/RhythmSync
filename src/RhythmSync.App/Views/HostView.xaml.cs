using System.Windows;
using System.Windows.Controls;
using RhythmSync.App.ViewModels;

namespace RhythmSync.App.Views;

public partial class HostView : UserControl
{
    /// <summary>
    /// The speaker picker shown in the settings card. It belongs to the Host tab's view model (the speakers are the tab's, not the
    /// running host's), so the tab supplies the control and this view only places it.
    /// </summary>
    public static readonly DependencyProperty SpeakerPickerProperty = DependencyProperty.Register(
        nameof(SpeakerPicker), typeof(object), typeof(HostView), new FrameworkPropertyMetadata(null));

    public HostView()
    {
        InitializeComponent();
    }

    /// <summary>Leaving a client's offset box applies what was typed (Enter is a key binding on the box).</summary>
    private void OffsetInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClientRowViewModel row })
            row.CommitOffsetInputCommand.Execute(null);
    }

    public object? SpeakerPicker
    {
        get => GetValue(SpeakerPickerProperty);
        set => SetValue(SpeakerPickerProperty, value);
    }
}
