using System.Text;

namespace Genie.Core.Connection;

/// <summary>
/// Live-tails Lich's session debug log (<c>temp/debug-*.log</c>) for a Genie-owned
/// auto-launched process. Pure Core (no UI); the App prefixes lines and posts them
/// to the game window when <c>#config lichdebug</c> is on.
/// </summary>
public sealed class LichDebugLogTailer : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly object _gate = new();

    /// <summary>
    /// Resolve Lich's temp directory: <c>--temp=PATH</c> / <c>--temp PATH</c> /
    /// <c>--temp-dir=PATH</c> from <paramref name="lichArgs"/>, else
    /// <c>{dirname(lichPath)}/temp</c>.
    /// </summary>
    /// <exception cref="FormatException">
    /// <paramref name="lichArgs"/> has an unterminated quote. Propagated rather than
    /// swallowed into the <c>{dirname(lichPath)}/temp</c> fallback, which would tail
    /// the wrong directory (or nothing) and read as "Lich just isn't logging".
    /// </exception>
    public static string? ResolveTempDirectory(string? lichPath, string? lichArgs)
    {
        if (TryParseTempFromArgs(lichArgs, out var fromArgs))
            return fromArgs;

        if (string.IsNullOrWhiteSpace(lichPath))
            return null;

        var dir = Path.GetDirectoryName(Path.GetFullPath(lichPath.Trim()));
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "temp");
    }

    /// <summary>
    /// Newest <c>debug-*.log</c> under <paramref name="tempDir"/> whose
    /// <see cref="FileSystemInfo.LastWriteTimeUtc"/> is at or after
    /// <paramref name="notBeforeUtc"/>. Returns null when none qualify (e.g. only
    /// leftover files from earlier Lich runs).
    /// </summary>
    public static string? TryFindLatestDebugLog(string tempDir, DateTime notBeforeUtc)
    {
        if (string.IsNullOrWhiteSpace(tempDir) || !Directory.Exists(tempDir))
            return null;

        string? best = null;
        var bestWrite = DateTime.MinValue;

        foreach (var path in Directory.EnumerateFiles(tempDir, "debug-*.log"))
        {
            DateTime writeUtc;
            try { writeUtc = File.GetLastWriteTimeUtc(path); }
            catch { continue; }

            if (writeUtc < notBeforeUtc) continue;
            if (best is null || writeUtc > bestWrite ||
                (writeUtc == bestWrite && string.CompareOrdinal(path, best) > 0))
            {
                best = path;
                bestWrite = writeUtc;
            }
        }

        return best;
    }

    /// <summary>
    /// Begin polling <paramref name="tempDir"/> for an eligible debug log and
    /// emit complete lines via <paramref name="onLine"/>. Safe to call repeatedly;
    /// stops any prior loop first.
    /// </summary>
    /// <param name="tempDir">Lich temp directory containing <c>debug-*.log</c>.</param>
    /// <param name="notBeforeUtc">Ignore files last written before this (process start).</param>
    /// <param name="onLine">Raw log line (no prefix). Must not throw.</param>
    /// <param name="onFileBound">Fired once when a new file is opened for tailing.</param>
    public void Start(
        string tempDir,
        DateTime notBeforeUtc,
        Action<string> onLine,
        Action<string>? onFileBound = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDir);
        ArgumentNullException.ThrowIfNull(onLine);

        Stop();

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            _loop = Task.Run(() => RunLoop(tempDir, notBeforeUtc.ToUniversalTime(), onLine, onFileBound, cts.Token));
        }
    }

    /// <summary>Stop the background loop. Idempotent.</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null) return;
        try { cts.Cancel(); }
        catch { /* ignore */ }
        try { cts.Dispose(); }
        catch { /* ignore */ }

        // Don't block the UI on a stuck read — best-effort join with a short wait.
        if (loop is not null)
        {
            try { loop.Wait(TimeSpan.FromSeconds(2)); }
            catch { /* ignore */ }
        }
    }

    public void Dispose() => Stop();

    private static void RunLoop(
        string tempDir,
        DateTime notBeforeUtc,
        Action<string> onLine,
        Action<string>? onFileBound,
        CancellationToken ct)
    {
        string? currentPath = null;
        FileStream? stream = null;
        StreamReader? reader = null;
        var pending = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var latest = TryFindLatestDebugLog(tempDir, notBeforeUtc);
                    if (latest is not null &&
                        !string.Equals(latest, currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        CloseReaders(ref stream, ref reader);
                        pending.Clear();
                        currentPath = latest;
                        stream = new FileStream(
                            latest,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                        SafeInvoke(onFileBound, latest);
                    }

                    if (reader is not null)
                        Drain(reader, pending, onLine);
                }
                catch (IOException)
                {
                    // File not ready / briefly locked — retry next poll.
                    CloseReaders(ref stream, ref reader);
                    currentPath = null;
                }
                catch (UnauthorizedAccessException)
                {
                    CloseReaders(ref stream, ref reader);
                    currentPath = null;
                }
                catch
                {
                    // Never escape into the connect path / crash the host.
                }

                try { Task.Delay(PollInterval, ct).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            CloseReaders(ref stream, ref reader);
        }
    }

    private static void Drain(StreamReader reader, StringBuilder pending, Action<string> onLine)
    {
        var buf = new char[4096];
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
        {
            pending.Append(buf, 0, n);
            EmitCompleteLines(pending, onLine);
        }
    }

    private static void EmitCompleteLines(StringBuilder pending, Action<string> onLine)
    {
        while (true)
        {
            var s = pending.ToString();
            var idx = s.IndexOf('\n');
            if (idx < 0) break;

            var line = s[..idx];
            if (line.EndsWith('\r')) line = line[..^1];
            pending.Remove(0, idx + 1);
            SafeInvoke(onLine, line);
        }
    }

    private static void CloseReaders(ref FileStream? stream, ref StreamReader? reader)
    {
        try { reader?.Dispose(); } catch { /* ignore */ }
        try { stream?.Dispose(); } catch { /* ignore */ }
        reader = null;
        stream = null;
    }

    private static void SafeInvoke(Action<string>? action, string arg)
    {
        if (action is null) return;
        try { action(arg); }
        catch { /* best-effort */ }
    }

    /// <summary>Parse <c>--temp=</c>, <c>--temp-dir=</c>, or <c>--temp PATH</c> from a
    /// lichargs string. Tokenized through <see cref="LichArgs.Tokenize"/> — the same
    /// splitter <see cref="LichLauncher"/> uses to build argv — so a quoted path with
    /// spaces resolves to the directory Lich was actually handed.</summary>
    internal static bool TryParseTempFromArgs(string? lichArgs, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(lichArgs)) return false;

        var tokens = LichArgs.Tokenize(lichArgs);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.StartsWith("--temp=", StringComparison.OrdinalIgnoreCase))
            {
                path = TrimTrailingSeparators(t["--temp=".Length..]);
                return path.Length > 0;
            }
            if (t.StartsWith("--temp-dir=", StringComparison.OrdinalIgnoreCase))
            {
                path = TrimTrailingSeparators(t["--temp-dir=".Length..]);
                return path.Length > 0;
            }
            if (t.Equals("--temp", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("--temp-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Count) return false;
                path = TrimTrailingSeparators(tokens[i + 1]);
                return path.Length > 0;
            }
        }

        return false;
    }

    private static string TrimTrailingSeparators(string p) =>
        p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
