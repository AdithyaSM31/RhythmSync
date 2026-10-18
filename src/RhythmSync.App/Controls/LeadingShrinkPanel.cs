using System.Windows;
using System.Windows.Controls;

namespace RhythmSync.App.Controls;

/// <summary>
/// Lays its children out left to right like a horizontal StackPanel, except that the first child gives way: it is measured
/// last, with only the width the other children leave, so a long name trims (with an ellipsis, say) while the chip or
/// button that follows it stays whole and right next to it. Used for "name, then status" rows.
/// </summary>
public sealed class LeadingShrinkPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0)
            return default;

        double restWidth = 0;
        double height = 0;
        for (int i = 1; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            restWidth += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var lead = InternalChildren[0];
        double leadWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - restWidth);
        lead.Measure(new Size(leadWidth, availableSize.Height));
        height = Math.Max(height, lead.DesiredSize.Height);

        return new Size(lead.DesiredSize.Width + restWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }

        return finalSize;
    }
}
