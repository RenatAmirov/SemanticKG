using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SemanticKG;

public sealed class ChipPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize) => Layout(availableSize.Width, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        const double spacing = 6;
        double x = 0, y = 0, rowHeight = 0, maxWidth = 0;
        foreach (var child in Children)
        {
            if (!arrange)
                child.Measure(new Size(width, double.PositiveInfinity));

            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                y += rowHeight + spacing;
                x = 0;
                rowHeight = 0;
            }

            if (arrange)
                child.Arrange(new Rect(x, y, size.Width, size.Height));

            maxWidth = Math.Max(maxWidth, x + size.Width);
            x += size.Width + spacing;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return new Size(maxWidth, y + rowHeight);
    }
}
