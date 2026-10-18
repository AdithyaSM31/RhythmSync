using System.Windows;
using System.Windows.Controls;
using RhythmSync.App.ViewModels;

namespace RhythmSync.App.Views;

public partial class ClientView : UserControl
{
    public ClientView()
    {
        InitializeComponent();
    }

    /// <summary>Enter in a complete pairing code joins, exactly as the Join button would (which stays disabled until a host is picked).</summary>
    private void OnPinSubmitted(object sender, RoutedEventArgs e)
    {
        if (DataContext is ClientViewModel { JoinCommand: var join } && join.CanExecute(null))
            join.Execute(null);
    }
}
