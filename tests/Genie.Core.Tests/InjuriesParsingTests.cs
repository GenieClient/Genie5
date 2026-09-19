using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public issue #18 — the server pushes per-region injury state via
/// <c>&lt;dialogData id="injuries"&gt;&lt;image id="rightLeg" name="Injury1"/&gt;…</c>.
/// A healthy region echoes its own id as the image name; wounds arrive as
/// <c>Injury&lt;N&gt;</c> and scars as <c>Scar&lt;N&gt;</c>. Raw formats verbatim
/// from live captures (raw_session_20260521_190130.xml, naper_run0.xml).
/// </summary>
public class InjuriesParsingTests
{
    private static List<GameEvent> Feed(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        foreach (var chunk in chunks) parser.Feed(chunk);
        return events;
    }

    // Minimal IObserver so the test project needn't reference System.Reactive
    // just for the Subscribe(Action<T>) extension.
    private sealed class Collector : IObserver<GameEvent>
    {
        private readonly List<GameEvent> _sink;
        public Collector(List<GameEvent> sink) => _sink = sink;
        public void OnNext(GameEvent e) => _sink.Add(e);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    // The injured block captured live: right leg / left hand / abdomen carry
    // fresh level-1 wounds, everything else healthy.
    private const string InjuredBlock =
        "<dialogData id=\"injuries\"><image id=\"head\" name=\"head\" height=\"0\" width=\"0\"/>" +
        "<image id=\"neck\" name=\"neck\" height=\"0\" width=\"0\"/>" +
        "<image id=\"rightArm\" name=\"rightArm\" height=\"0\" width=\"0\"/>" +
        "<image id=\"leftArm\" name=\"leftArm\" height=\"0\" width=\"0\"/>" +
        "<image id=\"rightLeg\" name=\"Injury1\" height=\"0\" width=\"0\"/>" +
        "<image id=\"leftLeg\" name=\"leftLeg\" height=\"0\" width=\"0\"/>" +
        "<image id=\"rightHand\" name=\"rightHand\" height=\"0\" width=\"0\"/>" +
        "<image id=\"leftHand\" name=\"Injury1\" height=\"0\" width=\"0\"/>" +
        "<image id=\"chest\" name=\"chest\" height=\"0\" width=\"0\"/>" +
        "<image id=\"abdomen\" name=\"Injury1\" height=\"0\" width=\"0\"/>" +
        "<image id=\"back\" name=\"back\" height=\"0\" width=\"0\"/>" +
        "<image id=\"rightEye\" name=\"rightEye\" height=\"0\" width=\"0\"/>" +
        "<image id=\"leftEye\" name=\"leftEye\" height=\"0\" width=\"0\"/>" +
        "<image id=\"rightFoot\" name=\"rightFoot\" height=\"0\" width=\"0\"/>" +
        "<image id=\"nsys\" name=\"nsys\" height=\"0\" width=\"0\"/></dialogData>";

    [Fact]
    public void InjuredBlock_EmitsOneEventPerRegion()
    {
        var injuries = Feed(InjuredBlock).OfType<InjuryEvent>().ToList();

        Assert.Equal(15, injuries.Count);
        Assert.Equal(3, injuries.Count(i => i.Kind == InjuryKind.Wound));
        Assert.Equal(12, injuries.Count(i => i.Kind == InjuryKind.None));
    }

    [Fact]
    public void Wound_CarriesSeverityFromImageName()
    {
        var injuries = Feed(InjuredBlock).OfType<InjuryEvent>().ToList();

        var rightLeg = Assert.Single(injuries, i => i.Area == "rightLeg");
        Assert.Equal(InjuryKind.Wound, rightLeg.Kind);
        Assert.Equal(1, rightLeg.Severity);
    }

    [Fact]
    public void HealthyRegion_EmitsNoneWithZeroSeverity()
    {
        var injuries = Feed(InjuredBlock).OfType<InjuryEvent>().ToList();

        var head = Assert.Single(injuries, i => i.Area == "head");
        Assert.Equal(InjuryKind.None, head.Kind);
        Assert.Equal(0, head.Severity);
    }

    // Kind passed as string (not InjuryKind) — xunit's discovery-time theory
    // serialization can't load Genie.Core to materialize the enum argument.
    // Severity 1–3 for all kinds; nsys uses the Nsys<N> names and can't say
    // wound-vs-scar (both verified against Lich 5 xmlparser.rb).
    [Theory]
    [InlineData("Scar1", nameof(InjuryKind.Scar), 1)]
    [InlineData("Scar3", nameof(InjuryKind.Scar), 3)]
    [InlineData("Injury2", nameof(InjuryKind.Wound), 2)]
    [InlineData("Injury3", nameof(InjuryKind.Wound), 3)]
    [InlineData("Nsys1", nameof(InjuryKind.Damage), 1)]
    [InlineData("Nsys3", nameof(InjuryKind.Damage), 3)]
    [InlineData("Nsys0", nameof(InjuryKind.None), 0)]
    public void ImageName_MapsToKindAndSeverity(string imageName, string kindName, int severity)
    {
        var kind = Enum.Parse<InjuryKind>(kindName);
        var raw = $"<dialogData id=\"injuries\"><image id=\"chest\" name=\"{imageName}\" height=\"0\" width=\"0\"/></dialogData>";

        var evt = Assert.Single(Feed(raw).OfType<InjuryEvent>());
        Assert.Equal("chest", evt.Area);
        Assert.Equal(kind, evt.Kind);
        Assert.Equal(severity, evt.Severity);
    }

    [Fact]
    public void Block_SplitAcrossFeeds_StillParses()
    {
        // GameConnection chunks at arbitrary boundaries — split mid-block.
        var injuries = Feed(
            InjuredBlock[..200],
            InjuredBlock[200..]).OfType<InjuryEvent>().ToList();

        Assert.Equal(15, injuries.Count);
    }

    [Fact]
    public void ImageOutsideInjuriesDialog_EmitsNothing()
    {
        var events = Feed("<image id=\"head\" name=\"Injury3\" height=\"0\" width=\"0\"/>");

        Assert.Empty(events.OfType<InjuryEvent>());
        Assert.Empty(events.OfType<UnknownTagEvent>());
    }

    [Fact]
    public void ImageAfterDialogClose_EmitsNothing()
    {
        var raw = "<dialogData id=\"injuries\"><image id=\"head\" name=\"head\" height=\"0\" width=\"0\"/></dialogData>" +
                  "<image id=\"stray\" name=\"Injury1\" height=\"0\" width=\"0\"/>";

        var injuries = Feed(raw).OfType<InjuryEvent>().ToList();

        Assert.Single(injuries);
        Assert.Equal("head", injuries[0].Area);
    }

    [Fact]
    public void SelfClosingInjuriesDialog_DoesNotOpenContext()
    {
        var raw = "<dialogData id=\"injuries\"/>" +
                  "<image id=\"head\" name=\"Injury1\" height=\"0\" width=\"0\"/>";

        Assert.Empty(Feed(raw).OfType<InjuryEvent>());
    }

    [Fact]
    public void OtherDialogData_StillDropped_AndClosesInjuriesContext()
    {
        // The skin/progressBar companion block the server sends right after the
        // image list (from the live capture). The progressBar child has always
        // emitted through the generic vitals path; the skin stays dropped and
        // no injury events fire.
        var raw = "<dialogData id=\"injuries\"><image id=\"head\" name=\"head\" height=\"0\" width=\"0\"/></dialogData>" +
                  "<dialogData id=\"combat\"><image id=\"head\" name=\"Injury1\" height=\"0\" width=\"0\"/></dialogData>";

        var injuries = Feed(raw).OfType<InjuryEvent>().ToList();

        Assert.Single(injuries);       // only the block with id="injuries"
        Assert.Equal("head", injuries[0].Area);
        Assert.Equal(InjuryKind.None, injuries[0].Kind);
    }

    [Fact]
    public void InjuriesBlock_LeaksNoTextEvents()
    {
        Assert.Empty(Feed(InjuredBlock).OfType<TextEvent>());
    }

    [Fact]
    public void Engine_TracksInjuriesInGameState()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var state  = new Genie.Core.Models.GameState();
        using var engine = new Genie.Core.GameState.GameStateEngine(
            parser.GameEvents, state, NullLogger<Genie.Core.GameState.GameStateEngine>.Instance);

        parser.Feed(InjuredBlock);

        Assert.Equal(15, state.Injuries.Count);
        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.Wound, 1), state.Injuries["rightLeg"]);
        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.None, 0), state.Injuries["head"]);

        // A later healthy reading overwrites the wound in place.
        parser.Feed("<dialogData id=\"injuries\"><image id=\"rightLeg\" name=\"rightLeg\" height=\"0\" width=\"0\"/></dialogData>");
        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.None, 0), state.Injuries["rightLeg"]);
    }

    // ── `health` verb nerve refinement + silent poll window ──────────────────

    [Theory]
    [InlineData("You have a case of uncontrollable convulsions.", nameof(InjuryKind.Wound), 3)]
    [InlineData("You have a case of sporadic convulsions.",       nameof(InjuryKind.Wound), 2)]
    [InlineData("You have a strange case of muscle twitching.",   nameof(InjuryKind.Wound), 1)]
    [InlineData("You have a very difficult time with muscle control.", nameof(InjuryKind.Scar), 3)]
    [InlineData("You have constant muscle spasms.",               nameof(InjuryKind.Scar), 2)]
    [InlineData("You have developed slurred speech.",             nameof(InjuryKind.Scar), 1)]
    public void HealthNerveLine_RefinesNsysReading(string line, string kindName, int severity)
    {
        var events = Feed(line + "\n");

        var nsys = Assert.Single(events.OfType<InjuryEvent>());
        Assert.Equal("nsys", nsys.Area);
        Assert.Equal(Enum.Parse<InjuryKind>(kindName), nsys.Kind);
        Assert.Equal(severity, nsys.Severity);

        // User-typed health: the line itself still displays.
        Assert.Contains(events.OfType<TextEvent>(), t => t.Text == line);
    }

    [Fact]
    public void NervePhrase_InSpeech_DoesNotFire()
    {
        // Quoted speech doesn't start with "You", so no false positive.
        var events = Feed("Renucci says, \"You'd swear I have constant muscle spasms.\"\n");
        Assert.Empty(events.OfType<InjuryEvent>());
    }

    private const string SilentHealthResponse =
        "<output class=\"mono\"/>" +
        "You have a few nicks and scratches.\n" +
        "You have a strange case of muscle twitching.\n" +
        "<output class=\"\"/>";

    [Fact]
    public void SilentWindow_SuppressesHealthResponse_ButStillRefinesNsys()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));

        parser.BeginSilentHealthWindow();
        parser.Feed(SilentHealthResponse);
        parser.Feed("A sleazy lout swings a cudgel at you!\n");

        // The polled response is invisible: no text, no output-class brackets.
        Assert.Empty(events.OfType<TextEvent>().Where(t => t.Text.StartsWith("You have")));
        Assert.Empty(events.OfType<OutputClassEvent>());

        // …but its nerve line still refined the nsys reading.
        var nsys = Assert.Single(events.OfType<InjuryEvent>());
        Assert.Equal(InjuryKind.Wound, nsys.Kind);
        Assert.Equal(1, nsys.Severity);

        // The window closed with the response — later text flows normally.
        Assert.Contains(events.OfType<TextEvent>(), t => t.Text.StartsWith("A sleazy lout"));
    }

    [Fact]
    public void UnarmedMonoBlock_FlowsNormally()
    {
        var events = Feed(SilentHealthResponse);

        Assert.Contains(events.OfType<TextEvent>(), t => t.Text == "You have a few nicks and scratches.");
        Assert.Equal(2, events.OfType<OutputClassEvent>().Count());
    }

    [Fact]
    public void SilentWindow_ExpiresWithoutResponse()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));

        // Arm with an already-expired window: the mono bracket must NOT open
        // suppression, so the block displays like normal output.
        parser.BeginSilentHealthWindow(TimeSpan.FromSeconds(-1));
        parser.Feed(SilentHealthResponse);

        Assert.Contains(events.OfType<TextEvent>(), t => t.Text == "You have a few nicks and scratches.");
    }

    // ── `health` summary clears a stale panel (post-death staleness) ─────────
    // DR pushes the injuries dialog only while the character is conscious, so
    // the wound wipe that comes with a resurrection is never announced and the
    // panel would keep showing what the character died with. The `health`
    // report's summary line is authoritative for WHICH regions are injured:
    // every region it does not name reads healthy. Lines verbatim from the
    // 2026-09-18 session (death by lightning → depart → resurrection).

    // Renucci's readings at the moment of death.
    private const string DiedWithBlock =
        "<dialogData id=\"injuries\">" +
        "<image id=\"leftArm\" name=\"Injury1\" height=\"0\" width=\"0\"/>" +
        "<image id=\"chest\" name=\"Injury1\" height=\"0\" width=\"0\"/>" +
        "<image id=\"leftLeg\" name=\"Injury1\" height=\"0\" width=\"0\"/>" +
        "<image id=\"nsys\" name=\"Nsys3\" height=\"0\" width=\"0\"/></dialogData>";

    private const string AllClearReport =
        "Your body feels in very bad shape!\n" +
        "Your spirit feels empty.\n" +
        "You are fatigued.\n" +
        "You have no significant injuries.\n";

    private static Genie.Core.Models.GameState FeedToState(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var state  = new Genie.Core.Models.GameState();
        using var engine = new Genie.Core.GameState.GameStateEngine(
            parser.GameEvents, state, NullLogger<Genie.Core.GameState.GameStateEngine>.Instance);
        foreach (var chunk in chunks) parser.Feed(chunk);
        return state;
    }

    [Fact]
    public void HealthAllClear_ClearsEveryRegion_IncludingNsys()
    {
        var state = FeedToState(DiedWithBlock, AllClearReport);

        Assert.All(state.Injuries.Values, r => Assert.Equal(InjuryKind.None, r.Kind));
        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.None, 0), state.Injuries["leftArm"]);
        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.None, 0), state.Injuries["nsys"]);

        // The cleared areas must be the dialog's own region ids — a typo here
        // would quietly add a phantom key and leave the real region stale.
        Assert.Equal(
            new[] { "abdomen", "back", "chest", "head", "leftArm", "leftEye", "leftFoot",
                    "leftHand", "leftLeg", "neck", "nsys", "rightArm", "rightEye",
                    "rightFoot", "rightHand", "rightLeg" },
            state.Injuries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void HealthSummary_ClearsOnlyTheRegionsItDoesNotName()
    {
        var state = FeedToState(
            DiedWithBlock,
            "Your body feels slightly battered.\n" +
            "Your spirit feels full of life.\n" +
            "You are tired.\n" +
            "You have some minor abrasions to the left arm.\n");

        // Named → the dialog's reading stands; unnamed → healthy.
        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.Wound, 1), state.Injuries["leftArm"]);
        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.None, 0),  state.Injuries["chest"]);
        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.None, 0),  state.Injuries["leftLeg"]);
    }

    [Fact]
    public void HealthSummary_LeavesNsysToTheNerveScan()
    {
        // Nerve damage is described as whole-body / skin wording, which must
        // not be read as "nsys unmentioned".
        var state = FeedToState(
            DiedWithBlock,
            "Your body feels at death's door!\n" +
            "You have some minor abrasions to the chest, complete paralysis of the " +
            "entire body compounded by open and bleeding sores all over the skin.\n");

        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.Damage, 3), state.Injuries["nsys"]);
        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.Wound, 1),  state.Injuries["chest"]);
        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.None, 0),   state.Injuries["leftArm"]);
    }

    [Fact]
    public void YouHaveLine_OutsideAHealthReport_ClearsNothing()
    {
        // No "Your body feels" first line → not a report, so a stray line that
        // happens to name a body part can never wipe the panel.
        var state = FeedToState(
            DiedWithBlock,
            "You have a firm grip on the left arm of the chair.\n");

        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.Wound, 1),  state.Injuries["chest"]);
        Assert.Equal(new Genie.Core.Models.InjuryReading(InjuryKind.Damage, 3), state.Injuries["nsys"]);
    }

    [Fact]
    public void HealthReport_SkipsInterleavedYouHaveLine_AndStillApplies()
    {
        // Text can land between the report's first line and its summary; a
        // line that names no region leaves the window open for the real one.
        var state = FeedToState(
            DiedWithBlock,
            "Your body feels in extremely bad shape!\n" +
            "You have gained favor with your god.\n" +
            "You have no significant injuries.\n");

        Assert.All(state.Injuries.Values, r => Assert.Equal(InjuryKind.None, r.Kind));
    }

    [Fact]
    public void HealthReport_AllClearLine_StillDisplays()
    {
        // The clear is a side effect — a user-typed `health` still prints.
        var events = Feed(AllClearReport);
        Assert.Contains(events.OfType<TextEvent>(), t => t.Text == "You have no significant injuries.");
    }

    [Fact]
    public void HealthProgressBar_InsideInjuriesDialog_StillEmitsVitals()
    {
        var raw = "<dialogData id=\"injuries\">" +
                  "<skin id=\"healthSkin\" name=\"healthBar2\" controls=\"health2\" align=\"n\" top=\"160\" width=\"140\" left=\"0\" height=\"15\"/>" +
                  "<progressBar id=\"health2\" value=\"100\" text=\"HEALTH 100%\" customText=\"t\" align=\"n\" top=\"160\" width=\"140\" left=\"0\" height=\"15\"/>" +
                  "</dialogData>";

        var bars = Feed(raw).OfType<ProgressBarEvent>().ToList();

        var health = Assert.Single(bars);
        Assert.Equal("health2", health.BarId);
        Assert.Equal(100, health.Value);
    }
}
