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

    /// <summary>How long an empty temp directory is tolerated before the host is
    /// told. Generous — Lich has to start Ruby, create the directory and open the
    /// log — but short enough to answer "why is nothing showing up?" while the user
    /// is still looking at the screen.</summary>
    internal static readonly TimeSpan IdleWarningAfter = TimeSpan.FromSeconds(15);

    private static readonly int IdleWarningPolls =
        (int)(IdleWarningAfter.TotalMilliseconds / PollInterval.TotalMilliseconds);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly object _gate = new();

    /// <summary>
    /// Resolve Lich's temp directory the way <c>lich.rbw</c> itself does:
    /// <c>--temp=PATH</c> if present, else <c>{LICH_DIR}/temp</c> where
    /// <c>LICH_DIR</c> is <c>--home=PATH</c> or the directory holding
    /// <paramref name="lichPath"/>.
    /// </summary>
    /// <remarks>
    /// <para>Mirrors <c>lich.rbw</c>'s argument loop (<c>/^--temp=(.+)/</c>,
    /// <c>/^--home=(.+)/</c>) and <c>lib/constants.rb</c>
    /// (<c>TEMP_DIR ||= File.join(LICH_DIR, "temp")</c>). Only the <c>=</c> form is
    /// recognised because that is the only form Lich accepts — see
    /// <see cref="LichArgs.TryFindIgnoredDirFlag"/>, which rejects the others at
    /// launch rather than letting Genie tail a directory Lich silently ignored.</para>
    /// <para><paramref name="lichArgs"/> must be the argument string the process was
    /// actually launched with — i.e. after <see cref="LichLauncher.TryExpandArguments"/>
    /// has filled <c>{character}</c> / <c>{port}</c>. Passing the raw
    /// <c>#config lichargs</c> template makes <c>--temp=temp-{character}</c> resolve to
    /// a literal <c>temp-{character}</c> directory nothing ever writes to.</para>
    /// <para>Lich stores a relative <c>--temp=</c> / <c>--home=</c> verbatim, so it
    /// lands relative to Lich's working directory — which the launcher sets to the
    /// directory holding <c>lich.rbw</c>, not Genie's own cwd. Relative values are
    /// rooted there for the same reason.</para>
    /// </remarks>
    /// <exception cref="FormatException">
    /// <paramref name="lichArgs"/> has an unterminated quote. Propagated rather than
    /// swallowed into the <c>{LICH_DIR}/temp</c> fallback, which would tail the wrong
    /// directory (or nothing) and read as "Lich just isn't logging".
    /// </exception>
    public static string? ResolveTempDirectory(string? lichPath, string? lichArgs)
    {
        var tokens = LichArgs.Tokenize(lichArgs);
        var lichDir = string.IsNullOrWhiteSpace(lichPath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(lichPath.Trim()));

        if (LichArgs.TryParseDirFlag(tokens, "--temp", out var temp))
            return RootAtLichDir(temp, lichDir);

        // No --temp: TEMP_DIR is {LICH_DIR}/temp, and LICH_DIR is --home= when given,
        // otherwise the directory lich.rbw lives in.
        if (LichArgs.TryParseDirFlag(tokens, "--home", out var home))
            return Path.Combine(RootAtLichDir(home, lichDir), "temp");

        return string.IsNullOrEmpty(lichDir) ? null : Path.Combine(lichDir, "temp");
    }

    /// <summary>Resolve a relative <c>--temp=</c> / <c>--home=</c> value against Lich's
    /// working directory (<paramref name="lichDir"/>), which is where Lich itself
    /// resolves it. Absolute values, and any value we have no Lich directory for, are
    /// returned unchanged.</summary>
    private static string RootAtLichDir(string path, string? lichDir) =>
        Path.IsPathRooted(path) || string.IsNullOrEmpty(lichDir)
            ? path
            : Path.GetFullPath(Path.Combine(lichDir, path));

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
    /// <param name="onIdle">Fired once if no eligible log shows up within
    /// <see cref="IdleWarningAfter"/> — see the remarks.</param>
    /// <remarks>
    /// Lich creates its temp directory during startup, so "nothing there yet" is
    /// normal for the first moment and cannot be treated as an error. But if it stays
    /// empty, the tailer would otherwise poll a wrong or nonexistent path forever and
    /// present as "lichdebug does nothing" — the exact failure that made a discarded
    /// <c>--temp</c> argument so hard to trace. <paramref name="onIdle"/> gives the
    /// host something to say instead.
    /// </remarks>
    public void Start(
        string tempDir,
        DateTime notBeforeUtc,
        Action<string> onLine,
        Action<string>? onFileBound = null,
        Action<string>? onIdle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDir);
        ArgumentNullException.ThrowIfNull(onLine);

        Stop();

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            _loop = Task.Run(() => RunLoop(
                tempDir, notBeforeUtc.ToUniversalTime(), onLine, onFileBound, onIdle, cts.Token));
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
        Action<string>? onIdle,
        CancellationToken ct)
    {
        string? currentPath = null;
        FileStream? stream = null;
        StreamReader? reader = null;
        var pending = new StringBuilder();
        var polls = 0;
        var warned = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!warned && currentPath is null && ++polls > IdleWarningPolls)
                {
                    warned = true;
                    SafeInvoke(onIdle, tempDir);
                }

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
}
