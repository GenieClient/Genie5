using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Genie.Core.Diagnostics;
using Genie.Core.Events;
using Genie.Core.Highlights;

namespace Genie.App.Highlighting;

/// <summary>
/// Default Wrayth-style highlight set applied to incoming game text. First-rule-
/// per-character wins on overlap. Patterns are compiled once and reused.
///
/// Future work: merge with user-defined rules from <c>HighlightEngine</c> /
/// <c>NameHighlightEngine</c> in <c>Genie.Core</c>.
/// </summary>
public static class DefaultHighlights
{
    // ── Brushes (sharable, immutable) ─────────────────────────────────────────

    private static readonly IBrush CurrencyBrush   = MakeBrush(0x7C, 0xC8, 0x7C); // green
    private static readonly IBrush DirectionBrush  = MakeBrush(0x7C, 0xC8, 0xE8); // cyan
    private static readonly IBrush PossessiveBrush = MakeBrush(0xE0, 0xC0, 0x78); // gold
    private static readonly IBrush AllCapsBrush    = MakeBrush(0xE8, 0x90, 0x60); // orange
    private static readonly IBrush RoundTimeBrush  = MakeBrush(0xE0, 0x60, 0x60); // red-orange
    private static readonly IBrush NumberBrush     = MakeBrush(0xC8, 0xC8, 0x90); // tan-cream
    private static readonly IBrush RoomTitleBrush  = MakeBrush(0x90, 0xA8, 0xF0); // light blue
    private static readonly IBrush LinkBrush       = MakeBrush(0x80, 0xC0, 0xFF); // bright blue (Wrayth convention)
    private static readonly IBrush UrlBrush        = MakeBrush(0x8C, 0xD8, 0xA8); // cool green — distinguishes external URLs from game-command links

    /// <summary>
    /// Click dispatcher for &lt;d cmd&gt; links. Set by <c>MainWindowViewModel</c>
    /// at connect time so this static tokenizer doesn't need a GenieCore
    /// reference. Null until first connect, in which case the link still
    /// renders as styled but click is a no-op.
    /// <para>
    /// Signature is <c>(cmd, displayText)</c>: <c>cmd</c> is the value the
    /// server expects (with item-exist-IDs like <c>get #49489411 in
    /// #49489410</c>), <c>displayText</c> is the friendly text the user saw
    /// on screen (<c>a tapered cutlass</c>). The handler echoes <c>displayText</c>
    /// to the Game window so the player sees a readable echo instead of the
    /// raw IDs.
    /// </para>
    /// </summary>
    public static System.Action<string, string>? OnLinkClicked;

    /// <summary>
    /// Click dispatcher for <c>&lt;a href&gt;</c> URL links. Set by
    /// <c>MainWindowViewModel</c> at connect time. Passed the URL string —
    /// handler is expected to launch the OS-default browser
    /// (<c>Process.Start(url, UseShellExecute=true)</c>). Null until first
    /// connect; in that case the URL still renders styled but click is a no-op.
    /// </summary>
    public static System.Action<string>? OnUrlClicked;

    /// <summary>
    /// Sound dispatcher for highlight SFX. Set by <c>MainWindowViewModel</c> at
    /// connect time to route to <c>GenieCore.PlaySound</c> (which applies the
    /// PlaySounds gate + SoundDir resolution). Invoked once per line that a
    /// sound-carrying highlight matches. Null until connect → no sound.
    /// </summary>
    public static System.Action<string>? OnHighlightSound;

    /// <summary>
    /// TTS dispatcher for per-highlight speak. Set by <c>MainWindowViewModel</c>
    /// at connect time to route to the TTS engine at high priority (a speak-
    /// flagged highlight is a hand-picked alert, so it barges in over stream
    /// read-aloud). Invoked once per line a speak-carrying highlight matches,
    /// with the text to say. Null until connect → silent.
    /// </summary>
    public static System.Action<string>? OnHighlightSpeak;

    /// <summary>
    /// Master toggle that mirrors <c>GenieConfig.ShowLinks</c>. When false,
    /// link spans render as ordinary text (no underline, no cursor change,
    /// no click handler) — useful for users who find the underlines noisy.
    /// </summary>
    public static bool LinksEnabled = true;

    /// <summary>
    /// MonsterBold (#131). When true, DR's &lt;pushBold&gt; spans (creature names,
    /// combat hits) render bold + the <c>creatures</c> preset colour; when false
    /// they render as plain text. Mirrors <see cref="Genie.Core.Config.GenieConfig.MonsterBold"/>
    /// — set at connect (like <see cref="LinksEnabled"/>) and live from the
    /// Presets panel's MonsterBold checkbox. Gates the per-char bold mask below,
    /// so the whole effect (weight + colour) toggles from this one flag.
    /// </summary>
    public static bool MonsterBoldEnabled = true;

    // ── Patterns (ordered: earlier rules win on overlap) ──────────────────────

    private static readonly (Regex Pattern, IBrush Brush)[] Rules =
    [
        // Bracketed room titles on their own line — e.g. [Garden Rooftop, Medical Pavilion]
        (new Regex(@"^\s*\[[^\]]+\]\s*$", Opts), RoomTitleBrush),

        // "Roundtime: 3 sec." / "Roundtime: 3 seconds." — combat readiness signal
        (new Regex(@"\bRoundtime:\s*\d+\s*sec(?:ond)?s?\.?", Opts), RoundTimeBrush),

        // Compass directions when listed as exits or in movement messages
        (new Regex(@"\b(?:north|south|east|west|northeast|northwest|southeast|southwest|up|down|out)\b",
                   Opts), DirectionBrush),

        // Currency metals + DR currency names
        (new Regex(@"\b(?:platinum|gold|silver|bronze|copper)\b(?=\s+(?:Kronars|Lirums|Dokoras|coins?)|\s+to|,|\s*$|\s*\.)",
                   Opts), CurrencyBrush),
        (new Regex(@"\b(?:Kronars|Lirums|Dokoras)\b", Opts), CurrencyBrush),

        // "Your X" — common in health, vitals, status checks
        (new Regex(@"\bYour\b", Opts), PossessiveBrush),

        // All-caps tags like BANK DEBT, GO HOLD, RUMOR — 2+ caps words in a row
        (new Regex(@"\b[A-Z]{2,}(?:\s+[A-Z]{2,})+\b", Opts), AllCapsBrush),

        // Numbers >= 2 digits (skips small inline numbers like "1 sec")
        (new Regex(@"\b\d{2,}\b", Opts), NumberBrush),
    ];

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static IBrush MakeBrush(byte r, byte g, byte b)
        => new SolidColorBrush(Color.FromRgb(r, g, b));

    /// <summary>The foreground a link span paints with — game-command blue or
    /// external-URL green. Shared with the AvaloniaEdit renderer's link element
    /// (<c>Controls.GameLinkGenerator</c>) so both paths colour <c>&lt;d cmd&gt;</c>
    /// spans identically rather than each keeping their own copy of the palette.</summary>
    internal static IBrush LinkForeground(bool isUrl) => isUrl ? UrlBrush : LinkBrush;

    // ── Tokenizer ─────────────────────────────────────────────────────────────

    // ── User-rule brush cache (parsed hex → IBrush) ──────────────────────────
    // User-defined rules supply colours as hex strings; parse once and reuse.
    private static readonly Dictionary<string, IBrush> UserBrushCache = new(StringComparer.OrdinalIgnoreCase);

    private static IBrush? GetUserBrush(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        if (UserBrushCache.TryGetValue(hex, out var brush)) return brush;
        if (!Avalonia.Media.Color.TryParse(hex, out var c)) return null;
        brush = new SolidColorBrush(c);
        UserBrushCache[hex] = brush;
        return brush;
    }

    /// <summary>
    /// Active session's preset palette (<c>roomDesc</c>, <c>whisper</c>,
    /// <c>speech</c> … → colour). Set by <c>MainWindowViewModel</c> at connect,
    /// like <see cref="UserHighlights.Engine"/>. When set, <see cref="Tokenize"/>
    /// colours each line's preset spans as the BASE colour (highlights win on
    /// top). Null when no session is connected.
    /// </summary>
    public static Genie.Core.Presets.PresetEngine? PresetEngine { get; set; }

    /// <summary>
    /// The MonsterBold colour — the <c>creatures</c> preset resolved to a brush,
    /// or null when the preset is "Default"/unset (weight-only bold). The ONE
    /// source of truth for creature colouring (#236): the Main-window MonsterBold
    /// layer above and the Mobs panel's default row foreground both read it, so
    /// editing the preset moves every window together. Live updates ride
    /// <see cref="UserHighlights.RulesChanged"/> (the Presets panel fires it).
    /// </summary>
    public static IBrush? CreaturesPresetBrush =>
        PresetEngine is { } p ? GetUserBrush(p.GetForeground("creatures")) : null;

    /// <summary>
    /// Active session's player-name highlight engine (#154). Set by
    /// <c>MainWindowViewModel</c> at connect, like <see cref="PresetEngine"/>.
    /// When set, <see cref="Tokenize"/> paints each name rule's foreground /
    /// background over its match positions as its own colour layer. Null when no
    /// session is connected. Repaint of already-visible lines is driven by
    /// <see cref="UserHighlights.RulesChanged"/> (the Names panel fires it).
    /// </summary>
    public static NameHighlightEngine? NameEngine { get; set; }

    /// <summary>Map an XML preset id to its palette key. The lookup is
    /// case-insensitive (so <c>roomDesc</c>→<c>roomdesc</c>, <c>roomName</c>→
    /// <c>roomname</c> resolve directly). DR emits a few preset ids in the
    /// singular while <see cref="Genie.Core.Presets.PresetEngine"/> keys them in
    /// the plural — <c>whisper</c>→<c>whispers</c> and <c>thought</c>→
    /// <c>thoughts</c> — so those two are remapped here. Without the thought
    /// mapping the thoughts stream rendered with the default colour instead of
    /// its palette colour.</summary>
    private static string MapPresetKey(string xmlId) => xmlId.ToLowerInvariant() switch
    {
        "whisper" => "whispers",
        "thought" => "thoughts",
        _         => xmlId,
    };

    /// <summary>
    /// Split <paramref name="text"/> into a sequence of styled <see cref="Inline"/>s.
    /// Each character is colored by the first matching rule; runs of identical-color
    /// characters collapse into a single <see cref="Run"/>. Built-in defaults run
    /// first, then user-defined rules from <see cref="UserHighlights.Engine"/>
    /// fill in any character positions the defaults didn't claim.
    ///
    /// <paramref name="links"/> overlays clickable spans (from
    /// <c>&lt;d cmd="..."&gt;</c> markup): each span emits its own
    /// <see cref="Run"/> with underline + cursor and a pointer handler
    /// that calls <see cref="OnLinkClicked"/> on press.
    ///
    /// <paramref name="boldSpans"/> overlays bold styling (from
    /// <c>&lt;pushBold/&gt;</c> / <c>&lt;popBold/&gt;</c> markers). Bold
    /// ranges interact with color highlighting orthogonally — a colored
    /// substring inside a bold span gets both color and weight.
    /// </summary>
    public static IReadOnlyList<Inline> Tokenize(string text,
                                                 IReadOnlyList<LinkSpan>? links = null,
                                                 IReadOnlyList<BoldSpan>? boldSpans = null,
                                                 IReadOnlyList<PresetSpan>? presetSpans = null,
                                                 string window = "main")
    {
        if (string.IsNullOrEmpty(text))
            return new[] { new Run(string.Empty) };

        var map = BuildStyleMap(text, links, boldSpans, presetSpans, window);
        return EmitInlines(text, map);
    }

    /// <summary>
    /// The per-character style layers <see cref="BuildStyleMap"/> resolves for a
    /// line, before anything is committed to a particular renderer's element
    /// type. <see cref="Tokenize"/> turns these into Avalonia <see cref="Inline"/>s
    /// for the legacy per-line renderer; <c>Controls.GameTextColorizer</c> turns
    /// the same maps into AvaloniaEdit <c>ChangeLinePart</c> runs. Both consume
    /// one implementation of the highlight semantics — the maps ARE the contract.
    ///
    /// <para><see cref="Foreground"/> / <see cref="Background"/> / <see cref="Bold"/>
    /// are all <c>text.Length</c> long and indexed by character position; a null
    /// brush means "leave the renderer's default". <see cref="Links"/> is the
    /// validated, start-ordered link span list — empty when links are disabled or
    /// the line carries none. Characters inside a link span are NOT styled from
    /// the maps: a link paints as a link (see <see cref="MakeLinkRun"/>), which is
    /// why the emit pass skips those ranges.</para>
    /// </summary>
    public readonly record struct StyleMap(
        IBrush?[] Foreground,
        IBrush?[] Background,
        bool[] Bold,
        IReadOnlyList<LinkSpan> Links);

    /// <summary>
    /// Resolve every highlight layer for <paramref name="text"/> into per-character
    /// maps. This is <see cref="Tokenize"/> minus the final emit pass — same rules,
    /// same precedence, same side effects (highlight sound / TTS fire here, once per
    /// matching line, exactly as before).
    /// <para>Layer order, top to bottom: player names (#154, supreme) →
    /// MonsterBold colour (#131, above user rules per #235) → user highlight
    /// rules (#143) → built-in defaults → preset spans (base). Earlier layers
    /// win: each writes only where the channel is still null.</para>
    /// </summary>
    public static StyleMap BuildStyleMap(string text,
                                         IReadOnlyList<LinkSpan>? links = null,
                                         IReadOnlyList<BoldSpan>? boldSpans = null,
                                         IReadOnlyList<PresetSpan>? presetSpans = null,
                                         string window = "main")
    {
        if (string.IsNullOrEmpty(text))
            return new StyleMap([], [], [], []);

        // Build a per-char color map. Null = use the default TextBlock foreground.
        var brushes = new IBrush?[text.Length];

        // Parallel per-char BACKGROUND map. Null = transparent (default). A
        // highlight/preset may set a background independently of its foreground,
        // so this tracks its own first-write-wins layer and splits runs alongside
        // foreground + bold in EmitStyledRange.
        var backgrounds = new IBrush?[text.Length];

        // Parallel per-char bold mask. Set true for every character that
        // falls inside a BoldSpan. Bold splits runs alongside color so a
        // partly-bold colored phrase emits two runs with the same brush
        // but different FontWeight.
        var bolds = new bool[text.Length];
        if (MonsterBoldEnabled && boldSpans is { Count: > 0 })
        {
            foreach (var span in boldSpans)
            {
                if (span.Length <= 0) continue;
                var spanStart = Math.Max(0, span.Start);
                var spanEnd   = Math.Min(text.Length, span.Start + span.Length);
                for (int i = spanStart; i < spanEnd; i++) bolds[i] = true;
            }
        }

        // ── Player-name highlights (#154) — SUPREME layer ─────────────
        // Name rules are the top colour layer (Genie 4 semantics): they paint
        // FIRST, before user string highlights and the built-in defaults, so a
        // named player always shows their name colour even where another rule
        // would also match those characters. First-write-wins, foreground and
        // background painted independently. MatchAll yields non-overlapping
        // matches, longest-name-first.
        if (NameEngine is { Rules.Count: > 0 } nameEngine)
        {
            foreach (var (rule, start, length) in nameEngine.MatchAll(text))
            {
                var nameFg = GetUserBrush(rule.ForegroundColor);
                var nameBg = GetUserBrush(rule.BackgroundColor);
                if (nameFg is null && nameBg is null) continue;  // filter-only rule
                var end = Math.Min(start + length, text.Length);
                for (int i = start; i < end; i++)
                {
                    if (nameFg is not null && brushes[i]     is null) brushes[i]     = nameFg;
                    if (nameBg is not null && backgrounds[i] is null) backgrounds[i] = nameBg;
                }
            }
        }

        // ── MonsterBold (#131) — above user highlights (#235) ────────────
        // DR emphasises creature/NPC names and incoming combat/Events lines
        // with <pushBold>…</pushBold>; Wrayth and Genie 3/4 render that
        // "monster bold" in a distinct COLOUR, not just heavier weight, and it
        // PRE-EMPTS normal highlights — a cosmetic rule ("You also see…") must
        // never disguise a creature or an Events message. So the colour paints
        // right after names (a named player inside a bold line keeps their
        // name colour — names stay supreme) and before user/built-in rules.
        // Foreground only: a user rule's BACKGROUND still applies beneath it.
        // Set the `creatures` preset to Default in the Presets panel for
        // weight-only bold (colour off, and user rules colour bold text again).
        if (MonsterBoldEnabled && boldSpans is { Count: > 0 } && PresetEngine is { } mbPresets)
        {
            var mbBrush = GetUserBrush(mbPresets.GetForeground("creatures"));
            if (mbBrush is not null)
                for (int i = 0; i < bolds.Length; i++)
                    if (bolds[i]) brushes[i] ??= mbBrush;
        }

        // ── User-defined highlights from the live HighlightEngine ─────
        // User rules paint after names but before the built-in defaults: they win
        // over the built-ins on overlap (#143) — Genie 4 semantics, where the
        // user's own highlight config beats the auto-colouring — yet yield to a
        // name rule on the same characters (names are supreme, above). A user rule
        // on the room title or on EXP numbers still beats the built-in RoomTitle/
        // Number colours; the defaults below fill only the characters nothing claimed.
        // Timed into the Highlights stage (no-op overhead when the overlay is
        // hidden). This is the render-path cost of user highlight rules.
        if (UserHighlights.Engine is { Enabled: true } engine)
        {
            void ApplyUserHighlights()
            {
                // RuleSnapshot, not Rules (#251): this runs on the UI render
                // path while `#highlight add` from a script mutates the engine
                // on the game thread — the snapshot array is a stable
                // point-in-time copy, never mutated in place.
                foreach (var rule in engine.RuleSnapshot)
                {
                    if (!rule.IsEnabled) continue;
                    if (engine.Classes is { } classes && !classes.IsActive(rule.ClassName)) continue;
                    // Per-window scope: an empty Windows set means "everywhere"
                    // (the default); otherwise the rule paints only in the
                    // windows it lists.
                    if (!rule.AppliesToWindow(window)) continue;

                    // A rule may carry a foreground, a background, a sound, or
                    // any mix — so we detect the match even with no brush at all
                    // (sound/TTS-only highlight) and paint each colour channel
                    // independently (foreground-only, background-only, or both).
                    var ruleBrush = GetUserBrush(rule.ForegroundColor);
                    var ruleBg    = GetUserBrush(rule.BackgroundColor);
                    var matched   = false;
                    foreach (var (start, length) in rule.GetMatchPositions(text))
                    {
                        matched = true;
                        if (ruleBrush is null && ruleBg is null) continue; // match-only (sound/TTS)
                        var end = Math.Min(start + length, text.Length);
                        for (int i = start; i < end; i++)
                        {
                            if (ruleBrush is not null && brushes[i]     is null) brushes[i]     = ruleBrush;
                            if (ruleBg    is not null && backgrounds[i] is null) backgrounds[i] = ruleBg;
                        }
                    }
                    // Optional per-highlight SFX, once per matching line.
                    if (matched && !string.IsNullOrEmpty(rule.SoundFile))
                        OnHighlightSound?.Invoke(rule.SoundFile);
                    // Optional per-highlight TTS, once per matching line:
                    // "*" speaks the whole line, anything else that text.
                    if (matched && !string.IsNullOrEmpty(rule.Speak))
                        OnHighlightSpeak?.Invoke(rule.Speak == "*" ? text : rule.Speak);
                }
            }

            if (UserHighlights.Metrics is { } metrics)
                metrics.Time(PipelineStage.Highlights, ApplyUserHighlights);
            else
                ApplyUserHighlights();
        }

        // ── Built-in default highlights (room titles, currencies, …) ──
        // Fill only positions no user rule claimed above (the null check keeps
        // user highlights on top); within this set, earlier rules still win.
        foreach (var (pattern, brush) in Rules)
        {
            foreach (Match m in pattern.Matches(text))
            {
                for (int i = m.Index; i < m.Index + m.Length; i++)
                    if (brushes[i] is null) brushes[i] = brush;
            }
        }

        // ── Preset colours (base layer) ──────────────────────────────────
        // Apply preset span colours LAST with ??= so built-in and user
        // highlights win where they fired; presets fill the remaining chars of
        // a preset region (room descriptions, whispers, speech, …) with their
        // palette colour. "Default"/unknown palette entries leave the text its
        // normal foreground.
        if (presetSpans is { Count: > 0 } && PresetEngine is { } presets)
        {
            foreach (var span in presetSpans)
            {
                if (span.Length <= 0) continue;
                var key   = MapPresetKey(span.PresetId);
                var brush = GetUserBrush(presets.GetForeground(key));
                var bg    = GetUserBrush(presets.GetBackground(key));
                if (brush is null && bg is null) continue;
                var ps = Math.Max(0, span.Start);
                var pe = Math.Min(text.Length, span.Start + span.Length);
                for (int i = ps; i < pe; i++)
                {
                    if (brush is not null) brushes[i]     ??= brush;
                    if (bg    is not null) backgrounds[i] ??= bg;
                }
            }
        }

        // Order link spans by start position so we can emit non-link runs
        // between them in a single forward pass. Filter out invalid spans
        // (out-of-bounds, zero-length) defensively — the parser shouldn't
        // emit any, but better than crashing the renderer.
        List<LinkSpan>? sortedLinks = null;
        if (LinksEnabled && links is { Count: > 0 })
        {
            sortedLinks = new List<LinkSpan>(links.Count);
            foreach (var span in links)
            {
                if (span.Length <= 0) continue;
                if (span.Start < 0 || span.Start >= text.Length) continue;
                if (span.Start + span.Length > text.Length) continue;
                sortedLinks.Add(span);
            }
            sortedLinks.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        return new StyleMap(brushes, backgrounds, bolds,
                            (IReadOnlyList<LinkSpan>?)sortedLinks ?? []);
    }

    /// <summary>
    /// The emit pass: walk <paramref name="map"/> and turn it into Avalonia
    /// <see cref="Inline"/>s. Unchanged from the original <see cref="Tokenize"/>
    /// tail — an empty <see cref="StyleMap.Links"/> behaves exactly as the old
    /// null <c>sortedLinks</c> did (no link boundary before <c>text.Length</c>,
    /// loop breaks after the first styled range).
    /// </summary>
    private static IReadOnlyList<Inline> EmitInlines(string text, StyleMap map)
    {
        var brushes     = map.Foreground;
        var backgrounds = map.Background;
        var bolds       = map.Bold;
        var sortedLinks = map.Links;

        var inlines = new List<Inline>();
        int cursor = 0;
        int linkIdx = 0;

        // Walk the text. At each link boundary, flush the run of styled
        // text leading up to it, then emit a single clickable Run for the
        // link itself, then continue past.
        while (cursor < text.Length)
        {
            int nextLinkStart = linkIdx < sortedLinks.Count
                ? sortedLinks[linkIdx].Start
                : text.Length;

            // Emit styled (non-link) runs up to nextLinkStart.
            EmitStyledRange(text, brushes, backgrounds, bolds, cursor, nextLinkStart, inlines);
            cursor = nextLinkStart;

            if (linkIdx >= sortedLinks.Count) break;

            var link = sortedLinks[linkIdx++];
            var end  = link.Start + link.Length;
            // Skip overlapping/embedded links (defensive — DR doesn't nest).
            if (end <= cursor) continue;
            var linkText = text.Substring(link.Start, link.Length);
            inlines.Add(MakeLinkRun(linkText, link.Command, link.IsUrl));
            cursor = end;
        }

        return inlines.Count == 0 ? new[] { new Run(text) } : inlines;
    }

    /// <summary>
    /// Emit a styled sequence of <see cref="Run"/>s for <paramref name="text"/>
    /// from <paramref name="start"/> (inclusive) to <paramref name="end"/>
    /// (exclusive). Runs break on a foreground change, a background change, OR a
    /// bold flip, so a partly-styled phrase emits separate runs. Foreground,
    /// background, and bold combine orthogonally.
    /// </summary>
    private static void EmitStyledRange(string text, IBrush?[] brushes, IBrush?[] backgrounds,
                                        bool[] bolds, int start, int end, List<Inline> inlines)
    {
        if (start >= end) return;
        int runStart = start;
        IBrush? currentBrush = brushes[start];
        IBrush? currentBg    = backgrounds[start];
        bool    currentBold  = bolds[start];
        for (int i = start + 1; i < end; i++)
        {
            if (!ReferenceEquals(brushes[i], currentBrush)
                || !ReferenceEquals(backgrounds[i], currentBg)
                || bolds[i] != currentBold)
            {
                inlines.Add(MakeRun(text.AsSpan(runStart, i - runStart).ToString(), currentBrush, currentBg, currentBold));
                runStart     = i;
                currentBrush = brushes[i];
                currentBg    = backgrounds[i];
                currentBold  = bolds[i];
            }
        }
        inlines.Add(MakeRun(text[runStart..end], currentBrush, currentBg, currentBold));
    }

    private static Run MakeRun(string text, IBrush? brush, IBrush? background = null, bool bold = false)
    {
        var run = new Run(text);
        if (brush is not null)      run.Foreground = brush;
        if (background is not null) run.Background = background;
        if (bold)                   run.FontWeight = FontWeight.Bold;
        return run;
    }

    /// <summary>
    /// Build a clickable inline. Avalonia's <see cref="Run"/> is a pure-text
    /// inline with no pointer events, so wrap a <see cref="TextBlock"/> in an
    /// <see cref="InlineUIContainer"/>. The TextBlock carries the underline,
    /// hand cursor, and click handler. We deliberately don't set FontFamily
    /// or FontSize — Avalonia inherits both from the visual parent (the
    /// SelectableTextBlock that hosts these inlines) so the link's text
    /// metrics align with the surrounding monospace flow.
    /// </summary>
    private static Inline MakeLinkRun(string text, string command, bool isUrl = false)
    {
        var tb = new TextBlock
        {
            Text             = text,
            Foreground       = isUrl ? UrlBrush : LinkBrush,
            TextDecorations  = TextDecorations.Underline,
            Cursor           = new Cursor(StandardCursorType.Hand),
            // Payload so a container that owns pointer input (the game window's
            // SelectableLinesControl, which suppresses this TextBlock's own press
            // handler to drive cross-line selection) can re-dispatch the click.
            // Other panels keep using the PointerPressed handler below.
            Tag              = new LinkPayload(command, text, isUrl),
        };
        // Tooltip shows the URL for external links — safety hint before the
        // user clicks something that will leave the game window and open a
        // browser. Game-command links don't need a tooltip because the
        // on-screen text already shows what'll be sent.
        if (isUrl) ToolTip.SetTip(tb, command);

        tb.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tb).Properties.IsLeftButtonPressed) return;
            if (isUrl)
            {
                // External URL — route to OS browser handler.
                OnUrlClicked?.Invoke(command);
            }
            else
            {
                // Game-command link — pass both the cmd (server-bound) and the
                // visible display text (Game-window echo). VM handler decides
                // what to do with each.
                OnLinkClicked?.Invoke(command, text);
            }
            e.Handled = true;
        };
        // #177: without an explicit baseline the InlineUIContainer sits its
        // child's box on the text baseline, lifting the link text above the
        // surrounding Runs (it read as superscript — obvious-paths links were
        // visibly raised). TextBottom aligns the child's bottom with the text
        // line's bottom; since the link uses the same inherited font/size as
        // the flow, that puts its glyphs on the same baseline as the rest.
        return new InlineUIContainer
        {
            Child             = tb,
            BaselineAlignment = BaselineAlignment.TextBottom,
        };
    }
}

/// <summary>
/// Data carried on a clickable link inline (via <see cref="Avalonia.Controls.Control.Tag"/>)
/// so a control that owns pointer input can re-dispatch the link without the
/// inline's own <c>PointerPressed</c> handler firing. <paramref name="Display"/>
/// is the visible link text (passed to <see cref="DefaultHighlights.OnLinkClicked"/>
/// as the echo override); <paramref name="Command"/> is the server-bound command.
/// </summary>
public sealed record LinkPayload(string Command, string Display, bool IsUrl);
