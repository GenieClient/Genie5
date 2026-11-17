using System;
using Avalonia.Media;
using Genie.App.Highlighting;
using Genie.Core.Events;
using Genie.Core.Highlights;
using Genie.Core.Presets;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #396 — Genie colours game text only from sources the user controls.
///
/// <para>DefaultHighlights used to carry eight hardcoded colour rules (room
/// titles, Roundtime, directions, currency, "Your", all-caps runs, 2+ digit
/// numbers) that painted every user's text and sat ABOVE presets, so the
/// built-in room-title colour hid the <c>roomname</c> preset's foreground
/// (the unfinished half of #174). Genie 3/4 had no such layer. The two link
/// colours were hardcoded too; they are now the <c>link</c> / <c>url</c>
/// presets.</para>
/// </summary>
[Collection("highlight-statics")]
public class NoBuiltInHighlightsTests
{
    private static Color? C(IBrush? b) => (b as ISolidColorBrush)?.Color;

    private sealed class Rig : IDisposable
    {
        private readonly HighlightEngine?     _prevHighlights = UserHighlights.Engine;
        private readonly PresetEngine?        _prevPresets    = DefaultHighlights.PresetEngine;
        private readonly NameHighlightEngine? _prevNames      = DefaultHighlights.NameEngine;

        public HighlightEngine Highlights { get; } = new();
        public PresetEngine    Presets    { get; } = new();

        public Rig()
        {
            UserHighlights.Engine          = Highlights;   // empty: no user rules
            DefaultHighlights.PresetEngine = Presets;
            DefaultHighlights.NameEngine   = null;
        }

        public void Dispose()
        {
            UserHighlights.Engine          = _prevHighlights;
            DefaultHighlights.PresetEngine = _prevPresets;
            DefaultHighlights.NameEngine   = _prevNames;
        }
    }

    [Theory]
    [InlineData("You count out 2100 Dokoras and 15 silver Lirums.")]   // numbers + currency
    [InlineData("Obvious paths: north, southeast, up, out.")]          // directions
    [InlineData("Roundtime: 3 sec.")]                                  // roundtime
    [InlineData("Your mind hears Someone thinking, \"hello\"")]        // possessive
    [InlineData("YOU HAVE BEEN IDLE TOO LONG.")]                       // all-caps run
    [InlineData("[Kaal Utewg, Bog]")]                                  // bracketed title line
    [InlineData("A ring trimmed in gold.")]                            // currency metal
    public void Unconfigured_text_keeps_the_default_foreground(string line)
    {
        using var _ = new Rig();
        var map = DefaultHighlights.BuildStyleMap(line);
        Assert.All(map.Foreground, b => Assert.Null(b));
        Assert.All(map.Background, b => Assert.Null(b));
    }

    [Fact]
    public void Roomname_preset_foreground_paints_the_room_title()
    {
        using var rig = new Rig();
        rig.Presets.Apply(new PresetRule { Id = "roomname", ForegroundColor = "Yellow", BackgroundColor = "DarkBlue" });
        const string title = "[Kaal Utewg, Bog]";

        var map = DefaultHighlights.BuildStyleMap(title, presetSpans: new[] { new PresetSpan(0, title.Length, "roomName") });

        Assert.All(map.Foreground, b => Assert.Equal(Colors.Yellow, C(b)));
        Assert.All(map.Background, b => Assert.Equal(Colors.DarkBlue, C(b)));
    }

    [Fact]
    public void User_rule_still_colours_text_it_matches()
    {
        using var rig = new Rig();
        rig.Highlights.AddRule("Dokoras", "#FF0000");
        const string line = "You have 2100 Dokoras.";

        var map = DefaultHighlights.BuildStyleMap(line);

        var at = line.IndexOf("Dokoras", StringComparison.Ordinal);
        Assert.Equal(Color.Parse("#FF0000"), C(map.Foreground[at]));
        Assert.Null(map.Foreground[line.IndexOf("2100", StringComparison.Ordinal)]);
    }

    // ── link / url presets ──────────────────────────────────────────────────

    [Fact]
    public void Link_colours_default_to_the_link_and_url_presets()
    {
        using var rig = new Rig();
        Assert.Equal(Color.Parse(rig.Presets.GetForeground("link")), C(DefaultHighlights.LinkForeground(isUrl: false)));
        Assert.Equal(Color.Parse(rig.Presets.GetForeground("url")),  C(DefaultHighlights.LinkForeground(isUrl: true)));
    }

    [Fact]
    public void Editing_the_link_presets_changes_link_colours()
    {
        using var rig = new Rig();
        rig.Presets.Apply(new PresetRule { Id = "link", ForegroundColor = "#123456" });
        rig.Presets.Apply(new PresetRule { Id = "url",  ForegroundColor = "#654321" });

        Assert.Equal(Color.Parse("#123456"), C(DefaultHighlights.LinkForeground(isUrl: false)));
        Assert.Equal(Color.Parse("#654321"), C(DefaultHighlights.LinkForeground(isUrl: true)));
    }

    [Fact]
    public void Default_link_preset_means_no_link_colour()
    {
        using var rig = new Rig();
        rig.Presets.Apply(new PresetRule { Id = "link", ForegroundColor = "Default" });
        Assert.Null(DefaultHighlights.LinkForeground(isUrl: false));
    }

    [Fact]
    public void Link_colours_come_from_stock_presets_before_connect()
    {
        using var _ = new Rig();
        DefaultHighlights.PresetEngine = null;   // Rig restores it on dispose
        var stock = new PresetEngine();
        Assert.Equal(Color.Parse(stock.GetForeground("link")), C(DefaultHighlights.LinkForeground(isUrl: false)));
    }
}
