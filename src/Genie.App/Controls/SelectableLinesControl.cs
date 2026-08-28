using System;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.Highlighting;
using Genie.App.ViewModels;

namespace Genie.App.Controls;

/// <summary>
/// Attached behavior that adds <b>browser-style selection across line items</b>
/// to a plain <see cref="ItemsControl"/> of game-text lines — something
/// Avalonia's per-control text selection can't do on its own.
///
/// <para>Set <c>controls:LineSelection.Enabled="True"</c> on the
/// <see cref="ItemsControl"/>. Implemented as a behavior on the <i>stock</i>
/// control (rather than an <c>ItemsControl</c> subclass) on purpose: subclassing
/// a templated control drops its default control-theme and renders a blank list.
/// The behavior owns pointer input over the list, hit-tests point → (line, char)
/// via each line's <see cref="Avalonia.Controls.TextBlock.TextLayout"/>, and
/// drives every line's <c>SelectionStart/End</c> so the native highlight spans
/// the cross-line range. <c>Ctrl+C</c> / <c>Ctrl+A</c> copy as plain text; a
/// click without a drag clears the selection and (if it hit a link)
/// re-dispatches it.</para>
/// </summary>
public sealed class LineSelection
{
    private LineSelection() { }   // holder for the attached property; not instantiable

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<LineSelection, ItemsControl, bool>("Enabled");

    public static void SetEnabled(ItemsControl c, bool value) => c.SetValue(EnabledProperty, value);
    public static bool GetEnabled(ItemsControl c) => c.GetValue(EnabledProperty);

    /// <summary>The live behavior backing a list, stashed so the window menu's
    /// "Copy" item can read the current cross-line selection (the behavior holds
    /// the selection state; the rendered per-line SelectionStart/End is just a
    /// view of it).</summary>
    internal static readonly AttachedProperty<LineSelectionBehavior?> BehaviorProperty =
        AvaloniaProperty.RegisterAttached<LineSelection, ItemsControl, LineSelectionBehavior?>("Behavior");

    static LineSelection()
    {
        EnabledProperty.Changed.AddClassHandler<ItemsControl>((ic, e) =>
        {
            if (e.GetNewValue<bool>()) ic.SetValue(BehaviorProperty, new LineSelectionBehavior(ic));
        });
    }

    /// <summary>Current cross-line selection of a LineSelection-enabled list as
    /// plain text, or null/empty when nothing is selected. Used by the window
    /// menu's Copy item.</summary>
    public static string? GetSelectedText(ItemsControl ic) =>
        ic.GetValue(BehaviorProperty)?.GetSelectedText();

    /// <summary>
    /// Re-index a cross-line selection after <paramref name="removed"/> lines
    /// were trimmed from the TOP of the buffer (the scrollback cap). Selection
    /// endpoints are (line, offset) pairs, so without this shift a selection
    /// made at line x resolves to line x+n once n lines have trimmed — the
    /// wrong-text copy of public #298. Offsets survive untouched (the lines
    /// themselves don't change), an endpoint that fell off the top clamps to
    /// the buffer start, and a selection trimmed away entirely returns null.
    /// </summary>
    public static ((int Line, int Off) Anchor, (int Line, int Off) Focus)?
        ShiftAfterTrim((int Line, int Off) anchor, (int Line, int Off) focus, int removed)
    {
        if (Math.Max(anchor.Line, focus.Line) < removed) return null;
        return (Shift(anchor, removed), Shift(focus, removed));

        static (int Line, int Off) Shift((int Line, int Off) p, int n)
            => p.Line < n ? (0, 0) : (p.Line - n, p.Off);
    }
}

/// <summary>The per-control state + handlers backing <see cref="LineSelection"/>.
/// Kept alive by the event subscriptions it registers on its control.</summary>
internal sealed class LineSelectionBehavior
{
    private const double DragThreshold  = 3.0;
    private const double EdgeBand       = 18.0;
    private const double AutoScrollStep = 24.0;

    private readonly ItemsControl _ic;

    private (int Line, int Off) _anchor;
    private (int Line, int Off) _focus;
    private bool  _selecting;
    private bool  _dragged;
    private Point _pressPoint;
    private Point _lastDragPoint;

    private SelectableTextBlock?[] _lineCache = Array.Empty<SelectableTextBlock?>();
    private ScrollViewer?          _scroll;
    private DispatcherTimer?       _autoScroll;
    private double                 _autoDir;

    private INotifyCollectionChanged? _buffer;
    private bool _attached;
    private bool _renderPending;

    public LineSelectionBehavior(ItemsControl ic)
    {
        _ic = ic;
        _ic.Focusable = true;

        // Press tunnels so we pre-empt the child SelectableTextBlock's own
        // selection/capture; move/release/key arrive via capture/focus so
        // Bubble-only avoids a Tunnel+Bubble double-fire.
        _ic.AddHandler(InputElement.PointerPressedEvent,  OnPressTunnel, RoutingStrategies.Tunnel);
        _ic.AddHandler(InputElement.PointerMovedEvent,    OnMoved,       RoutingStrategies.Bubble);
        _ic.AddHandler(InputElement.PointerReleasedEvent, OnReleased,    RoutingStrategies.Bubble);
        _ic.AddHandler(InputElement.KeyDownEvent,         OnKeyDown,     RoutingStrategies.Bubble);

        // #298: watch the line buffer so the (line, offset) selection can be
        // re-indexed when the scrollback cap trims lines off the top.
        _ic.PropertyChanged += (_, e) =>
        {
            if (e.Property == ItemsControl.ItemsSourceProperty) WatchBuffer();
        };

        _ic.AttachedToVisualTree   += (_, _) =>
        {
            _scroll = _ic.FindAncestorOfType<ScrollViewer>();
            _autoScroll ??= CreateTimer();
            _attached = true;
            WatchBuffer();
        };
        _ic.DetachedFromVisualTree += (_, _) =>
        {
            _autoScroll?.Stop();
            _attached = false;
            WatchBuffer();   // drops the buffer subscription while off-tree
        };
    }

    /// <summary>(Re)subscribe to the current ItemsSource. Subscribed only while
    /// in the visual tree so a discarded list doesn't stay pinned to the
    /// long-lived line buffer through this handler.</summary>
    private void WatchBuffer()
    {
        if (_buffer is not null)
        {
            _buffer.CollectionChanged -= OnBufferChanged;
            _buffer = null;
        }
        if (!_attached || _ic.ItemsSource is not INotifyCollectionChanged incc) return;
        _buffer = incc;
        _buffer.CollectionChanged += OnBufferChanged;
    }

    private void OnBufferChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex == 0:
                ReindexAfterTrim(e.OldItems?.Count ?? 0);
                break;
            case NotifyCollectionChangedAction.Reset:   // #clear — buffer gone
                _anchor = (0, 0);
                _focus  = (0, 0);
                break;
        }
    }

    private void ReindexAfterTrim(int removed)
    {
        if (removed <= 0 || _anchor == _focus) return;   // no live selection
        if (LineSelection.ShiftAfterTrim(_anchor, _focus, removed) is { } s)
        {
            (_anchor, _focus) = s;
        }
        else
        {
            _anchor = (0, 0);   // the whole selection was trimmed away
            _focus  = (0, 0);
        }
        ScheduleRender();
    }

    /// <summary>Repaint the per-line highlight after a trim, batched per UI
    /// frame (combat trims one line per incoming line — rendering each would
    /// be an O(lines) walk per line).</summary>
    private void ScheduleRender()
    {
        if (_renderPending) return;
        _renderPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _renderPending = false;
            RebuildLineCache();
            RenderSelection();
        }, DispatcherPriority.Loaded);
    }

    private DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        t.Tick += OnAutoScrollTick;
        return t;
    }

    // ── Pointer ──────────────────────────────────────────────────────────────

    private void OnPressTunnel(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_ic);
        if (!pt.Properties.IsLeftButtonPressed) return;   // right/middle pass through

        // No Focus() here — a click (incl. link clicks) must leave keyboard focus
        // on the command bar. Focus is taken only after a real drag (OnReleased).
        _pressPoint    = pt.Position;
        _lastDragPoint = pt.Position;
        RebuildLineCache();

        var hit = HitTest(pt.Position);
        if (hit is null) return;

        ClearSelection();
        _anchor    = hit.Value;
        _focus     = hit.Value;
        _selecting = true;
        _dragged   = false;

        e.Pointer.Capture(_ic);
        e.Handled = true;   // suppress the child's own selection/capture
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_selecting) return;
        var p = e.GetPosition(_ic);
        _lastDragPoint = p;

        if (!_dragged && Distance(p, _pressPoint) > DragThreshold) _dragged = true;
        if (!_dragged) return;

        UpdateAutoScrollZone(p);
        var hit = HitTest(ClampToViewport(p));
        if (hit is not null) { _focus = hit.Value; RenderSelection(); }
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        _autoDir   = 0;
        _autoScroll?.Stop();
        e.Pointer.Capture(null);

        if (!_dragged)
        {
            ClearSelection();
            var link = LinkAt(_pressPoint);
            if (link is not null)
            {
                if (link.IsUrl) DefaultHighlights.OnUrlClicked?.Invoke(link.Command);
                else            DefaultHighlights.OnLinkClicked?.Invoke(link.Command, link.Display);
            }
        }
        else
        {
            _ic.Focus();   // a real selection — take focus so Ctrl+C/Ctrl+A act on it
        }
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && (e.Key == Key.C || e.Key == Key.Insert)) { CopySelection(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A)                     { SelectAll();     e.Handled = true; }
    }

    // ── Autoscroll while dragging at the edges ───────────────────────────────

    private void UpdateAutoScrollZone(Point p)
    {
        if (_scroll is null) { _autoDir = 0; return; }
        var h = _ic.Bounds.Height;
        _autoDir = p.Y < EdgeBand ? -1 : p.Y > h - EdgeBand ? +1 : 0;
        if (_autoDir != 0) _autoScroll?.Start(); else _autoScroll?.Stop();
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (_scroll is null || _autoDir == 0) { _autoScroll?.Stop(); return; }
        var o = _scroll.Offset;
        _scroll.Offset = new Vector(o.X, Math.Max(0, o.Y + _autoDir * AutoScrollStep));
        var hit = HitTest(ClampToViewport(_lastDragPoint));
        if (hit is not null) { _focus = hit.Value; RenderSelection(); }
    }

    // ── Hit testing ──────────────────────────────────────────────────────────

    private (int Line, int Off)? HitTest(Point p)
    {
        SelectableTextBlock? line =
            (_ic.InputHitTest(p) as Visual)?
                .GetSelfAndVisualAncestors().OfType<SelectableTextBlock>().FirstOrDefault()
            ?? NearestLineByY(p.Y);
        if (line is null) return null;

        int idx = IndexOfBlock(line);
        if (idx < 0) return null;

        var local = _ic.TranslatePoint(p, line) ?? default;
        return (idx, OffsetAt(line, local, idx));
    }

    private int OffsetAt(SelectableTextBlock line, Point local, int idx)
    {
        var layout = line.TextLayout;
        int max = LayoutLen(ItemAt(idx));
        if (layout is null) return Math.Clamp((int)Math.Round(local.X / 8), 0, max);
        var r = layout.HitTestPoint(local);
        return Math.Clamp(r.TextPosition + (r.IsTrailing ? 1 : 0), 0, max);
    }

    private SelectableTextBlock? NearestLineByY(double y)
    {
        SelectableTextBlock? best = null;
        double bestDist = double.MaxValue;
        foreach (var b in _lineCache)
        {
            if (b is null) continue;
            var top = (b.TranslatePoint(default, _ic) ?? default).Y;
            var bot = top + b.Bounds.Height;
            double dist = y < top ? top - y : y > bot ? y - bot : 0;
            if (dist < bestDist) { bestDist = dist; best = b; }
            if (dist == 0) break;
        }
        return best;
    }

    // ── Selection model ──────────────────────────────────────────────────────

    private void RenderSelection()
    {
        var (s, e) = Normalized();
        for (int i = 0; i < _lineCache.Length; i++)
        {
            var b = _lineCache[i];
            if (b is null) continue;
            if (i < s.Line || i > e.Line) { b.SelectionStart = 0; b.SelectionEnd = 0; continue; }
            int len = LayoutLen(ItemAt(i));
            b.SelectionStart = i == s.Line ? s.Off : 0;
            b.SelectionEnd   = i == e.Line ? e.Off : len;
        }
    }

    private void ClearSelection()
    {
        foreach (var b in _lineCache)
            if (b is not null) { b.SelectionStart = 0; b.SelectionEnd = 0; }
    }

    private void SelectAll()
    {
        RebuildLineCache();
        int last = _ic.Items.Count - 1;
        if (last < 0) return;
        _anchor = (0, 0);
        _focus  = (last, LayoutLen(ItemAt(last)));
        RenderSelection();
        _ic.Focus();
    }

    private async void CopySelection()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;
        if (TopLevel.GetTopLevel(_ic)?.Clipboard is { } cb) await cb.SetTextAsync(text);
    }

    /// <summary>The current cross-line selection as plain text (links rendered as
    /// their display text), or "" when nothing is selected. Shared by Ctrl+C and
    /// the window menu's "Copy" item.</summary>
    public string GetSelectedText()
    {
        if (_anchor == _focus) return "";
        var (s, e) = Normalized();
        var sb = new StringBuilder();
        for (int L = s.Line; L <= e.Line; L++)
        {
            if (ItemAt(L) is not { } line) continue;
            int lo = L == s.Line ? s.Off : 0;
            int hi = L == e.Line ? e.Off : LayoutLen(line);
            sb.Append(SelectedPlain(line, lo, hi));
            if (L < e.Line) sb.Append('\n');
        }
        return sb.ToString();
    }

    // ── Layout ↔ plain-text mapping (links are one object-char in layout) ─────

    private static int LayoutLen(TextLine? line)
    {
        if (line is null) return 0;
        int len = line.Text.Length;
        if (line.Links is { Count: > 0 })
            foreach (var l in line.Links) len -= Math.Max(0, l.Length - 1);
        return Math.Max(0, len);
    }

    private static string SelectedPlain(TextLine line, int loStart, int loEnd)
    {
        if (loStart >= loEnd) return "";
        var text = line.Text;
        if (line.Links is not { Count: > 0 } links)
            return text[Math.Clamp(loStart, 0, text.Length)..Math.Clamp(loEnd, 0, text.Length)];

        var ordered = links.OrderBy(l => l.Start).ToList();
        var sb = new StringBuilder();
        int plain = 0, layout = 0, li = 0;
        while (plain < text.Length)
        {
            if (li < ordered.Count && plain == ordered[li].Start)
            {
                var lk = ordered[li];
                if (layout >= loStart && layout < loEnd) sb.Append(text, lk.Start, lk.Length);
                layout++; plain += lk.Length; li++;
            }
            else
            {
                if (layout >= loStart && layout < loEnd) sb.Append(text[plain]);
                layout++; plain++;
            }
        }
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private LinkPayload? LinkAt(Point p)
    {
        var v = _ic.InputHitTest(p) as Visual;
        while (v is not null)
        {
            if (v is TextBlock tb && tb.Tag is LinkPayload lp) return lp;
            v = v.GetVisualParent();
        }
        return null;
    }

    private void RebuildLineCache()
    {
        int n = _ic.Items.Count;
        var arr = new SelectableTextBlock?[n];
        foreach (var c in _ic.GetRealizedContainers())
        {
            int idx = _ic.IndexFromContainer(c);
            if (idx < 0 || idx >= n) continue;
            arr[idx] = c as SelectableTextBlock
                       ?? c.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault();
        }
        _lineCache = arr;
    }

    private int IndexOfBlock(SelectableTextBlock block)
    {
        for (int i = 0; i < _lineCache.Length; i++)
            if (ReferenceEquals(_lineCache[i], block)) return i;
        return block.DataContext is TextLine tl ? IndexFromItem(tl) : -1;
    }

    private int IndexFromItem(object item)
    {
        for (int i = 0; i < _ic.Items.Count; i++)
            if (ReferenceEquals(_ic.Items[i], item)) return i;
        return -1;
    }

    private TextLine? ItemAt(int i) => i >= 0 && i < _ic.Items.Count ? _ic.Items[i] as TextLine : null;

    private ((int Line, int Off) S, (int Line, int Off) E) Normalized()
        => Compare(_anchor, _focus) <= 0 ? (_anchor, _focus) : (_focus, _anchor);

    private static int Compare((int Line, int Off) a, (int Line, int Off) b)
        => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Off.CompareTo(b.Off);

    private Point ClampToViewport(Point p)
        => new(Math.Clamp(p.X, 0, Math.Max(0, _ic.Bounds.Width  - 1)),
               Math.Clamp(p.Y, 1, Math.Max(1, _ic.Bounds.Height - 1)));

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X; var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
