using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Genie.App.Controls;

/// <summary>
/// Lightweight custom-drawn chart for the Analytics panel — the same
/// render-override idiom as <see cref="MapCanvas"/>. Draws line series over a
/// numeric or time X axis and bar series over categories, with auto-scaled
/// "nice" axis ticks, an empty-state message, and a hover crosshair + value
/// badge.
///
/// Colours come from brush StyledProperties so the panel's DataTemplate can
/// bind DynamicResource theme roles — charts follow Dark/Light/custom themes
/// with no adapter layer. The series model (<see cref="ChartSeries"/>) is
/// renderer-neutral, so this control is swappable for a charting library
/// without touching the view-model.
///
/// Re-renders when <see cref="Series"/> changes reference or
/// <see cref="RenderTick"/> bumps (the MapCanvas idiom for in-place list
/// mutation).
/// </summary>
public class ChartCanvas : Control
{
    private const double MarginLeft   = 52;
    private const double MarginRight  = 10;
    private const double MarginTop    = 10;
    private const double MarginBottom = 24;

    // ── Styled properties ─────────────────────────────────────────────────

    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<ChartCanvas, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<int> RenderTickProperty =
        AvaloniaProperty.Register<ChartCanvas, int>(nameof(RenderTick));

    /// <summary>Format X tick labels as elapsed time (h:mm) instead of plain
    /// numbers. Line-series X values are then seconds.</summary>
    public static readonly StyledProperty<bool> XIsTimeProperty =
        AvaloniaProperty.Register<ChartCanvas, bool>(nameof(XIsTime));

    /// <summary>Format X tick labels as calendar dates; line-series X values
    /// are then days since 0001-01-01 (DateTime.Ticks / TicksPerDay).</summary>
    public static readonly StyledProperty<bool> XIsDateProperty =
        AvaloniaProperty.Register<ChartCanvas, bool>(nameof(XIsDate));

    public static readonly StyledProperty<string?> EmptyMessageProperty =
        AvaloniaProperty.Register<ChartCanvas, string?>(nameof(EmptyMessage));

    public static readonly StyledProperty<IBrush?> AxisBrushProperty =
        AvaloniaProperty.Register<ChartCanvas, IBrush?>(nameof(AxisBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<ChartCanvas, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<ChartCanvas, IBrush?>(nameof(LabelBrush));

    public IReadOnlyList<ChartSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }
    public int RenderTick
    {
        get => GetValue(RenderTickProperty);
        set => SetValue(RenderTickProperty, value);
    }
    public bool XIsTime
    {
        get => GetValue(XIsTimeProperty);
        set => SetValue(XIsTimeProperty, value);
    }
    public bool XIsDate
    {
        get => GetValue(XIsDateProperty);
        set => SetValue(XIsDateProperty, value);
    }
    public string? EmptyMessage
    {
        get => GetValue(EmptyMessageProperty);
        set => SetValue(EmptyMessageProperty, value);
    }
    public IBrush? AxisBrush
    {
        get => GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }
    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }
    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    // Series palette — readable on both dark and light surfaces.
    private static readonly IBrush[] Palette =
    {
        new SolidColorBrush(Color.FromRgb(0x4f, 0xa3, 0xe3)),   // blue
        new SolidColorBrush(Color.FromRgb(0x6f, 0xbf, 0x73)),   // green
        new SolidColorBrush(Color.FromRgb(0xe3, 0xa0, 0x4f)),   // amber
        new SolidColorBrush(Color.FromRgb(0xc9, 0x6f, 0xd6)),   // violet
        new SolidColorBrush(Color.FromRgb(0xe0, 0x6c, 0x75)),   // red
        new SolidColorBrush(Color.FromRgb(0x56, 0xc2, 0xb6)),   // teal
    };
    private static IBrush PaletteBrush(int index) => Palette[Math.Abs(index) % Palette.Length];

    private Point? _hover;

    static ChartCanvas()
    {
        AffectsRender<ChartCanvas>(SeriesProperty, RenderTickProperty, XIsTimeProperty,
                                   XIsDateProperty, EmptyMessageProperty,
                                   AxisBrushProperty, GridBrushProperty, LabelBrushProperty);
    }

    public ChartCanvas()
    {
        ClipToBounds = true;
        PointerMoved += (_, e) => { _hover = e.GetPosition(this); InvalidateVisual(); };
        PointerExited += (_, _) => { _hover = null; InvalidateVisual(); };
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = Bounds;
        var axis  = AxisBrush  ?? Brushes.Gray;
        var grid  = GridBrush  ?? new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        var label = LabelBrush ?? Brushes.Gray;

        var series = Series?.Where(s => s.Points.Count > 0).ToList();
        if (series is null || series.Count == 0)
        {
            DrawCentered(ctx, EmptyMessage ?? "(no data yet)", label, bounds);
            return;
        }

        var plot = new Rect(MarginLeft, MarginTop,
                            Math.Max(10, bounds.Width - MarginLeft - MarginRight),
                            Math.Max(10, bounds.Height - MarginTop - MarginBottom));

        bool hasBars = series.Any(s => s.Kind == ChartSeriesKind.Bar);
        if (hasBars) RenderBars(ctx, series.Where(s => s.Kind == ChartSeriesKind.Bar).ToList(), plot, axis, grid, label);
        else         RenderLines(ctx, series, plot, axis, grid, label);
    }

    // ── Line charts ──────────────────────────────────────────────────────

    private void RenderLines(DrawingContext ctx, List<ChartSeries> series, Rect plot,
                             IBrush axis, IBrush grid, IBrush label)
    {
        double minX = series.Min(s => s.Points.Min(p => p.X));
        double maxX = series.Max(s => s.Points.Max(p => p.X));
        double minY = series.Min(s => s.Points.Min(p => p.Y));
        double maxY = series.Max(s => s.Points.Max(p => p.Y));
        if (maxX <= minX) maxX = minX + 1;
        if (maxY <= minY) { minY -= 0.5; maxY += 0.5; }
        // A little headroom so lines don't kiss the frame.
        double padY = (maxY - minY) * 0.05;
        minY -= padY; maxY += padY;

        double X(double x) => plot.X + (x - minX) / (maxX - minX) * plot.Width;
        double Y(double y) => plot.Bottom - (y - minY) / (maxY - minY) * plot.Height;

        // Y grid + labels at nice steps, labelled to the step's precision.
        double yStep = NiceStep(minY, maxY, 5);
        foreach (var tick in NiceTicks(minY, maxY, 5))
        {
            double y = Y(tick);
            ctx.DrawLine(new Pen(grid, 1), new Point(plot.X, y), new Point(plot.Right, y));
            DrawText(ctx, FormatTick(tick, yStep), label, new Point(2, y - 7), 11);
        }
        // X grid + labels.
        foreach (var tick in NiceTicks(minX, maxX, 5))
        {
            double x = X(tick);
            ctx.DrawLine(new Pen(grid, 1), new Point(x, plot.Y), new Point(x, plot.Bottom));
            DrawText(ctx, FormatX(tick), label, new Point(x - 18, plot.Bottom + 4), 11);
        }
        // Axes frame.
        ctx.DrawLine(new Pen(axis, 1), new Point(plot.X, plot.Y), new Point(plot.X, plot.Bottom));
        ctx.DrawLine(new Pen(axis, 1), new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));

        foreach (var s in series)
        {
            var pen = new Pen(PaletteBrush(s.ColorIndex), 1.6);
            Point? prev = null;
            foreach (var p in s.Points.OrderBy(p => p.X))
            {
                var pt = new Point(X(p.X), Y(p.Y));
                if (prev is { } pv) ctx.DrawLine(pen, pv, pt);
                prev = pt;
            }
        }

        // Legend (top-left inside the plot) when there's more than one series.
        if (series.Count > 1)
        {
            double ly = plot.Y + 2;
            foreach (var s in series.Take(6))
            {
                ctx.DrawRectangle(PaletteBrush(s.ColorIndex), null, new Rect(plot.X + 6, ly + 3, 10, 3));
                DrawText(ctx, s.Name, label, new Point(plot.X + 20, ly - 2), 11);
                ly += 15;
            }
        }

        // Hover: crosshair + nearest point badge.
        if (_hover is { } h && plot.Contains(h))
        {
            ctx.DrawLine(new Pen(grid, 1), new Point(h.X, plot.Y), new Point(h.X, plot.Bottom));
            ChartPoint? best = null; string bestName = ""; double bestDist = double.MaxValue;
            foreach (var s in series)
            foreach (var p in s.Points)
            {
                double d = Math.Abs(X(p.X) - h.X);
                if (d < bestDist) { bestDist = d; best = p; bestName = s.Name; }
            }
            if (best is { } bp && bestDist < 40)
            {
                string text = $"{bestName}: {FormatTick(bp.Y, yStep)}  @ {FormatX(bp.X)}";
                DrawBadge(ctx, text, new Point(Math.Min(h.X + 8, plot.Right - 150), plot.Y + 6));
                var marker = new Point(X(bp.X), Y(bp.Y));
                ctx.DrawEllipse(PaletteBrush(0), null, marker, 3, 3);
            }
        }
    }

    // ── Bar charts ───────────────────────────────────────────────────────

    private void RenderBars(DrawingContext ctx, List<ChartSeries> series, Rect plot,
                            IBrush axis, IBrush grid, IBrush label)
    {
        // Bars are horizontal: one row per point — skill names read better
        // sideways than rotated under a vertical bar.
        var points = series.SelectMany(s => s.Points.Select(p => (Series: s, Point: p))).ToList();
        double maxY = Math.Max(points.Max(p => p.Point.Y), 0.0001);

        double rowH = Math.Min(24, plot.Height / Math.Max(1, points.Count));
        const double labelW = 110;
        double barLeft = plot.X + labelW;
        double barMaxW = Math.Max(10, plot.Right - barLeft - 46);

        // Vertical grid at nice steps.
        foreach (var tick in NiceTicks(0, maxY, 4))
        {
            double x = barLeft + tick / maxY * barMaxW;
            ctx.DrawLine(new Pen(grid, 1), new Point(x, plot.Y), new Point(x, plot.Y + rowH * points.Count));
            DrawText(ctx, FormatNumber(tick), label, new Point(x - 10, plot.Y + rowH * points.Count + 4), 10);
        }

        double y = plot.Y;
        foreach (var (s, p) in points)
        {
            double w = Math.Max(1, p.Y / maxY * barMaxW);
            var rect = new Rect(barLeft, y + 2, w, Math.Max(2, rowH - 4));
            ctx.DrawRectangle(PaletteBrush(s.ColorIndex), null, rect);
            DrawText(ctx, Ellipsize(p.Label ?? "", 16), label, new Point(plot.X, y + rowH / 2 - 7), 11);
            DrawText(ctx, FormatNumber(p.Y), label, new Point(barLeft + w + 4, y + rowH / 2 - 7), 11);
            y += rowH;
        }

        ctx.DrawLine(new Pen(axis, 1), new Point(barLeft, plot.Y), new Point(barLeft, y));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Round-number tick positions covering [min, max] — the classic
    /// 1/2/5×10ⁿ "nice step" so gridlines land on human values.</summary>
    internal static IEnumerable<double> NiceTicks(double min, double max, int target)
    {
        double step = NiceStep(min, max, target);
        if (step <= 0) yield break;
        double tick = Math.Ceiling(min / step) * step;
        for (; tick <= max + step * 0.001; tick += step)
            yield return Math.Round(tick, 10);
    }

    /// <summary>The 1/2/5×10ⁿ spacing <see cref="NiceTicks"/> uses — exposed so
    /// label formatting can match its precision to the spacing.</summary>
    internal static double NiceStep(double min, double max, int target)
    {
        double range = max - min;
        if (range <= 0 || double.IsNaN(range)) return 0;
        double rough = range / Math.Max(1, target);
        double mag   = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double norm  = rough / mag;
        return (norm < 1.5 ? 1 : norm < 3.5 ? 2 : norm < 7.5 ? 5 : 10) * mag;
    }

    /// <summary>Axis-tick label with precision derived from the tick spacing.
    /// A sub-1 step needs decimals: <see cref="FormatNumber"/> renders values
    /// ≥100 as integers, so a sub-rank range printed the same label on every
    /// tick (#166 — "555" six times down the axis).</summary>
    internal static string FormatTick(double v, double step)
    {
        if (step > 0 && step < 1 && Math.Abs(v) < 1000)
        {
            int decimals = Math.Min(3, (int)Math.Ceiling(-Math.Log10(step) - 1e-9));
            return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }
        return FormatNumber(v);
    }

    private string FormatX(double x)
    {
        if (XIsDate)
        {
            long ticks = (long)(x * TimeSpan.TicksPerDay);
            if (ticks > DateTime.MinValue.Ticks && ticks < DateTime.MaxValue.Ticks)
                return new DateTime(ticks, DateTimeKind.Utc).ToString("MM-dd", CultureInfo.InvariantCulture);
        }
        if (XIsTime)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, x));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}h" : $"{t.Minutes}:{t.Seconds:00}";
        }
        return FormatNumber(x);
    }

    private static string FormatNumber(double v) =>
        Math.Abs(v) >= 1000 ? v.ToString("0.#k", CultureInfo.InvariantCulture).Replace("000k", "k")
        : Math.Abs(v) >= 100 ? v.ToString("0", CultureInfo.InvariantCulture)
        : v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Ellipsize(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    // Resolved (and cached) to installed families: these run on every redraw,
    // and a list naming a missing family leaked on Linux (#393).
    private static Typeface LabelTypeface => new(FontChain.Resolve("Segoe UI, Cantarell, sans-serif"));

    private void DrawText(DrawingContext ctx, string text, IBrush brush, Point at, double size)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                   LabelTypeface, size, brush);
        ctx.DrawText(ft, at);
    }

    private void DrawCentered(DrawingContext ctx, string text, IBrush brush, Rect bounds)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                   LabelTypeface, 12, brush);
        ctx.DrawText(ft, new Point((bounds.Width - ft.Width) / 2, (bounds.Height - ft.Height) / 2));
    }

    private void DrawBadge(DrawingContext ctx, string text, Point at)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                   LabelTypeface, 11, Brushes.White);
        var rect = new Rect(at.X, at.Y, ft.Width + 12, ft.Height + 6);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xee, 0x22, 0x22, 0x22)),
                          new Pen(new SolidColorBrush(Color.FromRgb(0x66, 0x88, 0xaa)), 1), rect, 3, 3);
        ctx.DrawText(ft, new Point(at.X + 6, at.Y + 3));
    }
}
