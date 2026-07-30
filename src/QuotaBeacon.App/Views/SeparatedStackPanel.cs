using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuotaBeacon.App.Theming;

namespace QuotaBeacon.App.Views;

/// <summary>
/// A vertical stack that draws a hairline between children.
/// </summary>
/// <remarks>
/// Separation is painted rather than inserted as sibling elements, so the item template stays free of
/// a conditional "is this the last one" border and no separator can be left dangling under the final
/// row. Drawing at a device-pixel width keeps the line from blurring on scaled displays.
/// </remarks>
public sealed class SeparatedStackPanel : StackPanel
{
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (InternalChildren.Count < 2)
        {
            return;
        }

        if (TryFindResource(ThemeResources.Hairline) is not Brush brush)
        {
            return;
        }

        var thickness = 1 / VisualTreeHelper.GetDpi(this).DpiScaleY;
        var pen = new Pen(brush, thickness);
        pen.Freeze();

        var offset = 0d;

        for (var index = 0; index < InternalChildren.Count - 1; index++)
        {
            offset += InternalChildren[index].RenderSize.Height;

            var y = Math.Round(offset * VisualTreeHelper.GetDpi(this).DpiScaleY)
                / VisualTreeHelper.GetDpi(this).DpiScaleY;

            drawingContext.DrawLine(pen, new Point(0, y), new Point(RenderSize.Width, y));
        }
    }
}
