using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Genie.App.Highlighting;
using Genie.Core.Events;

namespace Genie.App.Controls;

/// <summary>
/// Turns each of a line's <c>&lt;d cmd="…"&gt;</c> / <c>&lt;a href="…"&gt;</c> spans
/// into a clickable visual-line element, the AvaloniaEdit equivalent of the
/// <c>InlineUIContainer</c> the legacy renderer emits. Modelled on AvaloniaEdit's
/// own <c>LinkElementGenerator</c> / <c>VisualLineLinkText</c> pair.
///
/// <para>The spans come from <see cref="GameLineEntry.Links"/> — the same validated,
/// start-ordered list the legacy emit pass walks — so <c>ShowLinks=false</c>
/// (<see cref="DefaultHighlights.LinksEnabled"/>) and out-of-bounds spans are
/// already filtered out upstream and this generator simply produces nothing for
/// them.</para>
/// </summary>
internal sealed class GameLinkGenerator : VisualLineElementGenerator
{
    private readonly Func<int, GameLineEntry?> _entryAt;
    private readonly TextArea _textArea;
    private readonly LinkClickArbiter _clicks;

    internal GameLinkGenerator(Func<int, GameLineEntry?> entryAt, TextArea textArea)
    {
        _entryAt  = entryAt;
        _textArea = textArea;
        _clicks   = new LinkClickArbiter(textArea);
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (Resolve(startOffset, out var lineStart, out var relative) is not { } links) return -1;
        foreach (var link in links)
        {
            // Links are start-ordered, so the first one reaching past `relative`
            // is the next point of interest. A span that straddles startOffset can
            // only happen if the view asks mid-element; start it where we are.
            if (link.Start >= relative) return lineStart + link.Start;
            if (link.Start + link.Length > relative) return startOffset;
        }
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (Resolve(offset, out var lineStart, out var relative) is not { } links) return null;
        foreach (var link in links)
        {
            if (link.Start != relative) continue;
            // Never overrun the visual line: an element longer than the remaining
            // document line would corrupt the visual-column mapping.
            var length = Math.Min(link.Length, CurrentContext.VisualLine.LastDocumentLine.EndOffset - offset);
            if (length <= 0) return null;
            return new GameLinkElement(CurrentContext.VisualLine, length, link, _textArea, _clicks);
        }
        return null;
    }

    /// <summary>Map a document offset to (start of its document line, offset within
    /// that line) and hand back that line's link spans. Null when the line has no
    /// buffered metadata — the side list and the document can be a beat apart.</summary>
    private IReadOnlyList<LinkSpan>? Resolve(int offset, out int lineStart, out int relative)
    {
        lineStart = 0;
        relative  = 0;
        var context = CurrentContext;
        if (context is null) return null;
        var docLine = context.Document.GetLineByOffset(offset);
        var entry   = _entryAt(docLine.LineNumber);
        if (entry is null || entry.Links.Count == 0) return null;
        lineStart = docLine.Offset;
        relative  = offset - docLine.Offset;
        return entry.Links;
    }
}

/// <summary>
/// A run of link text inside the game window: Wrayth-blue (or URL-green) and
/// underlined, hand cursor on hover, and a click that dispatches through the very
/// same handlers the legacy renderer uses.
///
/// <para><b>Release without a drag</b>, matching the legacy window. AvaloniaEdit's
/// own <c>VisualLineLinkText</c> fires on press, which cost us the ability to start
/// a selection on link text — and inventory and exit-heavy screens are mostly link
/// text. So the press only <i>arms</i> the click and is deliberately left unhandled,
/// letting <see cref="TextArea"/>'s normal selection machinery run; the decision is
/// made by <see cref="LinkClickArbiter"/> at release.</para>
/// </summary>
internal sealed class GameLinkElement : VisualLineText
{
    private readonly LinkSpan _link;
    private readonly TextArea _textArea;
    private readonly LinkClickArbiter _clicks;

    internal GameLinkElement(VisualLine parentVisualLine, int length, LinkSpan link,
                             TextArea textArea, LinkClickArbiter clicks)
        : base(parentVisualLine, length)
    {
        _link     = link;
        _textArea = textArea;
        _clicks   = clicks;
    }

    public override Avalonia.Media.TextFormatting.TextRun CreateTextRun(
        int startVisualColumn, ITextRunConstructionContext context)
    {
        // Set here rather than from the colorizer: transformers run before the run
        // is created, so this is the last word on a link's appearance — which is
        // what we want, since a link paints as a link regardless of what highlight
        // rules matched the same characters (legacy parity).
        TextRunProperties.SetForegroundBrush(DefaultHighlights.LinkForeground(_link.IsUrl));
        TextRunProperties.SetTextDecorations(TextDecorations.Underline);
        return base.CreateTextRun(startVisualColumn, context);
    }

    protected override VisualLineText CreateInstance(int length)
        => new GameLinkElement(ParentVisualLine, length, _link, _textArea, _clicks);

    protected override void OnQueryCursor(PointerEventArgs e)
    {
        e.Handled = true;
        _textArea.Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_textArea);
        if (!pt.Properties.IsLeftButtonPressed) return;   // right/middle pass through
        // Arm only. Crucially `e.Handled` stays false, so TextArea still begins its
        // own selection from this point — that is what makes a drag starting on link
        // text select instead of navigating. DisplayText() is read now, while this
        // element is still attached to a live visual line.
        _clicks.Arm(_link, DisplayText(), pt.Position);
    }

    /// <summary>The visible link text, read back off the document so a split
    /// element still reports the whole span (the legacy path passes the full
    /// display text, not the fragment under the pointer).</summary>
    private string DisplayText()
    {
        var doc  = ParentVisualLine.Document;
        var line = ParentVisualLine.FirstDocumentLine;
        var from = line.Offset + _link.Start;
        var len  = Math.Min(_link.Length, Math.Max(0, doc.TextLength - from));
        return len <= 0 ? "" : doc.GetText(from, len);
    }
}

/// <summary>
/// Decides whether a press that landed on link text was a click or the start of a
/// selection drag, and dispatches only in the former case — the same rule
/// <c>SelectableLinesControl</c> applies on the legacy path, including its 3 px
/// threshold and its latch-once semantics (drag out and back is still a drag).
///
/// <para>Lives here rather than in <see cref="GameLinkElement"/> because
/// <c>VisualLineElement</c> exposes no release hook: elements are rebuilt on every
/// redraw and only see <c>OnPointerPressed</c>, so the release has to be observed on
/// the <see cref="TextArea"/>. One arbiter per generator, i.e. one per editor.</para>
/// </summary>
internal sealed class LinkClickArbiter
{
    private const double DragThreshold = 3.0;   // matches SelectableLinesControl

    private readonly TextArea _textArea;

    private LinkSpan? _armed;
    private string    _display = "";
    private Point     _pressPoint;
    private bool      _dragged;

    internal LinkClickArbiter(TextArea textArea)
    {
        _textArea = textArea;
        // Tunnel for the press so this runs *before* TextArea's own handling, which
        // is what dispatches into the element that arms us — otherwise a press on
        // ordinary text would never clear a link armed by an earlier press.
        textArea.AddHandler(InputElement.PointerPressedEvent, OnAnyPressed,
                            RoutingStrategies.Tunnel);
        // Bubble + handledEventsToo for move/release: TextArea marks both handled
        // while it is extending a selection, and those are exactly the events that
        // tell us this was a drag.
        textArea.AddHandler(InputElement.PointerMovedEvent, OnMoved,
                            RoutingStrategies.Bubble, handledEventsToo: true);
        textArea.AddHandler(InputElement.PointerReleasedEvent, OnReleased,
                            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Called from a link element's press handler: this press landed on a
    /// link, so a release without a drag should dispatch it.</summary>
    internal void Arm(LinkSpan link, string display, Point pressPoint)
    {
        _armed      = link;
        _display    = display;
        _pressPoint = pressPoint;
        _dragged    = false;
    }

    private void OnAnyPressed(object? sender, PointerPressedEventArgs e) => _armed = null;

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_armed is null || _dragged) return;
        var p = e.GetPosition(_textArea);
        var dx = p.X - _pressPoint.X;
        var dy = p.Y - _pressPoint.Y;
        if (Math.Sqrt((dx * dx) + (dy * dy)) > DragThreshold) _dragged = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        var link = _armed;
        _armed = null;
        if (link is null || _dragged) return;
        if (e.InitialPressMouseButton != MouseButton.Left) return;

        // Identical dispatch to SelectableLinesControl's release handler: URLs go to
        // the OS-browser handler, game commands carry both the server-bound command
        // and the visible display text (the Game-window echo override).
        if (link.IsUrl) DefaultHighlights.OnUrlClicked?.Invoke(link.Command);
        else            DefaultHighlights.OnLinkClicked?.Invoke(link.Command, _display);
    }
}
