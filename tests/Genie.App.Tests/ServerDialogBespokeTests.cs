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
/// Public #156 Phase 2 — the bespoke registry, its first entry (another
/// character's injuries, public #345), and clickable streamBox links, which
/// spellChoose depends on. Fixtures are the dialog journal's XML, verbatim,
/// fed through the real parser and engine.
/// </summary>
public class ServerDialogBespokeTests
{
    // The 2026-09-02 journal entry, verbatim.
    private const string OtherInjuriesXml =
        "<openDialog type=\"dynamic\" id=\"injuries-10224090\" title=\"Renucci's Injuries\" location=\"center\" height=\"200\" width=\"190\">\n" +
        "<dialogData id=\"injuries-10224090\"><image id=\"head\" name=\"head\" height=\"0\" width=\"0\"/><image id=\"neck\" name=\"neck\" height=\"0\" width=\"0\"/><image id=\"rightArm\" name=\"rightArm\" height=\"0\" width=\"0\"/><image id=\"leftArm\" name=\"leftArm\" height=\"0\" width=\"0\"/><image id=\"rightLeg\" name=\"rightLeg\" height=\"0\" width=\"0\"/><image id=\"leftLeg\" name=\"Injury1\" cmd=\"transfer Renucci internal left leg\" tooltip=\"transfer internal left leg\" height=\"0\" width=\"0\"/><image id=\"rightHand\" name=\"rightHand\" height=\"0\" width=\"0\"/><image id=\"leftHand\" name=\"leftHand\" height=\"0\" width=\"0\"/><image id=\"chest\" name=\"chest\" height=\"0\" width=\"0\"/><image id=\"abdomen\" name=\"Injury1\" cmd=\"transfer Renucci internal abdomen\" tooltip=\"transfer internal abdomen\" height=\"0\" width=\"0\"/><image id=\"back\" name=\"back\" height=\"0\" width=\"0\"/><image id=\"rightEye\" name=\"rightEye\" height=\"0\" width=\"0\"/><image id=\"leftEye\" name=\"leftEye\" height=\"0\" width=\"0\"/><image id=\"rightFoot\" name=\"rightFoot\" height=\"0\" width=\"0\"/><image id=\"nsys\" name=\"nsys\" height=\"0\" width=\"0\"/></dialogData>\n";

    private const string SpellChooseXml =
        "<openDialog type='dynamic' id='spellChoose' resident='false' title='Choose A New Spell' save='false' location='center' height='460' width='600'>\n" +
        "<dialogData id='spellChoose' clear='t'><streamBox id='spells' top='40' left='15' width='250' height='380' /><streamBox id='spellInfo' top='40' left='20' width='300' height='380' anchor_left='spells' /><closeButton id='chooseSpell' value='Close' width='200' top='-10' left='0' align='s' /></dialogData>\n";

    private static ServerDialogState Feed(string xml, string id)
    {
        var engine = new ServerDialogEngine();
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        using var _ = parser.GameEvents.Subscribe(new Sink(engine));
        parser.Feed(xml);
        return engine.Get(id)!;
    }

    private sealed class Sink(ServerDialogEngine engine) : IObserver<GameEvent>
    {
        public void OnNext(GameEvent e)
        {
            switch (e)
            {
                case OpenDialogEvent od: engine.Observe(od); break;
                case DialogDataEvent dd: engine.Observe(dd); break;
                case DynaStreamEvent ds: engine.Observe(ds); break;
            }
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private static (ServerDialogViewModel Vm, List<ServerDialogAction> Sent) Build(ServerDialogState state)
    {
        var vm   = new ServerDialogViewModel(state.Id);
        var sent = new List<ServerDialogAction>();
        vm.ActionRequested += sent.Add;
        vm.Apply(state);
        return (vm, sent);
    }

    // ── Registry ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("injuries-10224090", true)]
    [InlineData("INJURIES-1",        true)]
    [InlineData("injuries",          false)]   // the player's own — the #18 panel, excluded upstream
    [InlineData("injuries-abc",      false)]
    [InlineData("bank_debt",         false)]
    [InlineData("spellChoose",       false)]   // fixed generically, not bespoke
    public void Only_other_character_injuries_is_bespoke(string id, bool bespoke)
    {
        Assert.Equal(bespoke, ServerDialogOverrides.HasOverride(id));
        Assert.Equal(!bespoke, new ServerDialogViewModel(id).IsGeneric);
    }

    // ── Other character's injuries ───────────────────────────────────────────

    [Fact]
    public void Injured_parts_read_as_wounds_and_can_transfer()
    {
        var (vm, _) = Build(Feed(OtherInjuriesXml, "injuries-10224090"));
        var o = Assert.IsType<OtherInjuriesViewModel>(vm.Bespoke);

        Assert.Equal("Renucci's Injuries", o.Subject);
        var abdomen = o.Parts.Single(p => p.RegionId == "abdomen");
        Assert.Equal(InjuryKind.Wound, abdomen.Cell.Kind);
        Assert.Equal(1, abdomen.Cell.Severity);
        Assert.True(abdomen.CanTransfer);
        Assert.Contains("transfer internal abdomen", abdomen.Tip);

        var head = o.Parts.Single(p => p.RegionId == "head");
        Assert.Equal(InjuryKind.None, head.Cell.Kind);
        Assert.False(head.CanTransfer);

        Assert.Equal(2, o.Injured.Count);
        Assert.True(o.AnyTransfer);
        Assert.False(o.IsEmpty);
    }

    [Fact]
    public void Clicking_a_part_sends_its_transfer_through_the_host()
    {
        var (vm, sent) = Build(Feed(OtherInjuriesXml, "injuries-10224090"));
        var o = (OtherInjuriesViewModel)vm.Bespoke!;

        o.Transfer(o.Parts.Single(p => p.RegionId == "leftLeg"));
        o.Transfer(o.Parts.Single(p => p.RegionId == "head"));   // healthy: no cmd, nothing sent

        var action = Assert.Single(sent);
        Assert.Equal(ServerDialogActionKind.GameCommand, action.Kind);
        Assert.Equal("transfer Renucci internal left leg", action.Value);
    }

    [Fact]
    public void A_healed_part_stops_being_transferable_on_the_next_delta()
    {
        var engine = new ServerDialogEngine();
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        using var _ = parser.GameEvents.Subscribe(new Sink(engine));
        parser.Feed(OtherInjuriesXml);
        var (vm, _) = Build(engine.Get("injuries-10224090")!);

        parser.Feed("<dialogData id=\"injuries-10224090\"><image id=\"abdomen\" name=\"abdomen\" height=\"0\" width=\"0\"/></dialogData>\n");
        vm.Apply(engine.Get("injuries-10224090")!);

        var abdomen = ((OtherInjuriesViewModel)vm.Bespoke!).Parts.Single(p => p.RegionId == "abdomen");
        Assert.Equal(InjuryKind.None, abdomen.Cell.Kind);
        Assert.False(abdomen.CanTransfer);
    }

    // ── spellChoose: sized stream boxes with clickable links ─────────────────

    [Fact]
    public void Stream_boxes_take_the_size_DR_asked_for()
    {
        var (vm, _) = Build(Feed(SpellChooseXml, "spellChoose"));
        var spells = vm.Controls.OfType<DialogStreamViewModel>().Single(s => s.Id == "spells");

        Assert.Equal(250, spells.BoxWidth);
        Assert.Equal(380, spells.BoxHeight);
    }

    [Fact]
    public void Spell_links_in_the_stream_are_clickable_and_send_their_command()
    {
        var xml = SpellChooseXml +
            "<dynaStream id='spells'><d cmd='choose 1'>Fire Shards</d>\n<d cmd='choose 2'>Ethereal Fissure</d>\n</dynaStream>\n";
        var (vm, sent) = Build(Feed(xml, "spellChoose"));
        var spells = vm.Controls.OfType<DialogStreamViewModel>().Single(s => s.Id == "spells");

        var links = spells.Lines.SelectMany(l => l.Segments).Where(s => s.IsLink).ToList();
        Assert.Equal(new[] { "Fire Shards", "Ethereal Fissure" }, links.Select(l => l.Text));

        vm.ActivateStreamLink(links[1].Link!);
        Assert.Equal("choose 2", Assert.Single(sent).Value);
    }

    [Fact]
    public void A_stream_link_cannot_fan_out_into_several_commands()
    {
        var xml = SpellChooseXml + "<dynaStream id='spells'><d cmd='choose 1;drop sword'>Fire Shards</d></dynaStream>\n";
        var (vm, sent) = Build(Feed(xml, "spellChoose"));
        var link = vm.Controls.OfType<DialogStreamViewModel>().Single(s => s.Id == "spells")
                     .Lines.SelectMany(l => l.Segments).Single(s => s.IsLink).Link!;

        vm.ActivateStreamLink(link);

        Assert.Equal("choose 1\\;drop sword", Assert.Single(sent).Value);
    }

    [Fact]
    public void Splitting_keeps_plain_text_around_links_on_each_line()
    {
        var text  = "Pick: Fire Shards now\nnext";
        var links = new[] { new LinkSpan(6, 11, "choose 1") };

        var lines = DialogStreamViewModel.Split(text, links).ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal(new[] { "Pick: ", "Fire Shards", " now" }, lines[0].Segments.Select(s => s.Text));
        Assert.Equal(new[] { false, true, false }, lines[0].Segments.Select(s => s.IsLink));
        Assert.Equal("next", Assert.Single(lines[1].Segments).Text);
    }
}
