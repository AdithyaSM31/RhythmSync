using System.Windows;
using System.Windows.Controls;
using RhythmSync.App.ViewModels;

namespace RhythmSync.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    // Enter commits through the box's key binding; leaving the box commits here.
    private void OnNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.CommitNameCommand.Execute(null);
    }
}
