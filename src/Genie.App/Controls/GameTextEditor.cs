using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Genie.App.Docking;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Controls;

/// <summary>
/// The experimental AvaloniaEdit-backed renderer for the main Game window
/// (<c>#config useeditorgamewindow on</c>, default off). It hosts a single
/// read-only <see cref="TextEditor"/> and mirrors
/// <see cref="GameTextViewModel.Lines"/> — still the source of truth — into its
/// document, one buffered line per document line.
///
/// <para>Only the <i>rendering</i> moves. Timestamping, the Name-List-Only filter,
/// gags/substitutes, session logging, Copy All and Save As all stay on the
/// view-model and are untouched by which renderer is active.</para>
///
/// <para>Selected out of the legacy path by type, not by a visibility toggle:
/// <see cref="EditorGameTextDocument"/> gets its own <c>DataTemplate</c>, so with
/// the flag off this control is never constructed and the legacy subtree is
/// exactly what it always was.</para>
/// </summary>
public sealed class GameTextEditor : UserControl
{
    /// <summary>How close to the bottom still counts as "following the tail",
    /// in device-independent pixels. Same band <c>AutoScrollState</c> uses for the
    /// legacy ScrollViewer, so the ↓ Bottom button appears at the same moment.</summary>
    private const double AtBottomBand = 10.0;

    private readonly TextEditor  _editor;
    private readonly TextDocument _document = new();
    private readonly List<GameLineEntry> _entries = [];

    private GameTextDocument?   _host;
    private GameTextViewModel?  _vm;
    private FindInWindowModel?  _find;

    private bool _atBottom = true;
    private bool _paused;
    private bool _scrollHooked;

    /// <summary>Focus at the moment of a pointer press, restored on release when the
    /// click produced no selection — the game window must not steal focus from the
    /// command bar for a plain click (legacy <c>LineSelection</c> only calls
    /// <c>Focus()</c> after a real drag).</summary>
    private IInputElement? _focusBeforePress;

    public static readonly DirectProperty<GameTextEditor, bool> IsScrolledUpProperty =
        AvaloniaProperty.RegisterDirect<GameTextEditor, bool>(
            nameof(IsScrolledUp), o => o.IsScrolledUp);

    private bool _isScrolledUp;

    /// <summary>True when the view has been scrolled off the tail — drives the
    /// ↓ Bottom overlay button, mirroring <c>AutoScrollState.IsScrolledUp</c>.</summary>
    public bool IsScrolledUp
    {
        get => _isScrolledUp;
        private set => SetAndRaise(IsScrolledUpProperty, ref _isScrolledUp, value);
    }

    /// <summary>Jump to the newest line and resume following it.</summary>
    public ICommand JumpToBottomCommand { get; }

    public GameTextEditor()
    {
        // GameTextEditorControl, not a stock TextEditor: its TextArea leaves
        // typing and the editing keys unhandled so the window's type-anywhere
        // redirect (#141) still fires when the game text has focus.
        _editor = new GameTextEditorControl
        {
            Document        = _document,
            IsReadOnly      = true,
            ShowLineNumbers = false,
            Background      = Brushes.Transparent,
            Padding         = new Thickness(0),
            // Suppress the built-in Copy/Cut/Paste flyout so a right-click over game
            // text surfaces the window menu on the hosting Grid — the same problem
            // the legacy template solves with a local ContextFlyout null.
            ContextFlyout   = null,
        };

        var o = _editor.Options;
        o.EnableHyperlinks              = false;   // we generate our own <d cmd> links
        o.EnableEmailHyperlinks         = false;
        o.EnableTextDragDrop            = false;   // read-only; dragging text out is not a thing
        o.EnableRectangularSelection    = false;
        o.EnableVirtualSpace            = false;
        o.HighlightCurrentLine          = false;
        o.AllowScrollBelowDocument      = false;
        o.CutCopyWholeLine              = false;   // Ctrl+C with no selection must be a no-op
        o.AcceptsTab                    = false;   // read-only: Tab is focus navigation, not indent

        var area = _editor.TextArea;
        area.CaretBrush   = Brushes.Transparent;   // read-only view: no caret
        area.ContextFlyout = null;
        area.TextView.ContextFlyout = null;
        area.TextView.LineTransformers.Add(new GameTextColorizer(EntryAt));
        area.TextView.ElementGenerators.Add(new GameLinkGenerator(EntryAt, area));

        // The ScrollViewer normally exists by TemplateApplied; LayoutUpdated is the
        // belt-and-braces retry (it unhooks itself the moment the lookup succeeds)
        // so a slow template application can't leave paging permanently unwired.
        _editor.TemplateApplied += (_, _) => HookScrollViewer();
        _editor.LayoutUpdated   += OnEditorLayoutUpdated;

        // Focus etiquette: capture where focus was on the way DOWN (tunnel, before
        // the TextArea takes it) and hand it back on release if nothing got selected.
        AddHandler(PointerPressedEvent, OnPressTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble);

        JumpToBottomCommand = ReactiveCommand.Create(JumpToBottom);

        Content = _editor;
    }

    // ── Host wiring ───────────────────────────────────────────────────────────

    // Subscriptions follow the VISUAL TREE, not the DataContext: a dock re-parent
    // (tab switch, float, re-dock) detaches and re-attaches the control without
    // ever changing its DataContext, so unhooking on DataContext alone would leave
    // a re-attached window permanently frozen. Attach re-seeds from the buffer, so
    // whatever arrived while detached is picked up.

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unsubscribe();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Unsubscribe();
        Subscribe();
    }

    private void Subscribe()
    {
        if (_host is not null) return;                       // already wired
        if (this.GetVisualRoot() is null) return;              // wired on attach instead
        if (DataContext is not GameTextDocument host) return;

        _host = host;
        _vm   = host.ViewModel;
        _find = host.Find;

        host.PropertyChanged        += OnHostPropertyChanged;
        _vm.Lines.CollectionChanged += OnLinesChanged;
        _find.JumpRequested         += OnFindJump;

        ApplyHostSettings();
        _paused = host.IsScrollPaused;
        RebuildAll();
    }

    private void Unsubscribe()
    {
        if (_host is not null) _host.PropertyChanged -= OnHostPropertyChanged;
        if (_vm   is not null) _vm.Lines.CollectionChanged -= OnLinesChanged;
        if (_find is not null) _find.JumpRequested -= OnFindJump;
        _host = null;
        _vm   = null;
        _find = null;
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_host is null) return;
        switch (e.PropertyName)
        {
            case nameof(GameTextDocument.IsScrollPaused):
                SetPaused(_host.IsScrollPaused);
                break;
            default:
                ApplyHostSettings();
                break;
        }
    }

    /// <summary>Push the per-window appearance settings (font, size, foreground,
    /// wrap, horizontal scrollbar) onto the editor. Re-run on every host property
    /// change so Layout-tab edits repaint live, exactly like the legacy bindings.</summary>
    private void ApplyHostSettings()
    {
        if (_host is null) return;
        _editor.FontFamily = _host.ToolFontFamily;
        _editor.FontSize   = _host.ToolFontSize;
        // Null means "inherit the global game colour", which is what the legacy
        // Foreground binding produces — clear the local value rather than leaving
        // the last explicit brush behind.
        if (_host.ToolForeground is { } fg) _editor.Foreground = fg;
        else _editor.ClearValue(ForegroundProperty);
        _editor.WordWrap = _host.ToolTextWrapping == TextWrapping.Wrap;
        _editor.HorizontalScrollBarVisibility = _host.ToolHScroll;
        if (GameTextColorizer.FindResource("Theme.Selection") is IBrush selection)
            _editor.TextArea.SelectionBrush = selection;
    }

    private void OnEditorLayoutUpdated(object? sender, EventArgs e)
    {
        HookScrollViewer();
        if (_scrollHooked) _editor.LayoutUpdated -= OnEditorLayoutUpdated;
    }

    /// <summary>
    /// Register the editor's own ScrollViewer with the shared PageUp/PageDown
    /// targeting (#136) and start tracking the tail. The template only produces the
    /// ScrollViewer on apply, which is why this isn't done in the constructor.
    ///
    /// <para>Found by walking the visual tree: <c>TextEditor.ScrollViewer</c> is
    /// internal in AvaloniaEdit 11.4.1. It is used ONLY for target registration —
    /// <see cref="PageScroll"/> pages through <c>ScrollViewer</c> methods and Ctrl+F
    /// resolves the active window from <c>PageScroll.CurrentTarget.DataContext</c>,
    /// both of which need the real control. Scroll offset itself is always read and
    /// written through <c>(IScrollable)TextView</c> (see <see cref="RemoveHead"/> and
    /// <see cref="UpdateAtBottom"/>): the Phase 2 spike found the editor-level offset
    /// properties did not survive a forced layout pass.</para>
    /// </summary>
    private void HookScrollViewer()
    {
        if (_scrollHooked) return;
        if (_editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } sv) return;
        _scrollHooked = true;
        PageScroll.SetIsTarget(sv, true);
        PageScroll.SetIsDefaultTarget(sv, true);
        _editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => UpdateAtBottom();
        UpdateAtBottom();
    }

    // ── Buffer → document ─────────────────────────────────────────────────────

    /// <summary>Document line number (1-based) → its buffered line. The side list is
    /// kept strictly parallel to the document: index <c>i</c> is always document
    /// line <c>i+1</c>, which is what lets the colorizer and the link generator find
    /// a line's spans from nothing but its line number.</summary>
    private GameLineEntry? EntryAt(int lineNumber)
        => lineNumber >= 1 && lineNumber <= _entries.Count ? _entries[lineNumber - 1] : null;

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add
                when e.NewItems is not null && e.NewStartingIndex == _entries.Count:
                foreach (var item in e.NewItems)
                    if (item is TextLine line) Append(line);
                if (!_paused && _atBottom)
                    Dispatcher.UIThread.Post(_editor.ScrollToEnd, DispatcherPriority.Loaded);
                break;

            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex == 0:
                RemoveHead(e.OldItems?.Count ?? 0);
                break;

            case NotifyCollectionChangedAction.Replace
                when e.NewItems is { Count: 1 } && e.NewItems[0] is TextLine replacement:
                ReplaceLine(e.NewStartingIndex, replacement);
                break;

            case NotifyCollectionChangedAction.Reset:
                RebuildAll();
                break;

            // Anything else (a mid-buffer insert or a Move) would desynchronise the
            // side list; nothing in GameTextViewModel produces one, but rebuilding
            // is cheap insurance against a silently corrupted mapping.
            default:
                RebuildAll();
                break;
        }

        Debug.Assert(_entries.Count == 0 || _document.LineCount == _entries.Count,
                     "GameTextEditor: document lines drifted from the side list");
    }

    private void Append(TextLine line)
    {
        var entry = new GameLineEntry(line);
        var text  = Flatten(line.Text);
        _document.Insert(_document.TextLength, _entries.Count == 0 ? text : "\n" + text);
        _entries.Add(entry);
    }

    private void ReplaceLine(int index, TextLine line)
    {
        if (index < 0 || index >= _entries.Count) { RebuildAll(); return; }
        var docLine = _document.GetLineByNumber(index + 1);
        var text    = Flatten(line.Text);
        // The only producer today (RetokenizeAllLines) keeps the text identical and
        // only wants a repaint; guard the general case anyway.
        if (!string.Equals(_document.GetText(docLine.Offset, docLine.Length), text, StringComparison.Ordinal))
            _document.Replace(docLine.Offset, docLine.Length, text);
        _entries[index] = new GameLineEntry(line);
        _editor.TextArea.TextView.Redraw(docLine);
    }

    /// <summary>
    /// Drop the oldest <paramref name="count"/> lines and hold the reader's place.
    ///
    /// <para>AvaloniaEdit does not anchor scroll: removing text above the viewport
    /// slides everything up under a stationary offset, which is the same
    /// content-shift bug the legacy renderer has. The fix is exact because the
    /// <c>HeightTree</c> that produced the current offset is the same one
    /// <c>GetVisualTopByDocumentLine</c> reads — capture the first visible line's
    /// visual top before the edit, subtract its visual top after, and apply the
    /// difference. It stays exact even for trimmed lines that were never rendered
    /// and carry estimated heights, because those estimates are what the offset
    /// being corrected was built from.</para>
    ///
    /// <para>Only applied when the view is NOT following the tail: when it is, the
    /// view is pinned to the bottom and compensating would fight the pin.</para>
    /// </summary>
    private void RemoveHead(int count)
    {
        if (count <= 0) return;
        if (count >= _entries.Count) { RebuildAll(); return; }

        var view       = _editor.TextArea.TextView;
        var compensate = !_atBottom && view.VisualLinesValid && view.VisualLines.Count > 0;
        var landmark   = 0;
        var topBefore  = 0.0;
        if (compensate)
        {
            landmark  = view.VisualLines[0].FirstDocumentLine.LineNumber;
            topBefore = view.GetVisualTopByDocumentLine(landmark);
        }

        // Line (count+1) is the first survivor; its offset is exactly the length of
        // everything being dropped, delimiters included.
        _document.Remove(0, _document.GetLineByNumber(count + 1).Offset);
        _entries.RemoveRange(0, count);

        var moved = landmark - count;
        if (!compensate || moved < 1) return;
        var scroll   = (IScrollable)view;
        var topAfter = view.GetVisualTopByDocumentLine(moved);
        scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y - (topBefore - topAfter));
    }

    /// <summary>Re-seed the document from the buffer. Used for <c>#clear</c>
    /// (Reset), for the first attach — the view-model has usually already logged a
    /// line or two by then — and as the fallback for any collection change the
    /// incremental path doesn't model.</summary>
    private void RebuildAll()
    {
        _entries.Clear();
        if (_vm is null || _vm.Lines.Count == 0)
        {
            _document.Text = "";
            return;
        }

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < _vm.Lines.Count; i++)
        {
            var line = _vm.Lines[i];
            _entries.Add(new GameLineEntry(line));
            if (i > 0) sb.Append('\n');
            sb.Append(Flatten(line.Text));
        }
        _document.Text = sb.ToString();
        if (!_paused) Dispatcher.UIThread.Post(_editor.ScrollToEnd, DispatcherPriority.Loaded);
    }

    /// <summary>One buffered line must occupy exactly one document line or the
    /// side-list mapping breaks. The parser splits on newlines so embedded ones
    /// don't occur in practice; flatten defensively rather than corrupt every
    /// line number after the offender.</summary>
    private static string Flatten(string text)
        => text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0
            ? text
            : text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    // ── Auto-follow / Pause Scrolling ─────────────────────────────────────────

    private void UpdateAtBottom()
    {
        var scroll = (IScrollable)_editor.TextArea.TextView;
        var max    = scroll.Extent.Height - scroll.Viewport.Height;
        _atBottom  = max <= 0 || scroll.Offset.Y >= max - AtBottomBand;
        IsScrolledUp = !_atBottom;
    }

    /// <summary>"Pause Scrolling": stop following the tail but keep appending.
    /// Unlike the legacy path this needs no scrollback-cap suspension — a trim
    /// while paused is compensated in <see cref="RemoveHead"/>, so the frozen view
    /// genuinely stays put past the cap.</summary>
    private void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (!paused) JumpToBottom();   // resuming catches up to whatever arrived
    }

    private void JumpToBottom()
    {
        _atBottom    = true;
        IsScrolledUp = false;
        Dispatcher.UIThread.Post(_editor.ScrollToEnd, DispatcherPriority.Loaded);
    }

    // ── Find (#120) ───────────────────────────────────────────────────────────

    private void OnFindJump(FindInWindowModel.Match match)
        => Dispatcher.UIThread.Post(() =>
        {
            var lineNumber = match.Line + 1;
            if (lineNumber < 1 || lineNumber > _entries.Count) return;   // trimmed away
            var line = _document.GetLineByNumber(lineNumber);
            _editor.ScrollToLine(lineNumber);
            // Select the hit so it reads at a glance, matching the legacy Find's
            // SelectionStart/End on the matched line.
            var from   = line.Offset + Math.Clamp(match.Col, 0, line.Length);
            var length = Math.Clamp(match.Length, 0, line.EndOffset - from);
            _editor.Select(from, length);
        });

    // ── Focus etiquette ───────────────────────────────────────────────────────

    private void OnPressTunnel(object? sender, PointerPressedEventArgs e)
        => _focusBeforePress = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        var previous = _focusBeforePress;
        _focusBeforePress = null;
        // A drag that produced a selection keeps focus here so Ctrl+C / Ctrl+A act
        // on it; a plain click hands focus straight back to wherever it was (the
        // command bar), which is what the legacy behaviour does.
        if (previous is null || ReferenceEquals(previous, _editor) || _editor.SelectionLength > 0) return;
        Dispatcher.UIThread.Post(() => previous.Focus());
    }
}
