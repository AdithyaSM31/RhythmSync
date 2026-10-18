using System.Windows;

namespace RhythmSync.App.Flyout;

/// <summary>Where the tray flyout goes on a monitor (spec §5). Pure: the caller supplies the monitor in device-independent pixels.</summary>
public static class FlyoutPlacement
{
    /// <summary>
    /// Puts the flyout in the working-area corner on the taskbar's edge: the side where <paramref name="workArea"/> is inset
    /// furthest from <paramref name="screenBounds"/> (bottom when it is not inset at all). The corner is the right one for a
    /// bottom or top taskbar and the bottom one for a left or right taskbar, <paramref name="margin"/> away from the edges.
    /// The height is capped at the working area's height less both margins, and the result always lies inside the working area.
    /// </summary>
    /// <returns>The flyout's top-left corner and its (capped) height, in DIPs.</returns>
    public static (Point TopLeft, double Height) Compute(Rect workArea, Rect screenBounds, Size flyout, double margin = 12)
    {
        double height = Math.Max(0, Math.Min(flyout.Height, workArea.Height - 2 * margin));
        double width = flyout.Width;

        double right = workArea.Right - margin - width;
        double bottom = workArea.Bottom - margin - height;
        var (x, y) = TaskbarEdge(workArea, screenBounds) switch
        {
            Edge.Top => (right, workArea.Top + margin),
            Edge.Left => (workArea.Left + margin, bottom),
            _ => (right, bottom),   // bottom and right: the corner above the tray
        };

        // Keep it inside the working area; when it cannot fit, its top-left corner wins.
        x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - width));
        y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - height));
        return (new Point(x, y), height);
    }

    private enum Edge { Bottom, Top, Left, Right }

    private static Edge TaskbarEdge(Rect workArea, Rect screenBounds)
    {
        // Bottom first, so it also wins a tie and the "no inset" case.
        var insets = new[]
        {
            (Edge.Bottom, screenBounds.Bottom - workArea.Bottom),
            (Edge.Top, workArea.Top - screenBounds.Top),
            (Edge.Left, workArea.Left - screenBounds.Left),
            (Edge.Right, screenBounds.Right - workArea.Right),
        };
        var edge = Edge.Bottom;
        double largest = 0;
        foreach (var (side, inset) in insets)
        {
            if (inset > largest)
            {
                largest = inset;
                edge = side;
            }
        }
        return edge;
    }
}
