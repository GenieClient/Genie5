using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Events;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #329 — splitting DR's room-objects line into one entry per object
/// for the Objects panel.
///
/// The line carries no per-item markup (verified across every recorded
/// session: prose plus <c>&lt;pushBold/&gt;</c> on creatures, no
/// <c>&lt;a&gt;</c> links), so the split has to run on English list grammar.
/// Every "recorded" case below is a real line pulled from a session capture,
/// which is what pins the two rules that make it work: split the LAST " and "
/// only, and only when the tail opens like a new item.
/// </summary>
public class RoomObjectSplitterTests
{
    private static string[] Split(string content)
        => RoomObjectSplitter.SplitText(content).ToArray();

    // ── Empty / degenerate input ─────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("You also see")]
    [InlineData("You also see ")]
    [InlineData("You also see .")]
    public void Empty_input_yields_no_rows(string? content)
        => Assert.Empty(RoomObjectSplitter.SplitText(content));

    // ── The basic shapes ─────────────────────────────────────────────────

    [Fact]
    public void Single_object_drops_the_lead_in_and_the_period()
        => Assert.Equal(new[] { "a wooden sign" }, Split("You also see a wooden sign."));

    [Fact]
    public void Recorded_two_objects_split_on_the_conjunction()
        => Assert.Equal(new[] { "a stone urn", "a wide arch" },
                        Split("You also see a stone urn and a wide arch."));

    [Fact]
    public void Recorded_comma_list_splits_on_every_separator()
        => Assert.Equal(
            new[]
            {
                "a baked grey clay house",
                "a petite stacked stone house",
                "an old stacked stone shack",
                "a petite stacked stone shanty",
            },
            Split("You also see a baked grey clay house, a petite stacked stone house, " +
                  "an old stacked stone shack and a petite stacked stone shanty."));

    [Fact]
    public void Recorded_long_descriptive_names_survive_intact()
        => Assert.Equal(
            new[]
            {
                "the western gate",
                "a narrow trail",
                "a town wall",
                "a path that heads westward toward the grasslands",
                "a wooden sign printed in large block letters",
            },
            Split("You also see the western gate, a narrow trail, a town wall, a path that " +
                  "heads westward toward the grasslands and a wooden sign printed in large " +
                  "block letters."));

    // ── Rule 1: only the LAST " and " separates ──────────────────────────

    [Fact]
    public void Recorded_name_containing_and_is_not_split_at_its_own_conjunction()
    {
        // The line that kills a naive " and " split: the Collegium's own name
        // contains "and", and it is NOT the list's final conjunction.
        var rows = Split("You also see the firewood peddler Mags, Rartan's Collegium of Inner " +
                         "Juggling and Reflexes and a trodden dirt path.");

        Assert.Equal(new[]
        {
            "the firewood peddler Mags",
            "Rartan's Collegium of Inner Juggling and Reflexes",
            "a trodden dirt path",
        }, rows);
    }

    [Fact]
    public void Recorded_trailing_clause_before_the_conjunction_stays_with_its_item()
        => Assert.Equal(
            new[]
            {
                "a pool of black shadows",
                "some stone stairs leading to the top of the town wall",
                "the Guard House",
            },
            Split("You also see a pool of black shadows, some stone stairs leading to the top " +
                  "of the town wall and the Guard House."));

    // ── Rule 2: the tail must open like a new item ───────────────────────

    [Fact]
    public void Conjunction_inside_a_single_item_name_does_not_split()
    {
        // "pail set" opens with neither a determiner nor a capital, so the
        // conjunction is read as part of the name rather than a separator.
        Assert.Equal(new[] { "a mop and pail set" }, Split("You also see a mop and pail set."));
    }

    [Fact]
    public void Proper_noun_tail_still_splits()
        => Assert.Equal(new[] { "a rose", "Rartan's Collegium" },
                        Split("You also see a rose and Rartan's Collegium."));

    [Theory]
    [InlineData("some stone stairs")]
    [InlineData("several copper coins")]
    [InlineData("an ethereal vela'tohr")]
    [InlineData("two wooden crates")]
    [InlineData("the Guard House")]
    public void Every_determiner_opens_a_new_item(string tail)
        => Assert.Equal(new[] { "a rose", tail }, Split($"You also see a rose and {tail}."));

    // ── List punctuation variants ────────────────────────────────────────

    [Fact]
    public void Oxford_comma_does_not_leave_a_dangling_and()
        => Assert.Equal(new[] { "a rose", "a crate", "a lantern" },
                        Split("You also see a rose, a crate, and a lantern."));

    [Fact]
    public void Missing_trailing_period_is_tolerated()
        => Assert.Equal(new[] { "a rose", "a crate" }, Split("You also see a rose and a crate"));

    // ── Spans index the original string ──────────────────────────────────

    [Fact]
    public void Spans_address_the_untouched_content()
    {
        const string content = "You also see a stone urn and a wide arch.";
        var spans = RoomObjectSplitter.Split(content);

        Assert.Collection(spans,
            s => Assert.Equal("a stone urn", content.Substring(s.Start, s.Length)),
            s => Assert.Equal("a wide arch", content.Substring(s.Start, s.Length)));
        // End is Start+Length and the spans stay in document order.
        Assert.True(spans[0].End <= spans[1].Start);
    }
}

/// <summary>
/// #329 row rendering: which rows are creatures, and what colour they take.
/// Shares the highlight statics collection because ObjectRow tokenizes through
/// the global rule set.
/// </summary>
[Collection("highlight-statics")]
public class ObjectRowTests
{
    private static ObjectRow[] Rows(string content, params BoldSpan[] bold)
        => RoomObjectSplitter.Split(content)
            .Select(s => new ObjectRow(content, s, bold.Length == 0 ? null : bold))
            .ToArray();

    [Fact]
    public void Bolded_creature_rows_are_marked_and_plain_objects_are_not()
    {
        // The recorded line "You also see <b>a Custodian of the Dusk</b> and an
        // ornately arched building." — bold covers the creature only.
        const string content = "You also see a Custodian of the Dusk and an ornately arched building.";
        var bold = new BoldSpan(content.IndexOf("a Custodian", System.StringComparison.Ordinal),
                                "a Custodian of the Dusk".Length);

        var rows = Rows(content, bold);

        Assert.Equal(2, rows.Length);
        Assert.Equal("a Custodian of the Dusk", rows[0].Text);
        Assert.True(rows[0].IsCreature);
        Assert.Equal("an ornately arched building", rows[1].Text);
        Assert.False(rows[1].IsCreature);
    }

    [Fact]
    public void A_line_with_no_bold_marks_nothing_as_a_creature()
    {
        var rows = Rows("You also see a stone urn and a wide arch.");
        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.False(r.IsCreature));
    }

    [Fact]
    public void Two_bolded_creatures_both_mark()
    {
        const string content = "You also see a town guard and a grizzled old war veteran.";
        var rows = Rows(content,
            new BoldSpan(content.IndexOf("a town guard", System.StringComparison.Ordinal), "a town guard".Length),
            new BoldSpan(content.IndexOf("a grizzled", System.StringComparison.Ordinal),
                         "a grizzled old war veteran".Length));

        Assert.Equal(2, rows.Length);
        Assert.All(rows, r => Assert.True(r.IsCreature));
    }
}

/// <summary>
/// #329 end-to-end: ComponentEvents off the parser stack drive the panel's
/// rows. Mirrors the StreamTabsViewModel harness — and relies on the same
/// empirically-confirmed fact documented there, that outside a UI app
/// <c>RxApp.MainThreadScheduler</c> delivers synchronously, so a Publish is
/// observable on the next line.
/// </summary>
[Collection("highlight-statics")]
public class ObjectsViewModelTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public GenieCore Core { get; }
        public ObjectsViewModel Vm { get; } = new();
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_objects_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
            Vm.Attach(Core);
        }

        public void Objs(string content, params BoldSpan[] bold) =>
            Core.PublishGameEventForTests(
                new ComponentEvent("room objs", content, null, bold.Length == 0 ? null : bold));

        public void EnterRoom(string title) =>
            Core.PublishGameEventForTests(new ComponentEvent("room title", title));

        public async ValueTask DisposeAsync()
        {
            await Core.DisposeAsync();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Room_objs_populates_the_rows()
    {
        await using var h = new Harness();
        h.Objs("You also see a stone urn and a wide arch.");

        Assert.Equal(2, h.Vm.Count);
        Assert.False(h.Vm.IsEmpty);
        Assert.Equal(new[] { "a stone urn", "a wide arch" }, h.Vm.Objects.Select(o => o.Text));
        Assert.Equal("You also see a stone urn and a wide arch.", h.Vm.RawText);
    }

    [Fact]
    public async Task An_empty_room_objs_empties_the_panel()
    {
        await using var h = new Harness();
        h.Objs("You also see a stone urn and a wide arch.");
        h.Objs("");

        Assert.Equal(0, h.Vm.Count);
        Assert.True(h.Vm.IsEmpty);
        Assert.Empty(h.Vm.Objects);
    }

    [Fact]
    public async Task Entering_a_new_room_clears_the_previous_rooms_contents()
    {
        // The belt-and-braces path: even if a room block omitted room objs
        // entirely, last room's contents must not linger.
        await using var h = new Harness();
        h.Objs("You also see a stone urn and a wide arch.");

        h.EnterRoom("[Riverhaven, Fisher's Cut]");

        Assert.True(h.Vm.IsEmpty);
        Assert.Empty(h.Vm.Objects);
    }

    [Fact]
    public async Task A_repopulated_room_replaces_rather_than_appends()
    {
        await using var h = new Harness();
        h.Objs("You also see a stone urn and a wide arch.");
        h.EnterRoom("[Riverhaven, Fisher's Cut]");
        h.Objs("You also see a wooden sign.");

        Assert.Equal(new[] { "a wooden sign" }, h.Vm.Objects.Select(o => o.Text));
    }

    // ── Creatures: filtered by default, opt-in via #config objectscreatures ──
    //
    // The reporter's call: Mobs already lists creatures, so the two panels
    // shouldn't repeat each other. Genie 4's behaviour (everything on the
    // line) is still one toggle away.

    private const string WithCreature =
        "You also see a Custodian of the Dusk and an ornately arched building.";

    private static BoldSpan CustodianBold() =>
        new(WithCreature.IndexOf("a Custodian", StringComparison.Ordinal),
            "a Custodian of the Dusk".Length);

    [Fact]
    public async Task Creatures_are_left_out_by_default()
    {
        await using var h = new Harness();
        h.Objs(WithCreature, CustodianBold());

        Assert.Equal(new[] { "an ornately arched building" }, h.Vm.Objects.Select(o => o.Text));
        Assert.Equal(1, h.Vm.Count);
        Assert.False(h.Vm.ShowCreatures);
    }

    [Fact]
    public async Task A_room_with_only_creatures_reads_as_empty()
    {
        await using var h = new Harness();
        const string content = "You also see a town guard.";
        h.Objs(content, new BoldSpan(content.IndexOf("a town guard", StringComparison.Ordinal),
                                     "a town guard".Length));

        Assert.True(h.Vm.IsEmpty);
        Assert.Empty(h.Vm.Objects);
    }

    [Fact]
    public async Task The_panel_toggle_brings_creatures_back_and_marks_them()
    {
        await using var h = new Harness();
        h.Objs(WithCreature, CustodianBold());

        h.Vm.ShowCreatures = true;

        Assert.Collection(h.Vm.Objects,
            r => { Assert.Equal("a Custodian of the Dusk", r.Text);     Assert.True(r.IsCreature); },
            r => { Assert.Equal("an ornately arched building", r.Text); Assert.False(r.IsCreature); });
    }

    [Fact]
    public async Task The_panel_toggle_writes_through_to_config()
    {
        await using var h = new Harness();
        h.Vm.ShowCreatures = true;
        Assert.True(h.Core.Config.ObjectsShowCreatures);

        h.Vm.ShowCreatures = false;
        Assert.False(h.Core.Config.ObjectsShowCreatures);
    }

    [Fact]
    public async Task A_typed_config_command_re_filters_the_live_rows()
    {
        await using var h = new Harness();
        h.Objs(WithCreature, CustodianBold());
        Assert.Single(h.Vm.Objects);

        h.Core.Config.SetSetting("objectscreatures", "on");

        Assert.True(h.Vm.ShowCreatures);
        Assert.Equal(2, h.Vm.Objects.Count);

        h.Core.Config.SetSetting("objectscreatures", "off");

        Assert.False(h.Vm.ShowCreatures);
        Assert.Single(h.Vm.Objects);
    }

    [Fact]
    public async Task A_room_arriving_while_the_toggle_is_on_keeps_its_creatures()
    {
        await using var h = new Harness();
        h.Vm.ShowCreatures = true;
        h.Objs(WithCreature, CustodianBold());

        Assert.Equal(2, h.Vm.Objects.Count);
    }
}
