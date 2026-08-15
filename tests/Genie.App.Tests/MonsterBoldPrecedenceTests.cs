using System;
using System.Collections.Generic;
using Avalonia.Media;
using Genie.App.Highlighting;
using Genie.App.ViewModels;
using Genie.Core.Events;
using Genie.Core.Highlights;
using Genie.Core.Presets;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #235 + #236 — MonsterBold precedence and colour source.
///
/// <para>#235: Genie 3/4 render MonsterBold (DR's &lt;pushBold&gt; on creature
/// names and Events messages) in a colour that PRE-EMPTS normal highlights — a
/// cosmetic rule must never disguise a creature or an Events line. G5's layer
/// order had MonsterBold below user highlights, so a rule matching the same
/// characters (the reporter's "You also see" line highlight) claimed them
/// first. Order is now: names (supreme) → MonsterBold → user rules → built-in
/// defaults → presets, and MonsterBold writes foreground only, so a user
/// rule's background still shows beneath it.</para>
///
/// <para>#236: the MonsterBold colour is the <c>creatures</c> preset — and it
/// is the ONE source: the Mobs panel's default row foreground
/// (<see cref="MobItem.Foreground"/>) reads the same preset instead of the
/// theme's Warning token, so editing the preset moves both windows.</para>
/// </summary>
[Collection("highlight-statics")]
public class MonsterBoldPrecedenceTests
{
    private const string Line = "You also see a fierce war troll.";

    private static BoldSpan[] BoldOn(string text, string phrase)
    {
        var start = text.IndexOf(phrase, StringComparison.Ordinal);
        Assert.True(start >= 0, $"probe text must contain '{phrase}'");
        return new[] { new BoldSpan(start, phrase.Length) };
    }

    private static (IBrush? Fg, IBrush? Bg) At(DefaultHighlights.StyleMap map, string text, string phrase)
    {
        var i = text.IndexOf(phrase, StringComparison.Ordinal);
        Assert.True(i >= 0);
        return (map.Foreground[i], map.Background[i]);
    }

    // Compare Color VALUES, not strings — Avalonia's Color.ToString() returns
    // known-colour names ("Red"), so string comparison is representation-fragile.
    private static Color? C(IBrush? b) => (b as ISolidColorBrush)?.Color;

    /// <summary>Swap all three process-wide engine slots for the test and
    /// restore them after (other test classes share the statics).</summary>
    private sealed class Rig : IDisposable
    {
        private readonly HighlightEngine?     _prevHighlights = UserHighlights.Engine;
        private readonly PresetEngine?        _prevPresets    = DefaultHighlights.PresetEngine;
        private readonly NameHighlightEngine? _prevNames      = DefaultHighlights.NameEngine;
        private readonly bool                 _prevEnabled    = DefaultHighlights.MonsterBoldEnabled;

        public HighlightEngine Highlights { get; } = new();
        public PresetEngine    Presets    { get; } = new();   // defaults: creatures = Gold

        public Rig()
        {
            UserHighlights.Engine          = Highlights;
            DefaultHighlights.PresetEngine = Presets;
            DefaultHighlights.NameEngine   = null;
            DefaultHighlights.MonsterBoldEnabled = true;
        }

        public void Dispose()
        {
            UserHighlights.Engine          = _prevHighlights;
            DefaultHighlights.PresetEngine = _prevPresets;
            DefaultHighlights.NameEngine   = _prevNames;
            DefaultHighlights.MonsterBoldEnabled = _prevEnabled;
        }
    }

    // ── #235: precedence ────────────────────────────────────────────────────

    [Fact]
    public void MonsterBold_preempts_a_user_highlight_on_the_same_chars()
    {
        using var rig = new Rig();
        // The reporter's shape: a whole-line highlight on "You also see".
        rig.Highlights.AddRule("You also see a fierce war troll.", "#FF0000");

        var map = DefaultHighlights.BuildStyleMap(Line, boldSpans: BoldOn(Line, "fierce war troll"));

        var creature = At(map, Line, "fierce war troll");
        var prose    = At(map, Line, "You also see");
        Assert.Equal(C(DefaultHighlights.CreaturesPresetBrush), C(creature.Fg)); // creature = MonsterBold
        Assert.Equal(Color.Parse("#FF0000"), C(prose.Fg));                           // rest = the rule
    }

    [Fact]
    public void User_highlight_background_still_shows_under_MonsterBold()
    {
        using var rig = new Rig();
        rig.Highlights.AddRule("You also see a fierce war troll.", "#FF0000", "#000080");

        var map = DefaultHighlights.BuildStyleMap(Line, boldSpans: BoldOn(Line, "fierce war troll"));

        var creature = At(map, Line, "fierce war troll");
        Assert.Equal(C(DefaultHighlights.CreaturesPresetBrush), C(creature.Fg));
        Assert.Equal(Color.Parse("#000080"), C(creature.Bg));   // rule's bg survives beneath
    }

    [Fact]
    public void Player_names_stay_supreme_over_MonsterBold()
    {
        using var rig = new Rig();
        var names = new NameHighlightEngine();
        names.Add("troll", "#00FF00");          // pretend "troll" is a tracked name
        DefaultHighlights.NameEngine = names;

        var map = DefaultHighlights.BuildStyleMap(Line, boldSpans: BoldOn(Line, "fierce war troll"));

        Assert.Equal(Color.Parse("#00FF00"), C(At(map, Line, "troll").Fg));   // name wins
        Assert.Equal(C(DefaultHighlights.CreaturesPresetBrush),
                     C(At(map, Line, "fierce war").Fg));             // rest of bold = MonsterBold
    }

    [Fact]
    public void Default_creatures_preset_restores_highlight_colouring_of_bold_text()
    {
        using var rig = new Rig();
        // Colour off (weight-only bold): the user rule may colour bold chars again.
        rig.Presets.Apply(new PresetRule { Id = "creatures", ForegroundColor = "Default" });
        rig.Highlights.AddRule("You also see a fierce war troll.", "#FF0000");

        var map = DefaultHighlights.BuildStyleMap(Line, boldSpans: BoldOn(Line, "fierce war troll"));

        Assert.Equal(Color.Parse("#FF0000"), C(At(map, Line, "fierce war troll").Fg));
    }

    [Fact]
    public void MonsterBold_toggle_off_restores_highlight_colouring_of_bold_text()
    {
        using var rig = new Rig();
        DefaultHighlights.MonsterBoldEnabled = false;
        rig.Highlights.AddRule("You also see a fierce war troll.", "#FF0000");

        var map = DefaultHighlights.BuildStyleMap(Line, boldSpans: BoldOn(Line, "fierce war troll"));

        Assert.Equal(Color.Parse("#FF0000"), C(At(map, Line, "fierce war troll").Fg));
    }

    // ── #236: one colour source ─────────────────────────────────────────────

    [Fact]
    public void Mobs_row_default_foreground_is_the_creatures_preset()
    {
        using var rig = new Rig();
        rig.Presets.Apply(new PresetRule { Id = "creatures", ForegroundColor = "#123456" });

        var item = new MobItem("a fierce war troll", new MobsViewModel());

        Assert.Equal(Color.Parse("#123456"), C(item.Foreground));
        // And it is literally the same source the Main window's layer reads.
        Assert.Equal(C(DefaultHighlights.CreaturesPresetBrush), C(item.Foreground));
    }

    [Fact]
    public void Mobs_row_and_main_window_agree_after_a_preset_edit()
    {
        using var rig = new Rig();
        rig.Presets.Apply(new PresetRule { Id = "creatures", ForegroundColor = "#ABCDEF" });

        var map  = DefaultHighlights.BuildStyleMap(Line, boldSpans: BoldOn(Line, "fierce war troll"));
        var item = new MobItem("a fierce war troll", new MobsViewModel());

        Assert.Equal(C(At(map, Line, "fierce war troll").Fg), C(item.Foreground));
    }
}
