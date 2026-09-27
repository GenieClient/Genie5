namespace Genie.Core.Commanding;

/// <summary>
/// A validated Genie 4 <c>#img</c> / <c>#image</c> request, ready for the UI to
/// render. <see cref="Path"/> is the resolved, existing file; <see cref="Window"/>
/// is the <c>&gt;window</c> target (null = Genie 4's default, see
/// <see cref="ImageCommand"/>); <see cref="Width"/> / <see cref="Height"/> are the
/// requested size in pixels, 0 = unspecified.
/// </summary>
public sealed record ImageRequest(string Path, string? Window, int Width, int Height)
{
    /// <summary>The file name shown in the text placeholder (<c>icon.png</c>).</summary>
    public string DisplayName => System.IO.Path.GetFileName(Path);

    /// <summary>The line's plain-text stand-in — what the session log, Copy /
    /// Copy All, Find and the text-only renderers see in place of the picture.</summary>
    public string Placeholder => ImageCommand.Placeholder(DisplayName);
}

/// <summary>
/// Genie 4 <c>#img [&gt;window] &lt;file&gt; [w:N] [h:N]</c> (alias <c>#image</c>) —
/// Core/Command.cs:397. Parsing, path resolution and file validation live here so
/// the command is testable without a UI; decoding and drawing the bitmap is the
/// App's job (Core stays UI-free).
/// <list type="bullet">
///   <item><b>Arguments</b> — in any order: <c>&gt;window</c>, <c>w:N</c> /
///   <c>width:N</c>, <c>h:N</c> / <c>height:N</c>; everything else is the file.
///   Genie 4 kept only the last bare token; here bare tokens are joined with a
///   space so an unquoted name with a space still resolves.</item>
///   <item><b>Path</b> — relative names resolve against the configured art
///   directory (<c>#config artdir</c>, default <c>Art</c> under the data folder),
///   exactly as Genie 4's <c>Path.Combine(ArtDir, file)</c>; an absolute path is
///   used as-is. Genie 4 scripts write <c>icons\foo.png</c>, so a backslash is
///   read as a folder separator on every platform.</item>
///   <item><b>Guards</b> — the file must exist, be png / jpg / gif / bmp by both
///   extension and header bytes, and be at most <see cref="MaxFileBytes"/>.
///   Genie 4 silently drew nothing for a bad path; here each failure echoes a
///   clear one-line reason instead.</item>
/// </list>
/// </summary>
public static class ImageCommand
{
    /// <summary>Largest file <c>#img</c> will load. Icons and portraits are
    /// kilobytes; anything past this is almost certainly the wrong file.</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    /// <summary>Largest rendered edge in pixels (see <see cref="FitSize"/>).</summary>
    public const int MaxRenderEdge = 1024;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp"];

    /// <summary>The plain-text stand-in for an image line: <c>[image: icon.png]</c>.</summary>
    public static string Placeholder(string displayName) => $"[image: {displayName}]";

    /// <summary>
    /// Parse the tokens after the verb (<paramref name="parts"/>[0] is
    /// <c>img</c>/<c>image</c>) and validate the file. Returns the request, or
    /// null with <paramref name="error"/> set to the line to echo. A malformed
    /// size option echoes through <paramref name="warn"/> (Genie 4's "Invalid
    /// Width Specified") and is ignored, so the image still shows at its natural
    /// size.
    /// </summary>
    public static ImageRequest? Parse(IReadOnlyList<string> parts, string artDir,
                                      Action<string> warn, out string? error)
    {
        error = null;
        string? window = null;
        int width = 0, height = 0;
        var file = new List<string>();

        for (var i = 1; i < parts.Count; i++)
        {
            var arg = parts[i];
            if (arg.Length == 0) continue;
            if (arg[0] == '>') { window = arg.TrimStart('>').Trim(); continue; }
            if (TrySize(arg, "w:", "width:", out var w, out var badW))
            {
                if (badW) warn($"Invalid Width Specified: {arg}"); else width = w;
                continue;
            }
            if (TrySize(arg, "h:", "height:", out var h, out var badH))
            {
                if (badH) warn($"Invalid Height Specified: {arg}"); else height = h;
                continue;
            }
            file.Add(arg);
        }

        if (file.Count == 0)
        {
            error = "No File Name was specified for the Image Command.";   // Genie 4's text
            return null;
        }

        var path = Resolve(string.Join(" ", file), artDir);
        if (!Validate(path, out error)) return null;
        return new ImageRequest(path, string.IsNullOrEmpty(window) ? null : window, width, height);
    }

    /// <summary>Resolve a script-supplied image name against the art directory.
    /// Absolute paths pass through; backslashes become the platform separator.</summary>
    public static string Resolve(string file, string artDir)
    {
        var name = file.Trim().Replace('\\', '/').Replace('/', System.IO.Path.DirectorySeparatorChar);
        return System.IO.Path.IsPathRooted(name)
            ? System.IO.Path.GetFullPath(name)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(artDir, name));
    }

    /// <summary>True when <paramref name="path"/> is an existing, size-capped
    /// png / jpg / gif / bmp. Otherwise <paramref name="error"/> says why.</summary>
    public static bool Validate(string path, out string? error)
    {
        error = null;
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(Extensions, ext) < 0)
        {
            error = $"#img: unsupported image type '{(ext.Length == 0 ? "(none)" : ext)}' " +
                    "— use a .png, .jpg, .gif or .bmp file.";
            return false;
        }
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = $"#img: image not found: {path}"; return false; }
            if (info.Length > MaxFileBytes)
            {
                error = $"#img: {info.Name} is too large ({info.Length / (1024 * 1024)} MB; the limit is {MaxFileBytes / (1024 * 1024)} MB).";
                return false;
            }
            if (!HasImageSignature(path))
            {
                error = $"#img: {info.Name} is not a readable png, jpg, gif or bmp image.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                         or ArgumentException or NotSupportedException)
        {
            error = $"#img: cannot read {path}: {ex.Message}";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The size to draw an image whose natural size is
    /// <paramref name="naturalW"/>×<paramref name="naturalH"/>. Both requested →
    /// exactly that (Genie 4 stretches to it); one requested → the other follows
    /// the aspect ratio (Genie 4 kept the natural size for the missing edge, which
    /// distorts — a deliberate improvement); neither → natural. The result is
    /// then scaled down uniformly so no edge exceeds <paramref name="maxEdge"/>,
    /// and never drops below 1 px.
    /// </summary>
    public static (int Width, int Height) FitSize(int naturalW, int naturalH,
                                                  int requestW, int requestH,
                                                  int maxEdge = MaxRenderEdge)
    {
        double nw = Math.Max(1, naturalW), nh = Math.Max(1, naturalH);
        double w, h;
        if (requestW > 0 && requestH > 0) { w = requestW; h = requestH; }
        else if (requestW > 0)            { w = requestW; h = requestW * nh / nw; }
        else if (requestH > 0)            { h = requestH; w = requestH * nw / nh; }
        else                              { w = nw; h = nh; }

        var longest = Math.Max(w, h);
        if (longest > maxEdge)
        {
            var scale = maxEdge / longest;
            w *= scale; h *= scale;
        }
        return (Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }

    // "w:32" / "width:32" (case-insensitive). True when the token IS a size
    // option; bad = it is one but the number doesn't parse (or isn't positive).
    private static bool TrySize(string arg, string shortKey, string longKey, out int value, out bool bad)
    {
        value = 0; bad = false;
        string? num = null;
        if (arg.Length > shortKey.Length && arg.StartsWith(shortKey, StringComparison.OrdinalIgnoreCase))
            num = arg[shortKey.Length..];
        else if (arg.Length > longKey.Length && arg.StartsWith(longKey, StringComparison.OrdinalIgnoreCase))
            num = arg[longKey.Length..];
        if (num is null) return false;
        bad = !int.TryParse(num, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out value) || value <= 0;
        if (bad) value = 0;
        return true;
    }

    // Header bytes of the four formats the renderer decodes, so a mis-named file
    // fails here with a clear message instead of as a blank line in the window.
    private static bool HasImageSignature(string path)
    {
        Span<byte> head = stackalloc byte[8];
        int read;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..read];
        return head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })    // PNG
            || head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })          // JPEG
            || head.StartsWith("GIF8"u8)                                 // GIF87a / GIF89a
            || head.StartsWith("BM"u8);                                  // BMP
    }
}
