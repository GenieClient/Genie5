using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Genie.Core.Health;

namespace Genie.App.Controls;

/// <summary>
/// Body-part sprites for the Healing panel's figures, baked from the same
/// <c>Assets/Injuries</c> art the Injuries panel draws (via
/// <see cref="InjurySprites.GetBase"/>), so a player sees the body they
/// already know.
///
/// <para><b>Two looks.</b> The four-figure layout paints one wound axis per
/// figure: the whole part in that wound's colour. The overlay layout paints all
/// four axes on one figure: <i>outside</i> wounds on the part's edge band,
/// <i>inside</i> wounds on its centre, fresh wounds solid, scars as diagonal
/// stripes — drawn in the order outside fresh, outside scar, inside fresh,
/// inside scar.</para>
///
/// <para>Colour is never the only carrier: the per-part card and the
/// wounds-in-words list repeat every reading as text.</para>
/// </summary>
internal static class HealingSprites
{
    /// <summary>Opacity of untouched art, as in the Injuries panel.</summary>
    public const double HealthyOpacity = 0.30;

    // Fresh: the Injuries panel's yellow → orange → red, carried on to a dark
    // red so all thirteen rungs stay apart. Scars: steel blue, light → deep.
    private static readonly Color[] FreshStops =
        { Color.Parse("#d8bb2c"), Color.Parse("#e07a1c"), Color.Parse("#d83030"), Color.Parse("#8a1414") };
    private static readonly Color[] ScarStops =
        { Color.Parse("#8fb6dc"), Color.Parse("#4f86c4"), Color.Parse("#24548c") };

    /// <summary>The colour of a wound on <paramref name="axis"/> at <paramref name="rung"/> (1–13).</summary>
    public static Color ColourFor(InjuryAxis axis, int rung)
        => Ramp(IsScar(axis) ? ScarStops : FreshStops, Math.Clamp(rung, 1, 13));

    public static bool IsScar(InjuryAxis axis) => axis is InjuryAxis.ScarExternal or InjuryAxis.ScarInternal;
    public static bool IsInside(InjuryAxis axis) => axis is InjuryAxis.FreshInternal or InjuryAxis.ScarInternal;

    private static Color Ramp(Color[] stops, int rung)
    {
        double t = (rung - 1) / 12.0 * (stops.Length - 1);
        int i = Math.Min((int)t, stops.Length - 2);
        double f = t - i;
        Color a = stops[i], b = stops[i + 1];
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));
    }

    // ── Pixel source ─────────────────────────────────────────────────────────

    /// <summary>A sprite's pixels (BGRA, unpremultiplied), cropped for the figure.</summary>
    internal sealed record Pixels(int Width, int Height, byte[] Bgra, byte[] Core, Bitmap Source);

    private static readonly ConcurrentDictionary<(string, double), Pixels?> PixelCache = new();
    private static readonly ConcurrentDictionary<string, IImage?> BakeCache = new(StringComparer.Ordinal);

    private static Pixels? Load(string regionId, double crop)
        => PixelCache.GetOrAdd((regionId, crop), key =>
        {
            if (InjurySprites.GetBase(key.Item1) is not { } bmp) return null;
            var w = bmp.PixelSize.Width;
            var h = crop >= 1.0 ? bmp.PixelSize.Height : (int)(bmp.PixelSize.Height * crop);
            // Same read InjurySprites.Tint does: the decoded PNG's BGRA rows.
            var full = new byte[w * bmp.PixelSize.Height * 4];
            var handle = GCHandle.Alloc(full, GCHandleType.Pinned);
            try
            {
                bmp.CopyPixels(new PixelRect(0, 0, w, bmp.PixelSize.Height),
                               handle.AddrOfPinnedObject(), full.Length, w * 4);
            }
            catch (NotSupportedException)
            {
                return null;   // the headless renderer cannot read pixels back
            }
            finally { handle.Free(); }

            var bgra = new byte[w * h * 4];
            Array.Copy(full, bgra, bgra.Length);
            var alpha = new byte[w * h];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = bgra[i * 4 + 3];
            return new Pixels(w, h, bgra, HealingMasks.Core(alpha, w, h), bmp);
        });

    /// <summary>True when the pixel at (<paramref name="u"/>, <paramref name="v"/>)
    /// — fractions of the sprite's width and height — is solid art. Without the
    /// asset (headless tests) the whole box counts.</summary>
    public static bool IsSolidAt(string regionId, double crop, double u, double v)
    {
        if (u < 0 || v < 0 || u >= 1 || v >= 1) return false;
        if (Load(regionId, crop) is not { } p) return true;
        int x = (int)(u * p.Width), y = (int)(v * p.Height);
        return p.Bgra[(y * p.Width + x) * 4 + 3] > 60;
    }

    /// <summary>Pixel size of the cropped sprite, or null without the asset.</summary>
    public static PixelSize? SizeOf(string regionId, double crop)
        => Load(regionId, crop) is { } p ? new PixelSize(p.Width, p.Height) : null;

    /// <summary>The untouched art, cropped for the figure.</summary>
    public static IImage? Plain(string regionId, double crop)
    {
        if (InjurySprites.GetBase(regionId) is not { } bmp) return null;
        return crop >= 1.0 ? bmp
            : BakeCache.GetOrAdd($"plain|{regionId}|{crop}",
                _ => new CroppedBitmap(bmp, new PixelRect(0, 0, bmp.PixelSize.Width,
                                                              (int)(bmp.PixelSize.Height * crop))));
    }

    // ── Baking ───────────────────────────────────────────────────────────────

    /// <summary>One part painted with the given wounds (axis → rung). In the
    /// four-figure look only one axis is passed and the whole part takes its
    /// colour; in the overlay look the edge/centre/stripe coding applies. Null
    /// without the asset.</summary>
    public static IImage? Bake(string regionId, double crop, IReadOnlyDictionary<InjuryAxis, int> wounds, bool overlay)
    {
        var key = $"{(overlay ? "o" : "p")}|{regionId}|{crop}|" +
                  string.Join(",", Order(wounds));
        return BakeCache.GetOrAdd(key, _ => Load(regionId, crop) is { } p ? Compose(p, wounds, overlay) : null);
    }

    private static IEnumerable<string> Order(IReadOnlyDictionary<InjuryAxis, int> wounds)
    {
        foreach (var axis in DrawOrder)
            if (wounds.TryGetValue(axis, out var r) && r > 0) yield return $"{axis}:{r}";
    }

    /// <summary>Outside fresh, outside scar, inside fresh, inside scar.</summary>
    private static readonly InjuryAxis[] DrawOrder =
        { InjuryAxis.FreshExternal, InjuryAxis.ScarExternal, InjuryAxis.FreshInternal, InjuryAxis.ScarInternal };

    private static WriteableBitmap Compose(Pixels p, IReadOnlyDictionary<InjuryAxis, int> wounds, bool overlay)
    {
        var outPx = new byte[p.Bgra.Length];
        bool anyInside  = false, anyOutside = false;
        foreach (var axis in DrawOrder)
            if (wounds.TryGetValue(axis, out var r) && r > 0)
            { if (IsInside(axis)) anyInside = true; else anyOutside = true; }

        for (int y = 0; y < p.Height; y++)
        for (int x = 0; x < p.Width; x++)
        {
            int i = y * p.Width + x, b = i * 4;
            byte a = p.Bgra[b + 3];
            if (a == 0) continue;

            // Base: the art, faded like a healthy part.
            double cr = p.Bgra[b + 2], cg = p.Bgra[b + 1], cb = p.Bgra[b], ca = a / 255.0 * HealthyOpacity;
            // Shade for a flat wound colour: the art's luminance, compressed so
            // the engraving shows without the ivory washing the colour out.
            double shade = (150 + Luma(p.Bgra[b + 2], p.Bgra[b + 1], p.Bgra[b]) * 105 / 255.0) / 255.0;
            double pa = a / 255.0;
            bool inCore = p.Core[i] != 0;

            foreach (var axis in DrawOrder)
            {
                if (!wounds.TryGetValue(axis, out var rung) || rung <= 0) continue;
                if (overlay && IsInside(axis) != inCore) continue;

                var c = ColourFor(axis, rung);
                double lr = c.R * shade, lg = c.G * shade, lb = c.B * shade;
                if (overlay && IsScar(axis))
                {
                    var freshTwin = IsInside(axis) ? InjuryAxis.FreshInternal : InjuryAxis.FreshExternal;
                    bool freshHere = wounds.TryGetValue(freshTwin, out var fr) && fr > 0;
                    if (!freshHere) Over(ref cr, ref cg, ref cb, ref ca, lr, lg, lb, pa * 0.35);
                    if (HealingMasks.Stripe(x, y)) Over(ref cr, ref cg, ref cb, ref ca, lr, lg, lb, pa);
                }
                else
                {
                    Over(ref cr, ref cg, ref cb, ref ca, lr, lg, lb, pa);
                }
            }

            // A dark seam where the centre meets the edge band, when both carry wounds.
            if (overlay && anyInside && anyOutside && inCore && HealingMasks.IsCoreEdge(p.Core, p.Width, p.Height, x, y))
                Over(ref cr, ref cg, ref cb, ref ca, 20, 20, 22, pa * 0.85);

            outPx[b]     = (byte)Math.Clamp(cb, 0, 255);
            outPx[b + 1] = (byte)Math.Clamp(cg, 0, 255);
            outPx[b + 2] = (byte)Math.Clamp(cr, 0, 255);
            outPx[b + 3] = (byte)Math.Clamp(ca * 255, 0, 255);
        }

        var wb = new WriteableBitmap(new PixelSize(p.Width, p.Height), p.Source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        for (int y = 0; y < p.Height; y++)
            Marshal.Copy(outPx, y * p.Width * 4, fb.Address + y * fb.RowBytes, p.Width * 4);
        return wb;
    }

    private static double Luma(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b;

    /// <summary>Source-over in unpremultiplied colour.</summary>
    private static void Over(ref double r, ref double g, ref double b, ref double a,
                             double sr, double sg, double sb, double sa)
    {
        double outA = sa + a * (1 - sa);
        if (outA <= 0) return;
        r = (sr * sa + r * a * (1 - sa)) / outA;
        g = (sg * sa + g * a * (1 - sa)) / outA;
        b = (sb * sa + b * a * (1 - sa)) / outA;
        a = outA;
    }
}

/// <summary>
/// The pure geometry behind the overlay look — split out so it is testable
/// without an asset pipeline.
/// </summary>
internal static class HealingMasks
{
    /// <summary>Alpha above this counts as the part's solid art.</summary>
    public const byte SolidAlpha = 110;

    /// <summary>
    /// The part's centre: solid pixels whose distance to the edge exceeds 22%
    /// of the part's mean thickness (filled area ÷ long side — a diagonal arm's
    /// bounding box is far wider than the limb, so the box would over-erode).
    /// What is left is roughly the middle 55% across the limb. Returned as
    /// 1/0 per pixel.
    /// </summary>
    public static byte[] Core(byte[] alpha, int w, int h)
    {
        var core = new byte[w * h];
        int minX = w, minY = h, maxX = -1, maxY = -1, area = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (alpha[y * w + x] < SolidAlpha) continue;
            area++;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        if (area == 0) return core;
        double thickness = area / (double)Math.Max(maxX - minX + 1, maxY - minY + 1);
        double depth = Math.Max(2, thickness * 0.22);

        // Two-pass chamfer distance (3-4 weights) to the nearest non-solid pixel.
        const int inf = int.MaxValue / 4;
        var d = new int[w * h];
        for (int i = 0; i < d.Length; i++) d[i] = alpha[i] >= SolidAlpha ? inf : 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x; if (d[i] == 0) continue;
            int v = d[i];
            v = Math.Min(v, x > 0 ? d[i - 1] + 3 : 3);
            v = Math.Min(v, y > 0 ? d[i - w] + 3 : 3);
            v = Math.Min(v, x > 0 && y > 0 ? d[i - w - 1] + 4 : 4);
            v = Math.Min(v, x < w - 1 && y > 0 ? d[i - w + 1] + 4 : 4);
            d[i] = v;
        }
        for (int y = h - 1; y >= 0; y--)
        for (int x = w - 1; x >= 0; x--)
        {
            int i = y * w + x; if (d[i] == 0) continue;
            int v = d[i];
            v = Math.Min(v, x < w - 1 ? d[i + 1] + 3 : 3);
            v = Math.Min(v, y < h - 1 ? d[i + w] + 3 : 3);
            v = Math.Min(v, x < w - 1 && y < h - 1 ? d[i + w + 1] + 4 : 4);
            v = Math.Min(v, x > 0 && y < h - 1 ? d[i + w - 1] + 4 : 4);
            d[i] = v;
        }
        for (int i = 0; i < d.Length; i++)
            if (d[i] / 3.0 > depth) core[i] = 1;
        return core;
    }

    /// <summary>Diagonal stripe pattern for scars: 4 on, 4 off.</summary>
    public static bool Stripe(int x, int y) => ((x + y) & 7) < 4;

    /// <summary>A centre pixel with an edge-band neighbour.</summary>
    public static bool IsCoreEdge(byte[] core, int w, int h, int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int nx = x + dx, ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
            if (core[ny * w + nx] == 0) return true;
        }
        return false;
    }
}
