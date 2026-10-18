using System.Windows;
using System.Windows.Controls;

namespace RhythmSync.App.Views;

public partial class HostTabView : UserControl
{
    public HostTabView()
    {
        InitializeComponent();

        // The running picker is shown inside HostView's settings card, below HostView's own DataContext (the running host), so it
        // does not inherit this view's. It takes the tab's view model directly instead of finding it through the tree.
        DataContextChanged += (_, e) =>
        {
            if (RunningHost.SpeakerPicker is FrameworkElement picker)
                picker.DataContext = e.NewValue;
        };
    }
}
