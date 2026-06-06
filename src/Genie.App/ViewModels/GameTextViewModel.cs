using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia.Controls.Documents;
using Genie.App.Highlighting;
using Genie.App.Settings;
using Genie.Core;
using Genie.Core.Events;
using ReactiveUI;

namespace Genie.App.ViewModels;

public class GameTextViewModel : ReactiveObject
{
    private const int MaxLines = 2000;

    public ObservableCollection<TextLine> Lines { get; } = [];

    /// <summary>
    /// Per-tag visibility filters — set by <see cref="MainWindowViewModel"/>
    /// before <see cref="Attach"/> fires. When non-null, lines are filtered
    /// at the subscription site based on their category (Game / Echo /
    /// System-Script). Null = no filtering (designer / unit-test default).
    /// </summary>
    public DisplaySettings? DisplaySettings { get; set; }

    public void Attach(GenieCore core)
    {
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
                var text = core.Substitutes.Apply(e.Text);
                if (core.Gags.ShouldGag(text)) return;
                // Substituting shifts offsets, so drop spans if a sub fired —
                // they refer to positions in the original text. Same rule for
                // link and bold spans.
                var unchanged = ReferenceEquals(text, e.Text);
                var links     = unchanged ? e.Links     : null;
                var bolds     = unchanged ? e.BoldSpans : null;
                AddLine(text, StreamColor.Main, links, bolds);
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

    private void AddLine(string text, StreamColor color,
                         IReadOnlyList<LinkSpan>? links = null,
                         IReadOnlyList<BoldSpan>? bolds = null)
    {
        Lines.Add(new TextLine(text, color, links, bolds));
        while (Lines.Count > MaxLines)
            Lines.RemoveAt(0);
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
}

/// <summary>
/// A single line of game text plus the visual category it belongs to.
/// Visual styling lives on the AXAML side via <c>DynamicResource</c>; this
/// record only exposes <see cref="IsEcho"/> for the <c>Classes.echo</c>
/// binding and <see cref="Inlines"/> for the highlighted-text renderer.
/// </summary>
public record TextLine(string Text, StreamColor Color,
                       IReadOnlyList<LinkSpan>? Links = null,
                       IReadOnlyList<BoldSpan>? BoldSpans = null)
{
    public bool IsEcho => Color == StreamColor.System;

    /// <summary>
    /// The line broken into styled <see cref="Inline"/> segments by
    /// <see cref="DefaultHighlights"/>. Echo lines bypass highlighting and
    /// render as a single plain run (the AXAML class selector handles their
    /// italic + colour). Each access produces a fresh list — Avalonia
    /// <see cref="Inline"/>s can only belong to one parent, so we don't cache.
    /// </summary>
    public IReadOnlyList<Inline> Inlines =>
        IsEcho ? [new Run(Text)] : DefaultHighlights.Tokenize(Text, Links, BoldSpans);
}

public enum StreamColor { Main, Logons, Talk, Whisper, Thought, Combat, Familiar, System }
