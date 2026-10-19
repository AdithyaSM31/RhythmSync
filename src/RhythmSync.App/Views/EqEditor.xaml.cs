using System.Windows.Controls;

namespace RhythmSync.App.Views;

/// <summary>The expanded part of an EQ section: preset picker, Flat, the preamp readout and ten band sliders. DataContext: an EqViewModel.</summary>
public partial class EqEditor : UserControl
{
    public EqEditor()
    {
        InitializeComponent();
    }
}
