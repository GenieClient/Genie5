using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Genie.Core.Events;

namespace Genie.App.Controls;

/// <summary>
/// Loads the per-region body-part sprites for the Injuries panel
/// (<c>Assets/Injuries/{regionId}.png</c> — sliced from a 16-part sheet, one
/// sprite per DR hit-test region) and bakes colour-variant overlays for each
/// injury state.
///
/// <para>
/// The tint is a per-pixel multiply of the severity colour over the sprite:
/// the art is near-grayscale ivory, so multiplying preserves all the engraved
/// line work while the silhouette reads yellow→orange→red for wound 1–3,
/// steel blue for scar, purple for nerve damage (same palette the old vector
/// doll used). Healthy regions return the untinted sprite. Variants are baked
/// once per (region, state) and cached — 16 regions × at most 6 states.
/// </para>
/// </summary>
internal static class InjurySprites
{
    // Severity palette — single source of truth for the panel's colour coding
    // (the summary list repeats every reading in words, so colour is never the
    // only carrier). Scar and nerve damage keep one colour across severities;
    // the tooltip and list carry the number.
    private static readonly Color Wound1 = Color.Parse("#c8ae2a");
    private static readonly Color Wound2 = Color.Parse("#d2761e");
    private static readonly Color Wound3 = Color.Parse("#d23434");
    private static readonly Color Scar   = Color.Parse("#7e9cb4");
    private static readonly Color Damage = Color.Parse("#a87ed6");

    // Concurrent: the app only touches these from the UI thread, but test hosts
    // construct view models from parallel xUnit collections (two of them build a
    // MainWindowViewModel), and a plain Dictionary corrupts under that.
    private static readonly ConcurrentDictionary<string, Bitmap?> BaseCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Region, InjuryKind Kind, int Severity), Bitmap?> TintCache = new();

    /// <summary>Sprite for a region in a given state; null if the asset can't
    /// load (headless/test contexts) — the view just shows the label.</summary>
    public static Bitmap? Get(string regionId, InjuryKind kind, int severity)
    {
        if (kind == InjuryKind.None) return GetBase(regionId);

        // Wound varies by severity; scar/damage use one tint regardless.
        var key = (regionId, kind, kind == InjuryKind.Wound ? Math.Clamp(severity, 1, 3) : 0);
        if (TintCache.TryGetValue(key, out var cached)) return cached;

        var tinted = GetBase(regionId) is { } src ? Tint(src, TintFor(kind, key.Item3)) : null;
        TintCache[key] = tinted;
        return tinted;
    }

    private static Color TintFor(InjuryKind kind, int severity) => kind switch
    {
        InjuryKind.Wound when severity <= 1 => Wound1,
        InjuryKind.Wound when severity == 2 => Wound2,
        InjuryKind.Wound                    => Wound3,
        InjuryKind.Scar                     => Scar,
        _                                   => Damage,
    };

    private static Bitmap? GetBase(string regionId)
    {
        if (BaseCache.TryGetValue(regionId, out var cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Genie5/Assets/Injuries/{regionId}.png"));
            bmp = new Bitmap(stream);
        }
        catch
        {
            // No Avalonia asset pipeline (unit tests / designer edge cases).
        }
        BaseCache[regionId] = bmp;
        return bmp;
    }

    private static WriteableBitmap Tint(Bitmap src, Color tint)
    {
        var size = src.PixelSize;
        var wb = new WriteableBitmap(size, src.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using var fb = wb.Lock();
        var byteCount = fb.RowBytes * size.Height;
        src.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), fb.Address, byteCount, fb.RowBytes);

        var bytes = new byte[byteCount];
        Marshal.Copy(fb.Address, bytes, 0, byteCount);

        for (int y = 0; y < size.Height; y++)
        {
            int row = y * fb.RowBytes;
            for (int x = 0; x < size.Width; x++)
            {
                int i = row + x * 4;                    // BGRA
                if (bytes[i + 3] == 0) continue;        // transparent bg stays
                bytes[i]     = (byte)(bytes[i]     * tint.B / 255);
                bytes[i + 1] = (byte)(bytes[i + 1] * tint.G / 255);
                bytes[i + 2] = (byte)(bytes[i + 2] * tint.R / 255);
            }
        }

        Marshal.Copy(bytes, 0, fb.Address, byteCount);
        return wb;
    }
}
