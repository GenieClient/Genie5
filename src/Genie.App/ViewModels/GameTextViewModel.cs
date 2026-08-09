using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Genie.App.Highlighting;
using Genie.App.Settings;
using Genie.Core;
using Genie.Core.Events;
using Genie.Core.Layout;
using ReactiveUI;

namespace Genie.App.ViewModels;

public class GameTextViewModel : ReactiveObject
{
    // Scrollback cap — how many rendered lines to keep before trimming the
    // oldest. Set from GenieConfig.ScrollbackLines on Attach (default 2000);
    // the config value is already clamped to [100, 100000].
    private int _maxLines = 2000;

    // Tracks whether the most recently appended line was a game prompt, so the
    // PromptEvent subscription can dedup consecutive prompts (Genie 4's
    // LastRowWasPrompt). Set by AddLine; only the prompt path passes true.
    private bool _lastLineWasPrompt;

    // Live player status, mirrored from <indicator> XML (IndicatorEvent), used
    // by the promptforce composer to reconstruct the prompt's status letters
    // even when DR sends a bare ">". Roundtime ("R") is read live from
    // GameState.Combat at compose time, not stored here.
    private bool _stKneeling, _stSitting, _stProne, _stStunned, _stHidden,
                 _stInvisible, _stWebbed, _stBleeding, _stJoined, _stDead;

    public ObservableCollection<TextLine> Lines { get; } = [];

    /// <summary>Live per-window settings for the main game window ("game-text"),
    /// assigned by <see cref="Genie.App.Docking.GameTextDocument"/>. The instance
    /// is mutated in place by the Layout tab, so reading
    /// <see cref="WindowSettings.Timestamp"/> at append time always reflects the
    /// latest toggle (#90).</summary>
    public WindowSettings? Settings { get; set; }

    /// <summary>Live Names engine (assigned in <see cref="Attach"/>), used by the
    /// <see cref="NameListOnly"/> filter.</summary>
    public Genie.Core.Highlights.NameHighlightEngine? Names { get; set; }

    /// <summary>Genie 4 "Name List Only" — when true the main game feed only
    /// shows lines mentioning a name in the player's Names list. Toggled from the
    /// window right-click menu; mirrors <see cref="WindowSettings.NameListOnly"/>.
    /// Applies to game text only (echoes / prompts / script lines pass through so
    /// the window stays usable while filtered).</summary>
    public bool NameListOnly { get; set; }

    /// <summary>
    /// Concat every visible line into a single newline-separated string and
    /// push it to the OS clipboard. Workaround for the Avalonia
    /// <see cref="Avalonia.Controls.SelectableTextBlock"/> limitation that
    /// each rendered line is its own selection-island — drag-selecting across
    /// the <c>exp all</c> dump stops at the first line. Bound to Ctrl+Shift+C
    /// via <c>MainWindow.KeyBindings</c>. (Full visual multi-line drag-select
    /// is on the backlog as a custom selection-model refactor; this is the
    /// pragmatic "I want to paste the whole dump elsewhere" path.)
    /// </summary>
    public ReactiveCommand<Unit, Unit> CopyAllCommand { get; }

    public GameTextViewModel()
    {
        CopyAllCommand = ReactiveCommand.CreateFromTask(CopyAllToClipboardAsync);
    }

    private async System.Threading.Tasks.Task CopyAllToClipboardAsync()
    {
        if (Lines.Count == 0) return;
        var sb = new StringBuilder(Lines.Count * 64);
        foreach (var line in Lines)
            sb.AppendLine(line.Text);
        var top = (Application.Current?.ApplicationLifetime
                       as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (top?.Clipboard is { } cb)
        {
            await cb.SetTextAsync(sb.ToString());
            AddSystemLine($"[copied {Lines.Count} line(s) to clipboard]");
        }
    }

    /// <summary>
    /// Per-tag visibility filters — set by <see cref="MainWindowViewModel"/>
    /// before <see cref="Attach"/> fires. When non-null, lines are filtered
    /// at the subscription site based on their category (Game / Echo /
    /// System-Script). Null = no filtering (designer / unit-test default).
    /// </summary>
    public DisplaySettings? DisplaySettings { get; set; }

    public void Attach(GenieCore core)
    {
        // Scrollback cap from config (already clamped to [100, 100000]).
        _maxLines = core.Config?.ScrollbackLines ?? 2000;

        // Live Names engine for the "Name List Only" right-click filter.
        Names = core.NameHighlights;

        // ── Main-stream game text ──────────────────────────────────────────
        // Genie 4 applies the substitute pass first, then the gag check —
        // so a substitute can rewrite a line to one that a gag matches
        // (rare, but the canonical ordering).
        //
        // Link spans (from <d cmd="..."> in the XML) are carried through
        // unchanged ONLY when no substitute fired — substituting would
        // shift offsets and invalidate the spans. When a sub fires we drop
        // the links rather than try to remap; clickable text is a UX bonus,
        // not a correctness requirement.
        core.GameEvents
            .OfType<TextEvent>()
            .Where(e => e.Stream == "main")
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                if (DisplaySettings?.ShowGameText == false) return;
                // DR's bare re-send of a talk/whispers line already shown via
                // its own stream (DrXmlParser.TextEvent.DuplicateEcho) — Core
                // consumers (triggers/scripts) still need the event, but a
                // display sink would just be showing the same line twice.
                if (e.DuplicateEcho) return;
                // Name List Only: drop game lines that don't mention a tracked
                // name. Guarded on a non-empty Names list so the toggle never
                // blanks the window when no names are configured (see StreamBuffer).
                if (NameListOnly && Names is { Rules.Count: > 0 } && Names.Match(e.Text) is null)
                    return;
                // Timed into their own stages for the perf overlay (zero overhead
                // when disabled). Substitute pass first, then the gag check.
                var text = core.Metrics.Time(Genie.Core.Diagnostics.PipelineStage.Substitutes,
                                             () => core.Substitutes.Apply(e.Text));
                if (core.Metrics.Time(Genie.Core.Diagnostics.PipelineStage.Gags,
                                      () => core.Gags.ShouldGag(text))) return;
                // Condensed mode (Genie 4): drop blank / whitespace-only lines
                // from the main window so output reads compact. Read live so a
                // #config condensed change applies immediately. Room text is on
                // its own stream, so it's naturally exempt (Genie 4 kept it).
                if (core.Config?.Condensed == true && string.IsNullOrWhiteSpace(text)) return;
                // Substituting shifts offsets, so drop spans if a sub fired —
                // they refer to positions in the original text. Same rule for
                // link and bold spans.
                var unchanged = ReferenceEquals(text, e.Text);
                var links     = unchanged ? e.Links       : null;
                var bolds     = unchanged ? e.BoldSpans   : null;
                var presets   = unchanged ? e.PresetSpans : null;
                AddLine(text, StreamColor.Main, links, bolds, presets, mono: e.Mono);
            });

        // ── Game prompt ───────────────────────────────────────────────────
        // DR sends a <prompt> after every server batch (steady-state ">"), so
        // rendering every one would flood the window. We mirror Genie 4:
        //   • dedup — never show two prompt lines in a row; a prompt only
        //     reappears once real output has arrived since the last one
        //     (AddLine resets _lastLineWasPrompt for every non-prompt line), and
        //   • promptbreak — when false, suppress the standalone prompt line
        //     entirely so output flows uninterrupted (default true = show it).
        // The displayed string is the status letters followed by Config.Prompt
        // ("> "), so a bare ">" renders as "> " and "R>" renders as "R> ".
        //   • promptforce off — show the server's own indicator letters as-is
        //     (the chars before ">"), so the prompt mirrors exactly what DR sent.
        //   • promptforce on (default) — reconstruct the letters from the live
        //     <indicator> state + roundtime, so the prompt is always accurate
        //     even when DR sends a bare ">" while separately flagging hidden/RT.
        // Read live so #config prompt / promptbreak / promptforce apply at once.
        core.GameEvents
            .OfType<PromptEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                if (DisplaySettings?.ShowGameText == false) return;
                if (core.Config?.PromptBreak == false) return; // promptbreak off → no prompt line
                if (_lastLineWasPrompt) return;                // dedup back-to-back prompts
                var promptStr = core.Config?.Prompt ?? "> ";
                var status = core.Config?.PromptForce == true
                    ? ComposeStatusLetters(core)
                    : (e.Indicator ?? string.Empty).TrimEnd('>', ' ');
                AddLine(status + promptStr, StreamColor.Main, isPrompt: true);
            });

        // ── Player status (for the promptforce composer) ──────────────────
        // Mirror <indicator> XML into local flags. VitalsViewModel keeps its
        // own copies for the status bar. GameState.ActiveStatuses now covers
        // all ten (incl. Invisible/Joined), but the prompt composer wants the
        // values as individual bools, so we keep this lightweight local mirror
        // rather than project the HashSet on every prompt.
        core.GameEvents
            .OfType<IndicatorEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                switch (e.IndicatorId.ToUpperInvariant())
                {
                    case "ICONKNEELING":  _stKneeling  = e.Visible; break;
                    case "ICONSITTING":   _stSitting   = e.Visible; break;
                    case "ICONPRONE":     _stProne     = e.Visible; break;
                    case "ICONSTUNNED":   _stStunned   = e.Visible; break;
                    case "ICONHIDDEN":    _stHidden    = e.Visible; break;
                    case "ICONINVISIBLE": _stInvisible = e.Visible; break;
                    case "ICONWEBBED":    _stWebbed    = e.Visible; break;
                    case "ICONBLEEDING":  _stBleeding  = e.Visible; break;
                    case "ICONJOINED":    _stJoined    = e.Visible; break;
                    case "ICONDEAD":      _stDead      = e.Visible; break;
                }
            });

        // ── Local echoes: typed commands + system diagnostics ─────────────
        // EchoLine carries non-script lines of two kinds:
        //   (a) typed-command echoes ("> look", "> n", …) — what we call "Echo"
        //   (b) system diagnostics ([plugin] …, [layout] …, [recorder] …) — a
        //       bracketed tag, grouped with "Script" for filtering purposes.
        // The split is detected by prefix: a line starting with [xxx] is a
        // bracketed system tag. Script-originated lines no longer arrive here —
        // they come via ScriptOutputLine (below) — so there's no double render.
        // Both render with StreamColor.System styling (italic-gray); the filter
        // just decides whether to render at all.
        Observable.FromEvent<Action<string>, string>(
                h => core.EchoLine += h,
                h => core.EchoLine -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(msg =>
            {
                var isSystemTag = msg.StartsWith("[", StringComparison.Ordinal);
                if (isSystemTag)
                {
                    if (DisplaySettings?.ShowScriptText == false) return;
                }
                else
                {
                    if (DisplaySettings?.ShowEchoText == false) return;
                }
                AddLine(msg, StreamColor.System);
            });

        // ── Styled #echo (colour / mono, no >window) ──────────────────────
        // `#echo Yellow foo` / `#echo mono foo` from the command bar or a
        // script: a main-window line carrying an explicit colour and/or a
        // monospaced font. Governed by the same Echo-text filter as a plain
        // #echo; rendered via the explicit-style path on TextLine.
        Observable.FromEvent<Action<string, string?, bool>, (string Msg, string? Color, bool Mono)>(
                rx => (t, c, m) => rx((t, c, m)),
                h => core.EchoStyledLine += h,
                h => core.EchoStyledLine -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t =>
            {
                if (DisplaySettings?.ShowEchoText == false) return;
                AddEcho(t.Msg, t.Color, t.Mono);
            });

        // ── Script-originated lines ───────────────────────────────────────
        // Everything a script produces arrives on ScriptOutputLine: its
        // echo output, [script]/[dbg] diagnostics, AND the game commands it
        // issues (`put north`, …). All classified as Script lines so the
        // "Script Lines" toggle governs the whole of a script's activity.
        Observable.FromEvent<Action<string>, string>(
                h => core.ScriptOutputLine += h,
                h => core.ScriptOutputLine -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(msg =>
            {
                if (DisplaySettings?.ShowScriptText == false) return;
                AddLine(msg, StreamColor.System);
            });

        // ── Highlight-rule changes: re-tokenize already-rendered lines so
        // newly added rules repaint visible text, not just future lines.
        Highlighting.UserHighlights.RulesChanged += RetokenizeAllLines;
    }

    /// <summary>
    /// Force every existing <see cref="TextLine"/> to be re-tokenized by
    /// replacing it with a fresh instance carrying identical content. The
    /// <see cref="ObservableCollection{T}"/> raises Replace events, the
    /// ItemsControl re-binds each item, and <see cref="TextLine.Inlines"/>
    /// is re-evaluated against the current rule set.
    /// </summary>
    private void RetokenizeAllLines()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < Lines.Count; i++)
            {
                var existing = Lines[i];
                Lines[i] = new TextLine(existing.Text, existing.Color);
            }
        });
    }

    /// <summary>
    /// Build the prompt's status-letter prefix from the live indicator flags
    /// and current roundtime — the promptforce path. Letter order matches
    /// Genie 4 (Core/Game.cs prompt composition): K s P S H I W ! J R. A dead
    /// character shows "DEAD" instead of letters (Genie 4 special case).
    /// Returns "" when nothing is active, so a bare prompt renders as just
    /// the configured prompt string ("> ").
    /// </summary>
    private string ComposeStatusLetters(GenieCore core)
    {
        if (_stDead) return "DEAD";
        var sb = new StringBuilder(10);
        if (_stKneeling)  sb.Append('K');
        if (_stSitting)   sb.Append('s');
        if (_stProne)     sb.Append('P');
        if (_stStunned)   sb.Append('S');
        if (_stHidden)    sb.Append('H');
        if (_stInvisible) sb.Append('I');
        if (_stWebbed)    sb.Append('W');
        if (_stBleeding)  sb.Append('!');
        if (_stJoined)    sb.Append('J');
        if (core.State?.Combat.InRoundTime == true) sb.Append('R');
        return sb.ToString();
    }

    private void AddLine(string text, StreamColor color,
                         IReadOnlyList<LinkSpan>? links = null,
                         IReadOnlyList<BoldSpan>? bolds = null,
                         IReadOnlyList<PresetSpan>? presets = null,
                         bool isPrompt = false,
                         bool mono = false)
    {
        // #90: per-window timestamp. When the "game-text" window has the Layout-
        // tab "prepend timestamp to each line" toggle on, stamp each content line
        // as it arrives. Bare prompts are skipped (a stamped ">" is just noise).
        // The link/bold/preset spans are ABSOLUTE offsets into the text, so they
        // must be shifted right by the prefix length or highlights and clickable
        // links would land on the wrong characters.
        if (!isPrompt && Settings?.Timestamp == true)
        {
            var prefix = WindowTimestamp.Prefix();
            var shift  = prefix.Length;
            text    = prefix + text;
            links   = links?.Select(s   => s with { Start = s.Start + shift }).ToList();
            bolds   = bolds?.Select(s   => s with { Start = s.Start + shift }).ToList();
            presets = presets?.Select(s => s with { Start = s.Start + shift }).ToList();
        }
        Lines.Add(new TextLine(text, color, links, bolds, presets, Mono: mono));
        TrimScrollback();
        // Only a prompt line arms the dedup; every other line clears it so the
        // next prompt is allowed through (Genie 4 LastRowWasPrompt semantics).
        _lastLineWasPrompt = isPrompt;
    }

    /// <summary>
    /// Drop oldest lines when over the scrollback cap. Trimming is deferred when
    /// a <see cref="ObservableCollection{T}.CollectionChanged"/> is already in
    /// flight (Avalonia ItemsControl / nested Add) — mutating inside that event
    /// throws <c>Cannot change ObservableCollection during a CollectionChanged event</c>.
    /// </summary>
    private void TrimScrollback()
    {
        if (Lines.Count <= _maxLines) return;
        try
        {
            while (Lines.Count > _maxLines)
                Lines.RemoveAt(0);
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains("CollectionChanged", StringComparison.Ordinal))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                while (Lines.Count > _maxLines)
                    Lines.RemoveAt(0);
            });
        }
    }

    /// <summary>
    /// Add a line that originated from a side stream (logons, talk, whispers, …)
    /// when its tool is closed. Prefixes the stream name in brackets so the
    /// reader can tell what channel it came from.
    /// </summary>
    public void AddStreamLine(string stream, string text)
        => AddLine($"[{stream}] {text}", StreamColor.Main);

    /// <summary>
    /// Echo a side-stream line into the main window because that stream's
    /// "Also show in Main window" (<c>WindowSettings.EchoToMain</c>) toggle is
    /// on. Unlike <see cref="AddStreamLine"/> (the panel-<i>closed</i> path)
    /// this adds no bracket prefix, so the line reads inline as ordinary main
    /// text — matching Genie 4's per-stream "show in main". The stream's own
    /// panel still receives the line separately.
    /// <para>
    /// #187: the parser's span metadata (bold / link / preset) rides along so
    /// the echoed line renders identically to a native main-stream line — most
    /// visibly, the combat stream's <c>&lt;pushBold&gt;</c> hit result shows in
    /// the gold monster-bold colour instead of plain white. <see cref="AddLine"/>
    /// shifts the (absolute) span offsets for the timestamp prefix when the
    /// game-window Timestamp toggle is on.
    /// </para>
    /// </summary>
    public void EchoStreamToMain(string text,
                                 IReadOnlyList<BoldSpan>? bolds = null,
                                 IReadOnlyList<LinkSpan>? links = null,
                                 IReadOnlyList<PresetSpan>? presets = null)
        => AddLine(text, StreamColor.Main, links, bolds, presets);

    /// <summary>
    /// Add a client-side system / diagnostic line — recorder status, internal
    /// notices, etc. Uses the System colour so it visually distinguishes from
    /// game text.
    /// </summary>
    public void AddSystemLine(string text)
        => AddLine(text, StreamColor.System);

    /// <summary>
    /// Add a styled <c>#echo</c> line to the main window — an explicit colour
    /// (named or <c>#rrggbb</c>) and/or a monospaced font (Genie 4 <c>#echo</c>
    /// colour / <c>mono</c> options). Renders as a single plain run carrying the
    /// requested style; unparseable colours fall back to the default echo colour.
    /// </summary>
    /// <summary>
    /// Add a Genie 4 <c>#link</c> clickable menu line to the main window — the
    /// whole line is a link (a single <see cref="LinkSpan"/> covering it) that
    /// runs <paramref name="command"/> via the normal link-click path when
    /// clicked. Goes through <see cref="AddLine"/> so timestamping shifts the
    /// span correctly and the scrollback cap applies.
    /// </summary>
    public void AddLink(string text, string command)
        => AddLine(text, StreamColor.Main,
                   links: new[] { new LinkSpan(0, text.Length, command) });

    /// <summary>Empty the main game window (Genie 4 <c>#clear</c>).</summary>
    public void Clear() => Lines.Clear();

    public void AddEcho(string text, string? color, bool mono)
    {
        if (Settings?.Timestamp == true)              // #90: stamp echoes too
            text = WindowTimestamp.Prefix() + text;
        Lines.Add(new TextLine(text, StreamColor.System, EchoColor: color, Mono: mono));
        TrimScrollback();
        _lastLineWasPrompt = false;
    }
}

/// <summary>
/// A single line of game text plus the visual category it belongs to.
/// Visual styling lives on the AXAML side via <c>DynamicResource</c>; this
/// record only exposes <see cref="IsEcho"/> for the <c>Classes.echo</c>
/// binding and <see cref="Inlines"/> for the highlighted-text renderer.
/// </summary>
public record TextLine(string Text, StreamColor Color,
                       IReadOnlyList<LinkSpan>? Links = null,
                       IReadOnlyList<BoldSpan>? BoldSpans = null,
                       IReadOnlyList<PresetSpan>? PresetSpans = null,
                       string? EchoColor = null,
                       bool Mono = false,
                       string Window = "main")
{
    public bool IsEcho => Color == StreamColor.System;

    /// <summary>Monospaced font for <c>#echo mono</c> lines — falls back through
    /// the chain if the first family is unavailable. Internal (not private) so the
    /// AvaloniaEdit renderer's colorizer can force the identical family for a mono
    /// line instead of keeping a second copy of the fallback chain (#178).</summary>
    internal static readonly FontFamily MonoFont = new("Consolas,Courier New,monospace");

    /// <summary>
    /// The line broken into styled <see cref="Inline"/> segments by
    /// <see cref="DefaultHighlights"/>. Echo lines bypass highlighting and
    /// render as a single plain run (the AXAML class selector handles their
    /// italic + colour). A styled <c>#echo</c> (<see cref="EchoColor"/> /
    /// <see cref="Mono"/>) sets the run's foreground/font directly so it
    /// overrides the class styling. Each access produces a fresh list — Avalonia
    /// <see cref="Inline"/>s can only belong to one parent, so we don't cache.
    /// </summary>
    public IReadOnlyList<Inline> Inlines
    {
        get
        {
            // Echo lines (#echo / script output) render as one run, optionally
            // styled with a colour and/or the monospace font (EchoColor implies
            // an echo line, so this branch owns both #echo cases).
            if (IsEcho)
            {
                if (EchoColor is null && !Mono) return [new Run(Text)];
                var run = new Run(Text);
                if (Mono) run.FontFamily = MonoFont;
                if (EchoColor is not null && TryParseColor(EchoColor, out var c))
                    run.Foreground = new SolidColorBrush(c);
                return [run];
            }

            // Game text: full highlighting. A <output class="mono"> block —
            // maps, stat tables, appraisals — keeps its highlights but renders
            // in the monospace font, so a proportional game font doesn't break
            // the column alignment (public #178).
            var inlines = DefaultHighlights.Tokenize(Text, Links, BoldSpans, PresetSpans, Window);
            if (Mono)
                foreach (var inl in inlines)
                    inl.FontFamily = MonoFont;
            return inlines;
        }
    }

    /// <summary>The explicit <c>#echo</c> colour as a brush, or null when the line
    /// carries none / it doesn't parse (caller keeps the default echo colour).
    /// Same parse as the <see cref="Inlines"/> echo branch, exposed so the
    /// AvaloniaEdit colorizer resolves it identically instead of re-deriving it.</summary>
    internal IBrush? EchoForeground()
        => EchoColor is not null && TryParseColor(EchoColor, out var c)
            ? new SolidColorBrush(c)
            : null;

    /// <summary>Parse a Genie 4 echo colour — a named colour (Yellow, DodgerBlue)
    /// or <c>#rrggbb</c> hex. Returns false (caller keeps the default colour) on
    /// anything Avalonia can't parse.</summary>
    private static bool TryParseColor(string token, out Avalonia.Media.Color color)
    {
        try { color = Avalonia.Media.Color.Parse(token); return true; }
        catch { color = default; return false; }
    }
}

public enum StreamColor { Main, Logons, Talk, Whisper, Thought, Combat, Familiar, System }
