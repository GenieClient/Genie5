using System;
using System.Collections.Generic;
using System.Linq;
using Genie.App.ViewModels;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #345 — befriend ("Friends &amp; Enemies"), from the 2026-09-03 raw
/// session recording, verbatim (first two rows). The parser used to drop
/// <c>menuLink</c> and <c>menuImage</c> entirely, so only the remove images
/// survived and the window read "crossFace" once per friend with no names.
/// </summary>
public class BefriendDialogTests
{
    private const string Xml =
        "<openDialog type=\"dynamic\" id=\"befriend\" title=\"Friends &amp;&amp; Enemies\" location=\"right\" target=\"befriend\" height=\"165\" resident=\"true\">\n" +
        "<dialogData id=\"befriend\"><menuLink id=\"friend1\" value=\"Dovefour\" exist=\"-10043102\" noun=\"Dovefour\" align=\"nw\" top=\"0\" left=\"0\" width=\"100\"/><menuImage id=\"demeanor1\" name=\"warmFace\" tooltip=\"Warm\" exist=\"-10043102\" noun=\"Dovefour\" align=\"nw\" top=\"0\" left=\"100\" height=\"25\" width=\"25\"/><image id='remove1' name='crossFace' cmd='befriend clear 1' align='nw' top=\"0\" left=\"130\" height=\"25\" width=\"25\" tooltip=\"Remove\" echo=\"befriend clear 1\"/><menuLink id=\"friend2\" value=\"Cyiarriah\" exist=\"-10356746\" noun=\"Cyiarriah\" align=\"nw\" top=\"30\" left=\"0\" width=\"100\"/><menuImage id=\"demeanor2\" name=\"friendlyFace\" tooltip=\"Friendly\" exist=\"-10356746\" noun=\"Cyiarriah\" align=\"nw\" top=\"30\" left=\"100\" height=\"25\" width=\"25\"/><image id='remove2' name='crossFace' cmd='befriend clear 2' align='nw' top=\"30\" left=\"130\" height=\"25\" width=\"25\" tooltip=\"Remove\" echo=\"befriend clear 2\"/></dialogData>\n";

    private static ServerDialogState Feed(string xml)
    {
        var engine = new ServerDialogEngine();
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using (parser.GameEvents.Subscribe(events.Add)) parser.Feed(xml);
        foreach (var e in events)
        {
            if (e is OpenDialogEvent od) engine.Observe(od);
            if (e is DialogDataEvent dd) engine.Observe(dd);
        }
        return engine.Get("befriend")!;
    }

    [Fact]
    public void Names_and_demeanors_are_captured_not_just_the_remove_buttons()
    {
        var state = Feed(Xml);

        Assert.Equal(6, state.Controls.Count);
        Assert.Equal(2, state.Controls.Count(c => c.Type == DialogControlType.MenuLink));
        Assert.Equal(2, state.Controls.Count(c => c.Type == DialogControlType.MenuImage));
    }

    [Fact]
    public void Each_row_reads_name_demeanor_remove()
    {
        var vm = new ServerDialogViewModel("befriend");
        vm.Apply(Feed(Xml));

        var name  = Assert.IsType<DialogLabelViewModel>(vm.Controls.Single(c => c.Id == "friend1"));
        var face  = Assert.IsType<DialogImageViewModel>(vm.Controls.Single(c => c.Id == "demeanor1"));
        var clear = Assert.IsType<DialogImageViewModel>(vm.Controls.Single(c => c.Id == "remove1"));

        Assert.Equal("Dovefour", name.Text);
        Assert.Equal("Warm", face.Label);            // DR's tooltip, not "warmFace"
        Assert.False(face.IsActivatable);
        Assert.Equal("✕", clear.Label);
        Assert.Equal("Remove", clear.Tooltip);
        Assert.True(clear.IsActivatable);

        // One row per friend, three columns.
        Assert.Equal(name.Row, face.Row);
        Assert.Equal(face.Row, clear.Row);
        Assert.True(name.Column < face.Column && face.Column < clear.Column);
    }

    [Fact]
    public void The_remove_button_sends_befriend_clear()
    {
        var vm = new ServerDialogViewModel("befriend");
        var sent = new List<ServerDialogAction>();
        vm.ActionRequested += sent.Add;
        vm.Apply(Feed(Xml));

        vm.Activate("remove2");

        Assert.Equal("befriend clear 2", Assert.Single(sent).Value);
    }

    /// <summary>An image with a tooltip but no known glyph is captioned by the
    /// tooltip; one with neither still falls back to its sprite name.</summary>
    [Fact]
    public void Unknown_faces_read_as_their_tooltip()
    {
        var xml = "<dialogData id=\"befriend\"><menuImage id=\"d9\" name=\"sternFace\" tooltip=\"Stern\" top=\"0\" left=\"100\"/><image id=\"x\" name=\"mysteryFace\" top=\"0\" left=\"130\"/></dialogData>\n";
        var vm = new ServerDialogViewModel("befriend");
        vm.Apply(Feed(xml));

        Assert.Equal("Stern", ((DialogImageViewModel)vm.Controls.Single(c => c.Id == "d9")).Label);
        Assert.Equal("mysteryFace", ((DialogImageViewModel)vm.Controls.Single(c => c.Id == "x")).Label);
    }
}
