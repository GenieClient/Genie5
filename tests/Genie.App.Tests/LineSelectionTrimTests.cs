using Genie.App.Controls;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Guards the selection re-index behind public #298: the game window stores its
/// cross-line selection as (line, offset) pairs, and the scrollback cap trims
/// lines off the TOP of the buffer — so an un-shifted selection made at line x
/// copies line x+n after n trims. <see cref="LineSelection.ShiftAfterTrim"/> is
/// the pure math the behavior applies on every top-trim.
/// </summary>
public class LineSelectionTrimTests
{
    [Fact]
    public void Selection_below_the_cut_shifts_down_by_the_removed_count()
    {
        var shifted = LineSelection.ShiftAfterTrim((100, 5), (103, 12), removed: 7);

        Assert.NotNull(shifted);
        Assert.Equal((93, 5),  shifted!.Value.Anchor);
        Assert.Equal((96, 12), shifted.Value.Focus);
    }

    [Fact]
    public void Offsets_survive_untouched_because_line_content_does_not_change()
    {
        var shifted = LineSelection.ShiftAfterTrim((10, 42), (10, 61), removed: 3);

        Assert.Equal(42, shifted!.Value.Anchor.Off);
        Assert.Equal(61, shifted.Value.Focus.Off);
    }

    [Fact]
    public void Endpoint_trimmed_off_the_top_clamps_to_the_buffer_start()
    {
        // Anchor at line 2 falls inside the 5 trimmed lines; focus survives.
        var shifted = LineSelection.ShiftAfterTrim((2, 8), (20, 4), removed: 5);

        Assert.Equal((0, 0),  shifted!.Value.Anchor);
        Assert.Equal((15, 4), shifted.Value.Focus);
    }

    [Fact]
    public void Selection_entirely_above_the_cut_is_reported_gone()
    {
        Assert.Null(LineSelection.ShiftAfterTrim((3, 0), (6, 10), removed: 10));
    }

    [Fact]
    public void Endpoint_exactly_at_the_cut_survives_as_the_new_top_line()
    {
        var shifted = LineSelection.ShiftAfterTrim((10, 3), (14, 9), removed: 10);

        Assert.Equal((0, 3), shifted!.Value.Anchor);
        Assert.Equal((4, 9), shifted.Value.Focus);
    }

    [Fact]
    public void Upward_drag_keeps_its_inverted_orientation()
    {
        // Focus above anchor (the user dragged upward); orientation is the
        // behavior's business (it normalizes on render) — the shift must not
        // swap the endpoints.
        var shifted = LineSelection.ShiftAfterTrim((30, 2), (25, 6), removed: 4);

        Assert.Equal((26, 2), shifted!.Value.Anchor);
        Assert.Equal((21, 6), shifted.Value.Focus);
    }

    [Fact]
    public void Zero_removed_is_the_identity()
    {
        var shifted = LineSelection.ShiftAfterTrim((7, 1), (9, 2), removed: 0);

        Assert.Equal((7, 1), shifted!.Value.Anchor);
        Assert.Equal((9, 2), shifted.Value.Focus);
    }
}
