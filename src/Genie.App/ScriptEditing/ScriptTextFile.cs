using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Genie.App.ScriptEditing;

/// <summary>
/// One script file as the built-in editor sees it (public #243): the text, plus
/// what it takes to write it back the way it was found — the encoding (and
/// whether it carried a byte-order mark) and the line-ending style — and a
/// fingerprint of the bytes last read or written, so an edit made by another
/// program can be told apart from our own save.
/// <para>No UI in here: the window owns the document, this owns the disk.</para>
/// </summary>
public sealed class ScriptTextFile
{
    /// <summary>Absolute path of the file.</summary>
    public string Path { get; }

    /// <summary>Encoding the file was read with; saves use it too. Carries its
    /// preamble setting, so a UTF-8 file without a BOM stays without one.</summary>
    public Encoding Encoding { get; private set; }

    /// <summary>The newline the file uses (the first one found; CRLF for a file
    /// with no line break yet on Windows, LF elsewhere). Saves write every line
    /// break with it.</summary>
    public string LineEnding { get; private set; }

    /// <summary>Set when the last save could not be written in the file's own
    /// encoding (a Latin-1 file that gained a character Latin-1 has no byte
    /// for) and fell back to UTF-8, so the caller can say so.</summary>
    public bool EncodingChangedOnLastSave { get; private set; }

    private byte[] _hash = [];
    private DateTime _stampTime;
    private long _stampLength = -1;

    private ScriptTextFile(string path, Encoding encoding, string lineEnding)
    {
        Path = path;
        Encoding = encoding;
        LineEnding = lineEnding;
    }

    /// <summary>Read <paramref name="path"/> and return its text.</summary>
    public static ScriptTextFile Open(string path, out string text)
    {
        var full  = System.IO.Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(full);
        var (enc, bomLength) = DetectEncoding(bytes);
        text = enc.GetString(bytes, bomLength, bytes.Length - bomLength);
        var file = new ScriptTextFile(full, enc, DetectLineEnding(text));
        file.Remember(bytes);
        return file;
    }

    /// <summary>Re-read the file from disk (after an outside change), picking up
    /// its encoding and line endings afresh.</summary>
    public string Reload()
    {
        var bytes = File.ReadAllBytes(Path);
        var (enc, bomLength) = DetectEncoding(bytes);
        var text = enc.GetString(bytes, bomLength, bytes.Length - bomLength);
        Encoding   = enc;
        LineEnding = DetectLineEnding(text);
        Remember(bytes);
        return text;
    }

    /// <summary>
    /// Write <paramref name="text"/> back atomically: the bytes go to a temp file
    /// in the same folder, which then replaces the original in one rename — a
    /// crash or a full disk mid-save leaves the old file whole, never a torn one.
    /// Every line break is written as <see cref="LineEnding"/>.
    /// </summary>
    public void Save(string text)
    {
        var normalized = NormalizeLineEndings(text, LineEnding);
        var bytes = Encode(normalized);

        var dir  = System.IO.Path.GetDirectoryName(Path)!;
        var temp = System.IO.Path.Combine(dir, $"{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(Path))
            {
                try { File.Replace(temp, Path, destinationBackupFileName: null); }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    // File.Replace is unavailable on some volumes (FAT, some
                    // network shares); a same-folder overwrite-rename is still a
                    // single step on every filesystem that matters here.
                    File.Move(temp, Path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, Path);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception) { /* best effort: a leftover .tmp is harmless */ }
        }
        Remember(bytes);
    }

    /// <summary>
    /// True when the file on disk no longer holds what we last read or wrote —
    /// another program saved it, or it was deleted. A touch that leaves the bytes
    /// the same is not a change.
    /// </summary>
    public bool HasChangedOnDisk()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists) return _stampLength >= 0;
            if (info.LastWriteTimeUtc == _stampTime && info.Length == _stampLength) return false;
            var bytes = File.ReadAllBytes(Path);
            if (SHA256.HashData(bytes).AsSpan().SequenceEqual(_hash))
            {
                _stampTime = info.LastWriteTimeUtc;   // same bytes, newer stamp
                return false;
            }
            return true;
        }
        catch (IOException)
        {
            // Mid-write by the other program: not decidable yet. The next
            // check (focus, or the watcher's next event) settles it.
            return false;
        }
    }

    /// <summary>Take the file's current disk state as the known one without
    /// reading it into the editor ("keep my version"), so the same outside change
    /// is not reported twice. The next <see cref="Save"/> overwrites it.</summary>
    public void AcknowledgeDiskState()
    {
        try
        {
            if (File.Exists(Path)) Remember(File.ReadAllBytes(Path));
            else { _hash = []; _stampLength = -1; }
        }
        catch (IOException) { /* still mid-write: the next check reports it again */ }
    }

    /// <summary>True when the file has been removed since we last saw it.</summary>
    public bool IsMissingOnDisk => !File.Exists(Path);

    /// <summary>Short label for the status bar: "UTF-8", "UTF-8 BOM", "UTF-16 LE", ….</summary>
    public string EncodingLabel
    {
        get
        {
            var bom = Encoding.GetPreamble().Length > 0;
            return Encoding switch
            {
                UTF8Encoding     => bom ? "UTF-8 BOM" : "UTF-8",
                UTF32Encoding u  => u.GetPreamble().SequenceEqual(new byte[] { 0, 0, 0xFE, 0xFF }) ? "UTF-32 BE" : "UTF-32 LE",
                UnicodeEncoding  => Encoding.CodePage == 1201 ? "UTF-16 BE" : "UTF-16 LE",
                _                => Encoding.WebName.ToUpperInvariant(),
            };
        }
    }

    /// <summary>"CRLF" / "LF" / "CR".</summary>
    public string LineEndingLabel => LineEnding switch { "\r\n" => "CRLF", "\r" => "CR", _ => "LF" };

    private byte[] Encode(string text)
    {
        EncodingChangedOnLastSave = false;
        byte[] body;
        try
        {
            body = Encoding.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            // Only the Latin-1 fallback is strict (see DetectEncoding); the
            // Unicode encodings can represent everything.
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            EncodingChangedOnLastSave = true;
            body = Encoding.GetBytes(text);
        }
        var preamble = Encoding.GetPreamble();
        if (preamble.Length == 0) return body;
        var all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return all;
    }

    private void Remember(byte[] bytes)
    {
        _hash = SHA256.HashData(bytes);
        try
        {
            var info = new FileInfo(Path);
            _stampTime   = info.LastWriteTimeUtc;
            _stampLength = info.Length;
        }
        catch (IOException) { _stampLength = -1; }
    }

    /// <summary>
    /// The encoding of <paramref name="bytes"/> and the length of its byte-order
    /// mark. A BOM decides it; without one, bytes that are valid UTF-8 are UTF-8
    /// (no BOM, kept that way), and anything else is read as Latin-1 — every
    /// byte maps to one character and back, so a legacy-codepage script survives
    /// an edit byte-for-byte outside the lines actually changed.
    /// </summary>
    internal static (Encoding Encoding, int BomLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding("iso-8859-1", EncoderFallback.ExceptionFallback,
                                         DecoderFallback.ReplacementFallback), 0);
        }
    }

    /// <summary>The first line break in <paramref name="text"/>, or the
    /// platform's newline when there is none.</summary>
    internal static string DetectLineEnding(string text)
    {
        var i = text.IndexOfAny(['\r', '\n']);
        if (i < 0) return OperatingSystem.IsWindows() ? "\r\n" : "\n";
        if (text[i] == '\n') return "\n";
        return i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
    }

    /// <summary>Rewrite every CRLF / CR / LF in <paramref name="text"/> as
    /// <paramref name="eol"/>.</summary>
    internal static string NormalizeLineEndings(string text, string eol)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append(eol);
            }
            else if (c == '\n') sb.Append(eol);
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
