using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Genie.Core.Commanding;

namespace Genie.App.Services;

/// <summary>
/// Decoded-bitmap cache for <c>#img</c> lines (public #361). A hotbar or banner
/// script redraws the same few icons over and over, so each file is decoded once,
/// off the UI thread, and every line showing it shares the one
/// <see cref="Bitmap"/>. Keyed on path + size + last-write time, so editing the
/// file on disk picks up the new picture on the next <c>#img</c>.
///
/// <para>Bitmaps are never disposed here: a line still on screen may hold one
/// after it leaves the cache. The cache is bounded by simply forgetting everything
/// past <see cref="MaxEntries"/>; unreferenced bitmaps are then collected with
/// their lines.</para>
/// </summary>
internal static class InlineImageCache
{
    private const int MaxEntries = 64;

    private static readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> Cache =
        new(StringComparer.Ordinal);

    /// <summary>The decoded bitmap for <paramref name="path"/>, or null when it
    /// can't be read or decoded. Never throws.</summary>
    public static Task<Bitmap?> GetAsync(string path)
    {
        string key;
        try
        {
            var info = new FileInfo(path);
            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { return Task.FromResult<Bitmap?>(null); }

        if (Cache.Count >= MaxEntries && !Cache.ContainsKey(key)) Cache.Clear();
        var task = Cache.GetOrAdd(key, _ => new Lazy<Task<Bitmap?>>(() => Task.Run(() => Decode(path)))).Value;
        // A failed decode is not remembered: a file being written can succeed next time.
        _ = task.ContinueWith(t => { if (t.Result is null) Cache.TryRemove(key, out _); },
                              TaskScheduler.Default);
        return task;
    }

    private static Bitmap? Decode(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bmp = new Bitmap(fs);
            // A huge picture is shown at most MaxRenderEdge wide (ImageCommand.FitSize),
            // so don't keep a full-resolution copy in memory for it.
            var edge = Math.Max(bmp.PixelSize.Width, bmp.PixelSize.Height);
            if (edge > ImageCommand.MaxRenderEdge * 2)
            {
                var (w, h) = ImageCommand.FitSize(bmp.PixelSize.Width, bmp.PixelSize.Height, 0, 0,
                                                  ImageCommand.MaxRenderEdge * 2);
                var scaled = bmp.CreateScaledBitmap(new PixelSize(w, h));
                bmp.Dispose();
                bmp = scaled;
            }
            return bmp;
        }
        catch (Exception ex)
        {
            Diagnostics.ErrorLog.Log("InlineImageCache.Decode", ex);
            return null;
        }
    }
}
