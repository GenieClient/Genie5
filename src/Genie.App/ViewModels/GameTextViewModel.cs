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
                AddLine(text, StreamColor.Main, links, bolds, presets);
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
                         bool isPrompt = false)
    {
        Lines.Add(new TextLine(text, color, links, bolds, presets));
        while (Lines.Count > _maxLines)
            Lines.RemoveAt(0);
        // Only a prompt line arms the dedup; every other line clears it so the
        // next prompt is allowed through (Genie 4 LastRowWasPrompt semantics).
        _lastLineWasPrompt = isPrompt;
    }

    /// <summary>
    /// Add a line that originated from a side stream (logons, talk, whispers, …)
    /// when its tool is closed. Prefixes the stream name in brackets so the
    /// reader can tell what channel it came from.
    /// </summary>
    public void AddStreamLine(string stream, string text)
        => AddLine($"[{stream}] {text}", StreamColor.Main);

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
    public void AddEcho(string text, string? color, bool mono)
    {
        Lines.Add(new TextLine(text, StreamColor.System, EchoColor: color, Mono: mono));
        while (Lines.Count > _maxLines)
            Lines.RemoveAt(0);
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
                       bool Mono = false)
{
    public bool IsEcho => Color == StreamColor.System;

    /// <summary>Monospaced font for <c>#echo mono</c> lines — falls back through
    /// the chain if the first family is unavailable.</summary>
    private static readonly FontFamily MonoFont = new("Consolas,Courier New,monospace");

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
            if (EchoColor is not null || Mono)
            {
                var run = new Run(Text);
                if (Mono) run.FontFamily = MonoFont;
                if (EchoColor is not null && TryParseColor(EchoColor, out var c))
                    run.Foreground = new SolidColorBrush(c);
                return [run];
            }
            return IsEcho ? [new Run(Text)]
                          : DefaultHighlights.Tokenize(Text, Links, BoldSpans, PresetSpans);
        }
    }

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
