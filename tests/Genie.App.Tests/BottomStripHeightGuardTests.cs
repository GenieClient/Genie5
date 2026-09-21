using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Guards the height floors on the two bottom-docked strips whose children are
/// all IsVisible-bound: the icon-bar chip strip (public #259) and the
/// <c>#statusbar</c> slot row. Both rows can momentarily have EVERY child
/// hidden — the server clears the old posture before setting the new one, and
/// status scripts clear-then-rewrite their slots in a tight loop. An invisible
/// child measures 0, so without a MinHeight the docked strip collapses ~19px
/// and reflows the whole window on every such update, even while the row's own
/// IsVisible (the HasAny collapse linger) is still holding it "visible".
/// </summary>
public class BottomStripHeightGuardTests
{
    /// <summary>Read one of the app's view files by name. Takes a name rather
    /// than hardcoding MainWindow because the strips these tests guard move
    /// between files as markup is extracted for reuse - the icon-bar chips went
    /// to IconBarView with #349 - and the floor has to follow the markup, not
    /// the file it started in.</summary>
    private static string ViewXaml(string fileName)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "Genie.App", "Views", fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(
            $"Could not locate src/Genie.App/Views/{fileName} walking up from {AppContext.BaseDirectory}");
    }

    [Fact]
    public void Statusbar_slot_panel_declares_a_height_floor()
    {
        Assert.Matches(
            new Regex(@"<controls:StatusSlotPanel[^>]*\bMinHeight=""(1[9-9]|[2-9]\d)"""),
            ViewXaml("MainWindow.axaml"));
    }

    [Fact]
    public void Icon_bar_chip_strip_keeps_its_height_floor()
    {
        // The #259 fix: the chip StackPanel directly under the IconBar border.
        // Lives in IconBarView.axaml since #349 extracted the bar so both dock
        // slots could share one copy.
        Assert.Matches(
            new Regex(@"<StackPanel Orientation=""Horizontal"" Spacing=""4"" MinHeight=""(1[9-9]|[2-9]\d)"""),
            ViewXaml("IconBarView.axaml"));
    }
}
