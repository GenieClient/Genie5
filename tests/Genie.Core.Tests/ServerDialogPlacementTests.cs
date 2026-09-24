using Genie.Core.Dialogs;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #156, "Where DR proposes" — reading <c>openDialog</c>'s
/// <c>location</c>/<c>width</c>/<c>height</c> into a first-show placement.
/// Location values are the ones seen in the dialog journal and recordings.
/// </summary>
public class ServerDialogPlacementTests
{
    [Theory]
    [InlineData("right",        ServerDialogPlacementKind.DockRight)]   // injuries, befriend
    [InlineData("left",         ServerDialogPlacementKind.DockLeft)]
    [InlineData("center",       ServerDialogPlacementKind.Float)]       // spellChoose, injuries-<charnum>
    [InlineData("detach",       ServerDialogPlacementKind.Float)]
    [InlineData("force-center", ServerDialogPlacementKind.FloatAlwaysCentered)]   // bank_debt
    [InlineData("Force-Center", ServerDialogPlacementKind.FloatAlwaysCentered)]
    [InlineData(" center ",     ServerDialogPlacementKind.Float)]
    public void KnownLocationsMapToAPlacement(string location, ServerDialogPlacementKind kind)
    {
        Assert.Equal(kind, ServerDialogPlacement.From(location, null, null).Kind);
    }

    /// <summary>No hint, or one we do not understand, keeps the behaviour every
    /// dialog had before hints were read.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("top")]
    [InlineData("statBar")]
    public void AnythingElseDocksRight(string? location)
    {
        Assert.Equal(ServerDialogPlacementKind.DockRight,
                     ServerDialogPlacement.From(location, null, null).Kind);
    }

    [Fact]
    public void SizeHintsAreReadAsPixels()
    {
        var p = ServerDialogPlacement.From("center", "600", "460");
        Assert.Equal(600, p.Width);
        Assert.Equal(460, p.Height);
        Assert.True(p.Floats);
    }

    [Theory]
    [InlineData("50%")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("wide")]
    [InlineData(null)]
    public void UnusableSizesAreIgnoredNotGuessed(string? raw)
    {
        var p = ServerDialogPlacement.From("center", raw, raw);
        Assert.Null(p.Width);
        Assert.Null(p.Height);
    }

    [Fact]
    public void TheDefaultDocksRight()
    {
        Assert.Equal(ServerDialogPlacementKind.DockRight, ServerDialogPlacement.Default.Kind);
        Assert.False(ServerDialogPlacement.Default.Floats);
    }
}
