using Avalonia;
using Avalonia.Controls;

namespace Genie.App.Controls;

/// <summary>
/// Layout panel for the <c>#statusbar</c> slot row: every cell is exactly as
/// wide as its content, packed left-to-right in slot order, so the row's
/// footprint follows what is actually presented (rather than Genie 4's
/// StatusStrip spring geometry, where slot 1 always filled the whole strip and
/// slots 2-10 sat pinned to the right edge).
///
/// Child 0 (slot 1) is special-cased for the "one giant un-numbered line"
/// scripts like uber.cmd: it may grow up to all width left over after the
/// other slots have taken their content size, and is measured against that cap
/// so its TextTrimming ellipsis engages instead of pushing slots 2-10 off
/// screen. Slots 2-10 keep measure priority (matching the old star-column
/// behavior); if their combined content overflows the row they clip at the
/// right edge — the parent Border clips.
///
/// Invisible children (empty slots) measure to zero and take no space.
/// </summary>
public sealed class StatusSlotPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double othersWidth = 0, maxHeight = 0;
        var unconstrained = new Size(double.PositiveInfinity, availableSize.Height);
        for (var i = 1; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(unconstrained);
            othersWidth += child.DesiredSize.Width;
            maxHeight = Math.Max(maxHeight, child.DesiredSize.Height);
        }

        var total = othersWidth;
        if (Children.Count > 0)
        {
            var spring = Children[0];
            var cap = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : Math.Max(0, availableSize.Width - othersWidth);
            spring.Measure(new Size(cap, availableSize.Height));
            total += spring.DesiredSize.Width;
            maxHeight = Math.Max(maxHeight, spring.DesiredSize.Height);
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? total : Math.Min(total, availableSize.Width),
            maxHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children)
        {
            var width = child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
