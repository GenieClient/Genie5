using System;
using System.Linq;
using Avalonia.Media;
using Genie.App.Highlighting;
using Genie.App.ViewModels;
using Genie.Core.Highlights;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #232 — a highlight scoped to only the Experience window never
/// painted. The scoping machinery was fine; the Experience panel's rows are
/// <see cref="TextLine"/>s whose <c>Window</c> parameter defaults to "main",
/// and <c>ExperienceViewModel</c> never passed one — so every row evaluated
/// <c>AppliesToWindow("main")</c>. The fix tags the panel's rows with the
/// canonical id "experience" (scope matching is case-insensitive, so the
/// reporter's "Experience" works too).
///
/// <para>Tests pin the contract at the <see cref="DefaultHighlights"/>
/// boundary — the per-character <see cref="DefaultHighlights.StyleMap"/> both
/// renderers consume — plus the <see cref="TextLine"/> clone used by the
/// retokenize-on-rule-change paths, which used to rebuild lines from
/// text+color only and strip Links/BoldSpans/Mono/Window from the whole
/// scrollback on every rule apply.</para>
/// </summary>
[Collection("highlight-statics")]
public class ExperienceHighlightScopingTests
{
    /// <summary>Install a throwaway HighlightEngine into the process-wide
    /// UserHighlights slot, restoring the previous one on dispose (other test
    /// classes may run in parallel against the same static).</summary>
    private sealed class EngineSwap : IDisposable
    {
        private readonly HighlightEngine? _previous;
        public HighlightEngine Engine { get; }
        public EngineSwap()
        {
            _previous = UserHighlights.Engine;
            Engine = new HighlightEngine();
            UserHighlights.Engine = Engine;
        }
        public void Dispose() => UserHighlights.Engine = _previous;
    }

    private static bool SpanPainted(DefaultHighlights.StyleMap map, string text, string span)
    {
        var start = text.IndexOf(span, StringComparison.Ordinal);
        Assert.True(start >= 0, $"probe text must contain '{span}'");
        for (int i = start; i < start + span.Length; i++)
            if (map.Foreground[i] is null) return false;
        return true;
    }

    private const string Probe = "Attack teaches well";

    [Fact]
    public void Experience_scoped_rule_paints_in_the_experience_window()
    {
        using var swap = new EngineSwap();
        // The reporter's exact shape: window name typed as "Experience".
        swap.Engine.AddRule("Attack", "#FF0000", windows: new[] { "Experience" });

        var map = DefaultHighlights.BuildStyleMap(Probe, window: "experience");

        Assert.True(SpanPainted(map, Probe, "Attack"));
    }

    [Fact]
    public void Experience_scoped_rule_does_not_paint_in_main()
    {
        using var swap = new EngineSwap();
        swap.Engine.AddRule("Attack", "#FF0000", windows: new[] { "Experience" });

        var map = DefaultHighlights.BuildStyleMap(Probe, window: "main");

        Assert.False(SpanPainted(map, Probe, "Attack"));
    }

    [Fact]
    public void Main_scoped_rule_does_not_paint_in_the_experience_window()
    {
        using var swap = new EngineSwap();
        // The inverse bug: before the fix, Experience rows claimed to be
        // "main", so a main-only rule wrongly painted there.
        swap.Engine.AddRule("Attack", "#FF0000", windows: new[] { "main" });

        var map = DefaultHighlights.BuildStyleMap(Probe, window: "experience");

        Assert.False(SpanPainted(map, Probe, "Attack"));
    }

    [Fact]
    public void Unscoped_rule_paints_everywhere_including_experience()
    {
        using var swap = new EngineSwap();
        swap.Engine.AddRule("Attack", "#FF0000");   // empty scope = all windows

        Assert.True(SpanPainted(DefaultHighlights.BuildStyleMap(Probe, window: "experience"), Probe, "Attack"));
        Assert.True(SpanPainted(DefaultHighlights.BuildStyleMap(Probe, window: "main"),       Probe, "Attack"));
    }

    [Fact]
    public void Retokenize_clone_keeps_spans_mono_and_window()
    {
        // The `with { }` clone both retokenize paths now use. The old
        // text+color rebuild dropped every other field.
        var original = new TextLine(
            "a razor-edged scimitar",
            StreamColor.Main,
            Links:       new[] { new Genie.Core.Events.LinkSpan(2, 20, "look scimitar", false) },
            BoldSpans:   new[] { new Genie.Core.Events.BoldSpan(2, 20) },
            PresetSpans: null,
            Mono:        true,
            Window:      "experience");

        var clone = original with { };

        Assert.NotSame(original, clone);           // Replace event still fires
        Assert.Equal(original.Links, clone.Links);
        Assert.Equal(original.BoldSpans, clone.BoldSpans);
        Assert.True(clone.Mono);
        Assert.Equal("experience", clone.Window);
    }
}
