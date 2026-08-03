using System;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Genie.App.Highlighting;
using Genie.App.ViewModels;
using Genie.Core.Events;

namespace Genie.App.Controls;

/// <summary>
/// One buffered game line as the AvaloniaEdit renderer sees it: the
/// <see cref="TextLine"/> the view-model produced, plus the resolved
/// <see cref="DefaultHighlights.StyleMap"/> for it.
///
/// <para>The map is built <b>eagerly, once, when the line arrives</b> — not lazily
/// at paint time — for two reasons. (1) A highlight rule can carry a sound or a
/// TTS phrase, and <see cref="DefaultHighlights.BuildStyleMap"/> fires those as a
/// side effect of matching; the legacy renderer realises every line's container
/// immediately, so the alert fires on arrival. AvaloniaEdit only paints the
/// visible window, so a lazily-built map would delay the alert until the user
/// scrolled to it — and re-fire it on every re-scroll. (2) The colorizer runs on
/// every visual-line rebuild; caching keeps the regex work per line at one pass.</para>
/// </summary>
internal sealed class GameLineEntry
{
    private static readonly LinkSpan[] NoLinks = [];

    internal GameLineEntry(TextLine line)
    {
        Line = line;
        // Echo lines bypass highlighting entirely (legacy TextLine.Inlines does the
        // same), so they never build a map — and never fire highlight sound / TTS.
        if (!line.IsEcho)
            Map = DefaultHighlights.BuildStyleMap(
                line.Text, line.Links, line.BoldSpans, line.PresetSpans, line.Window);
    }

    internal TextLine Line { get; }

    /// <summary>Resolved style layers; default (all-null arrays) for echo lines.</summary>
    internal DefaultHighlights.StyleMap Map { get; }

    /// <summary>Validated, start-ordered link spans for this line — the same list
    /// the legacy emit pass walks, so <c>ShowLinks=false</c> and malformed spans are
    /// already filtered out. Empty for echo lines.</summary>
    internal IReadOnlyList<LinkSpan> Links => Line.IsEcho ? NoLinks : Map.Links;
}

/// <summary>
/// Paints one document line of the AvaloniaEdit-backed Game window from the same
/// highlight semantics the legacy per-line renderer uses.
///
/// <para>The rules are NOT reimplemented here. <see cref="DefaultHighlights.BuildStyleMap"/>
/// resolves every layer (player names, user highlight rules, built-in defaults,
/// MonsterBold, presets) into per-character foreground / background / bold maps;
/// this class only translates those maps into <c>ChangeLinePart</c> runs. The
/// legacy path calls the same builder and translates the same maps into
/// <c>Inline</c>s instead.</para>
///
/// <para>Two shapes of line are handled separately, mirroring
/// <see cref="TextLine.Inlines"/>: echo lines (<c>#echo</c>, script output,
/// diagnostics) render as one run styled from the <c>EchoBrush</c> /
/// <c>EchoFontStyle</c> resources — the editor has no AXAML class selector, so the
/// <c>.echo</c> style is resolved here — and game text runs the full highlight
/// pipeline.</para>
///
/// <para>Character positions inside a link span are deliberately left alone: the
/// element built by <see cref="GameLinkGenerator"/> owns their colour and
/// underline, exactly as the legacy emit pass skips link ranges and emits a
/// clickable run instead.</para>
/// </summary>
internal sealed class GameTextColorizer : DocumentColorizingTransformer
{
    private readonly Func<int, GameLineEntry?> _entryAt;

    /// <param name="entryAt">Document line number (1-based) → the buffered line that
    /// produced it, or null when the document and the side list are momentarily out
    /// of step (nothing to paint — the row renders plain).</param>
    internal GameTextColorizer(Func<int, GameLineEntry?> entryAt) => _entryAt = entryAt;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0) return;
        if (_entryAt(line.LineNumber) is not { } entry) return;

        var meta  = entry.Line;
        var start = line.Offset;
        var end   = line.EndOffset;

        // #178: a <output class="mono"> block (maps, stat tables, appraisals) or an
        // `#echo mono` line keeps its highlights but renders monospaced, so a
        // proportional game font can't break column alignment. Applied to the WHOLE
        // line first — including link elements — so every glyph on the row shares
        // one advance width. Later ChangeLinePart calls only set fore/back/weight
        // (re-deriving the typeface from whatever this left), so they can't undo it.
        if (meta.Mono)
            ChangeLinePart(start, end, el => el.TextRunProperties.SetTypeface(
                Retypeface(el.TextRunProperties.Typeface, TextLine.MonoFont, bold: false)));

        if (meta.IsEcho)
        {
            ColorizeEchoLine(meta, start, end);
            return;
        }

        var map = entry.Map;

        // The maps are indexed by character position in meta.Text and the document
        // holds that same string, so document offset = line.Offset + index. Clamp
        // anyway: a drifted side list must render plain, never throw.
        var len   = Math.Min(map.Foreground.Length, line.Length);
        var links = entry.Links;

        var i       = 0;
        var linkIdx = 0;
        while (i < len)
        {
            // Skip characters owned by a link element.
            while (linkIdx < links.Count && links[linkIdx].Start + links[linkIdx].Length <= i)
                linkIdx++;
            if (linkIdx < links.Count && links[linkIdx].Start <= i)
            {
                i = links[linkIdx].Start + links[linkIdx].Length;
                continue;
            }

            // Coalesce the maximal run with identical foreground / background /
            // weight before touching the visual tree — one ChangeLinePart per
            // character would split every glyph into its own element.
            var limit = linkIdx < links.Count ? Math.Min(len, links[linkIdx].Start) : len;
            var fg    = map.Foreground[i];
            var bg    = map.Background[i];
            var bold  = map.Bold[i];
            var j     = i + 1;
            while (j < limit
                   && ReferenceEquals(map.Foreground[j], fg)
                   && ReferenceEquals(map.Background[j], bg)
                   && map.Bold[j] == bold)
                j++;

            if (fg is not null || bg is not null || bold)
                Apply(start + i, start + j, fg, bg, bold, meta.Mono);

            i = j;
        }
    }

    /// <summary>
    /// Echo lines bypass highlighting and render as a single styled run — the
    /// editor's stand-in for the AXAML <c>:is(TextBlock).echo</c> selector. An
    /// explicit <c>#echo</c> colour overrides the class colour, matching
    /// <see cref="TextLine.Inlines"/> where the run's own Foreground beats the
    /// class style.
    /// </summary>
    private void ColorizeEchoLine(TextLine meta, int start, int end)
    {
        var fg    = meta.EchoForeground() ?? FindResource(Settings.DisplaySettings.EchoBrushKey) as IBrush;
        var style = FindResource(Settings.DisplaySettings.EchoFontStyleKey) as FontStyle? ?? FontStyle.Italic;

        ChangeLinePart(start, end, el =>
        {
            if (fg is not null) el.TextRunProperties.SetForegroundBrush(fg);
            var tf = el.TextRunProperties.Typeface;
            el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, style, tf.Weight, tf.Stretch));
        });
    }

    private void Apply(int from, int to, IBrush? fg, IBrush? bg, bool bold, bool mono)
        => ChangeLinePart(from, to, el =>
        {
            if (fg is not null) el.TextRunProperties.SetForegroundBrush(fg);
            if (bg is not null) el.TextRunProperties.SetBackgroundBrush(bg);
            if (bold)
            {
                var tf = el.TextRunProperties.Typeface;
                el.TextRunProperties.SetTypeface(
                    Retypeface(tf, mono ? TextLine.MonoFont : tf.FontFamily, bold: true));
            }
        });

    /// <summary>Rebuild a typeface keeping style + stretch, swapping the family and
    /// (when asked) forcing Bold. <see cref="Typeface"/> is a readonly struct, so
    /// every change is a fresh instance.</summary>
    private static Typeface Retypeface(Typeface source, FontFamily family, bool bold)
        => new(family, source.Style, bold ? FontWeight.Bold : source.Weight, source.Stretch);

    /// <summary>Application-level resource lookup that also reaches into the merged
    /// dictionaries (the plain <c>Resources[key]</c> indexer only sees keys pushed
    /// directly, which covers <c>EchoBrush</c> but not the palette includes).
    /// <para>Looked up per echo line rather than cached: DisplaySettings rewrites
    /// EchoBrush / EchoFontStyle in Application.Resources whenever the user edits
    /// them, and a cache would need its own invalidation hook for a lookup that only
    /// runs for the echo lines actually on screen.</para></summary>
    internal static object? FindResource(string key)
    {
        var app = Application.Current;
        return app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var v) == true ? v : null;
    }
}
