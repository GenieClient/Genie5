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

    // The window as seen live on 2026-09-28 (public #374): the sprites plus DR's
    // own vitality bar and two buttons. Shapes follow the journal's other
    // progressBar / cmdButton entries.
    private const string OtherInjuriesWithButtonsXml =
        "<openDialog type=\"dynamic\" id=\"injuries-1\" title=\"Renucci's Injuries\" location=\"center\" height=\"200\" width=\"190\">\n" +
        "<dialogData id=\"injuries-1\"><progressBar id=\"health2\" value=\"100\" text=\"HEALTH 100%\" customText=\"t\" top=\"0\" left=\"0\" width=\"140\" height=\"15\"/>" +
        "<cmdButton id=\"xferVit\" value=\"Transfer Vit\" cmd=\"transfer Renucci vitality\" top=\"20\" left=\"150\"/>" +
        "<cmdButton id=\"relink\" value=\"Re-Link\" cmd=\"touch Renucci\" top=\"20\" left=\"240\"/>" +
        "<image id=\"chest\" name=\"Injury1\" cmd=\"transfer Renucci internal chest\" tooltip=\"transfer internal chest\" height=\"0\" width=\"0\"/>" +
        "<image id=\"head\" name=\"head\" height=\"0\" width=\"0\"/></dialogData>\n";

    /// <summary>Public #374: with the generic grid no longer drawn under the
    /// bespoke view, DR's non-sprite controls must be carried by it, or the
    /// empath loses Transfer Vit and Re-Link. Sprites stay out; they ARE the grid.</summary>
    [Fact]
    public void The_dialogs_own_bar_and_buttons_ride_on_the_bespoke_view()
    {
        var (vm, _) = Build(Feed(OtherInjuriesWithButtonsXml, "injuries-1"));
        var o = (OtherInjuriesViewModel)vm.Bespoke!;

        Assert.DoesNotContain(o.Extras, c => c is DialogImageViewModel);
        Assert.Contains(o.Extras, c => c is DialogProgressViewModel);
        var captions = o.Extras.OfType<DialogButtonViewModel>().Select(b => b.Caption).ToList();
        Assert.Contains("Transfer Vit", captions);
        Assert.Contains("Re-Link", captions);
    }

    [Fact]
    public void Clicking_a_carried_button_sends_its_command_through_the_host()
    {
        var (vm, sent) = Build(Feed(OtherInjuriesWithButtonsXml, "injuries-1"));
        var o = (OtherInjuriesViewModel)vm.Bespoke!;

        o.ActivateExtra("xferVit");

        var action = Assert.Single(sent);
        Assert.Equal("transfer Renucci vitality", action.Value);
    }

    // The 2026-09-28 live walk, verbatim from the recording: DR drew Renucci's
    // window with every part healthy (the viewer's display mode was "I Both"
    // and Renucci's wounds were all external), directly under a touch that
    // listed wounds on the left arm, left leg and chest.
    private const string AllHealthyInjuriesXml =
        "<openDialog type=\"dynamic\" id=\"injuries-10224090\" title=\"Renucci's Injuries\" location=\"center\" height=\"200\" width=\"190\">\n" +
        "<dialogData id=\"injuries-10224090\"><image id=\"head\" name=\"head\" height=\"0\" width=\"0\"/><image id=\"neck\" name=\"neck\" height=\"0\" width=\"0\"/><image id=\"rightArm\" name=\"rightArm\" height=\"0\" width=\"0\"/><image id=\"leftArm\" name=\"leftArm\" height=\"0\" width=\"0\"/><image id=\"rightLeg\" name=\"rightLeg\" height=\"0\" width=\"0\"/><image id=\"leftLeg\" name=\"leftLeg\" height=\"0\" width=\"0\"/><image id=\"rightHand\" name=\"rightHand\" height=\"0\" width=\"0\"/><image id=\"leftHand\" name=\"leftHand\" height=\"0\" width=\"0\"/><image id=\"chest\" name=\"chest\" height=\"0\" width=\"0\"/><image id=\"abdomen\" name=\"abdomen\" height=\"0\" width=\"0\"/><image id=\"back\" name=\"back\" height=\"0\" width=\"0\"/><image id=\"rightEye\" name=\"rightEye\" height=\"0\" width=\"0\"/><image id=\"leftEye\" name=\"leftEye\" height=\"0\" width=\"0\"/><image id=\"rightFoot\" name=\"rightFoot\" height=\"0\" width=\"0\"/><image id=\"nsys\" name=\"nsys\" height=\"0\" width=\"0\"/></dialogData>\n";

    private static Genie.Core.Health.PatientHealth Touch(string patient, params (string Region, Genie.Core.Health.InjuryAxis Axis, Genie.Core.Health.WoundSeverity Sev)[] wounds)
    {
        var regions = new Dictionary<string, Genie.Core.Health.RegionInjuries>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in wounds.GroupBy(w => w.Region))
            regions[g.Key] = new Genie.Core.Health.RegionInjuries
            {
                Region = g.Key,
                Axes   = g.ToDictionary(w => w.Axis, w => w.Sev),
            };
        return new Genie.Core.Health.PatientHealth { Patient = patient, Regions = regions, CapturedAt = DateTimeOffset.UtcNow };
    }

    private static readonly Genie.Core.Health.PatientHealth RenucciTouch = Touch("Renucci",
        ("leftArm", Genie.Core.Health.InjuryAxis.FreshExternal, Genie.Core.Health.WoundSeverity.Negligible),
        ("leftLeg", Genie.Core.Health.InjuryAxis.FreshExternal, Genie.Core.Health.WoundSeverity.Insignificant),
        ("chest",   Genie.Core.Health.InjuryAxis.FreshExternal, Genie.Core.Health.WoundSeverity.Insignificant));

    /// <summary>The walk's bug: the window said "No injuries." under a touch
    /// listing three. A touch reading for the same patient fills in the parts
    /// DR left blank, shown as wounds but not clickable (DR sent no cmd).</summary>
    [Fact]
    public void A_touch_reading_fills_the_parts_the_dialog_left_blank()
    {
        var (vm, sent) = Build(Feed(AllHealthyInjuriesXml, "injuries-10224090"));
        var o = (OtherInjuriesViewModel)vm.Bespoke!;
        Assert.True(o.IsEmpty);                       // DR's dialog alone: nothing

        o.ApplyReading(RenucciTouch);

        Assert.Equal("Renucci", o.Patient);
        Assert.False(o.IsEmpty);
        foreach (var id in new[] { "leftArm", "leftLeg", "chest" })
        {
            var part = o.Parts.Single(p => p.RegionId == id);
            Assert.Equal(InjuryKind.Wound, part.Cell.Kind);
            Assert.True(part.FromReading);
            Assert.False(part.CanTransfer);
        }
        Assert.Equal(InjuryKind.None, o.Parts.Single(p => p.RegionId == "head").Cell.Kind);
        Assert.Equal(3, o.Injured.Count);
        Assert.All(o.Injured, line => Assert.Contains("from your touch", line));

        o.Transfer(o.Parts.Single(p => p.RegionId == "chest"));   // no cmd from DR: nothing sent
        Assert.Empty(sent);
        Assert.False(o.AnyTransfer);
    }

    /// <summary>The reading usually lands AFTER the dialog (DR sends the
    /// dialogData first); a reading already on hand is used on the first render.</summary>
    [Fact]
    public void A_reading_already_on_hand_is_used_when_the_dialog_arrives()
    {
        var vm = new ServerDialogViewModel("injuries-10224090");
        var o  = (OtherInjuriesViewModel)vm.Bespoke!;
        o.AttachReadings(p => p == "Renucci" ? RenucciTouch : null,
                         System.Reactive.Linq.Observable.Empty<Genie.Core.Health.PatientHealth>());

        vm.Apply(Feed(AllHealthyInjuriesXml, "injuries-10224090"));

        Assert.Equal(InjuryKind.Wound, o.Parts.Single(p => p.RegionId == "leftArm").Cell.Kind);
    }

    [Fact]
    public void A_reading_for_someone_else_is_ignored()
    {
        var (vm, _) = Build(Feed(AllHealthyInjuriesXml, "injuries-10224090"));
        var o = (OtherInjuriesViewModel)vm.Bespoke!;

        o.ApplyReading(Touch("Naper", ("leftArm", Genie.Core.Health.InjuryAxis.FreshExternal, Genie.Core.Health.WoundSeverity.Severe)));

        Assert.True(o.IsEmpty);
    }

    /// <summary>DR's own marking wins: a part DR marked transferable keeps its
    /// cmd and its severity even when a touch says something else.</summary>
    [Fact]
    public void The_dialogs_own_marking_wins_over_the_reading()
    {
        var (vm, sent) = Build(Feed(OtherInjuriesXml, "injuries-10224090"));
        var o = (OtherInjuriesViewModel)vm.Bespoke!;

        o.ApplyReading(Touch("Renucci",
            ("abdomen", Genie.Core.Health.InjuryAxis.FreshExternal, Genie.Core.Health.WoundSeverity.Useless),
            ("head",    Genie.Core.Health.InjuryAxis.ScarExternal,  Genie.Core.Health.WoundSeverity.Minor)));

        var abdomen = o.Parts.Single(p => p.RegionId == "abdomen");
        Assert.Equal(1, abdomen.Cell.Severity);       // DR's Injury1, not the touch's "useless"
        Assert.True(abdomen.CanTransfer);
        Assert.False(abdomen.FromReading);

        var head = o.Parts.Single(p => p.RegionId == "head");
        Assert.Equal(InjuryKind.Scar, head.Cell.Kind); // DR left it blank: the touch fills it
        Assert.True(head.FromReading);

        o.Transfer(abdomen);
        Assert.Equal("transfer Renucci internal abdomen", Assert.Single(sent).Value);
    }

    [Theory]
    [InlineData(Genie.Core.Health.WoundSeverity.Insignificant, 1)]
    [InlineData(Genie.Core.Health.WoundSeverity.MoreThanMinor, 1)]
    [InlineData(Genie.Core.Health.WoundSeverity.Harmful, 2)]
    [InlineData(Genie.Core.Health.WoundSeverity.VeryDamaging, 2)]
    [InlineData(Genie.Core.Health.WoundSeverity.Severe, 3)]
    [InlineData(Genie.Core.Health.WoundSeverity.Useless, 3)]
    public void The_thirteen_rungs_fold_onto_three_sprite_levels(Genie.Core.Health.WoundSeverity s, int level)
        => Assert.Equal(level, OtherInjuriesViewModel.SpriteLevel(s));

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
