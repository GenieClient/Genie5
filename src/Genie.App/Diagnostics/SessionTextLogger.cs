using System.Collections.Specialized;
using System.IO;
using Genie.App.ViewModels;

namespace Genie.App.Diagnostics;

/// <summary>
/// Automatic rendered-text session log — the Genie 4 <c>AutoLog</c> feature.
/// Writes every line shown in the game window (game text, command echoes,
/// script / system lines) to a plain-text file under <c>{AppData}/Genie5/Logs/</c>,
/// gated by <c>GenieConfig.AutoLog</c> and started/stopped on connect/disconnect.
///
/// <para>Genie 4 parity: the filename is <c>{Character}{Game}_{yyyy-MM-dd}.log</c>
/// and opened in <b>append</b> mode — one file per character/game/day, so
/// multiple sessions on the same day concatenate.</para>
///
/// <para><b>Failure and flush policy.</b> Opening the file can fail — most
/// often a second Genie instance logging the same character on the same day
/// holds it. That is reported (<see cref="LastError"/>, <see cref="ErrorLog"/>)
/// and the logger simply stays off: a log file must never abort a connect.
/// Lines go into the writer's buffer on the UI thread and are flushed at most
/// once per <see cref="FlushInterval"/> (plus on <see cref="Stop"/>), so a busy
/// session does not pay a synchronous disk write per line, and a crash loses at
/// most the last second of text.</para>
///
/// <para><b>Midnight roll.</b> The file name carries the date, so a session left
/// open across local midnight rolls to the new day's file on its next line
/// (<see cref="RollIfNewDay"/>) rather than appending to yesterday's. A failed
/// roll keeps writing to the old file — losing the day label beats losing lines.</para>
///
/// <para>Sibling of <see cref="SessionRecorder"/> (which captures the raw XML
/// stream on a manual toggle); this one captures the <i>rendered</i> text
/// automatically. Same local-only policy stance — recording the user's own
/// client stream locally is fine; external transmission is gated elsewhere.</para>
/// </summary>
public sealed class SessionTextLogger : IDisposable
{
    /// <summary>Upper bound on how long a written line can sit unflushed.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly string _logsDir;
    // Guards _writer and _dirty: lines are written on the UI thread, the flush
    // timer fires on the thread pool.
    private readonly object _gate = new();
    private StreamWriter?   _writer;
    private bool            _dirty;
    private Timer?          _flushTimer;
    private GameTextViewModel? _source;
    private NotifyCollectionChangedEventHandler? _handler;
    private string? _currentFile;
    private string  _safeChar = "";
    private string  _safeGame = "";
    private DateTime _openDay;
    private readonly Func<DateTime> _now;

    public SessionTextLogger(string logsDir, Func<DateTime>? now = null)
    {
        _logsDir = logsDir;
        _now = now ?? (() => DateTime.Now);
        try { Directory.CreateDirectory(_logsDir); }
        catch (Exception ex) { ErrorLog.Log("SessionTextLogger.CreateDirectory", ex); }
    }

    /// <summary>True while a log file is open and subscribed.</summary>
    public bool    IsLogging   => _writer is not null;
    /// <summary>Absolute path of the open log file, or null when stopped.</summary>
    public string? CurrentFile => _currentFile;
    /// <summary>Why the last <see cref="Start"/> could not open its file, or null.</summary>
    public string? LastError   { get; private set; }

    /// <summary>
    /// Begin logging the rendered lines from <paramref name="source"/> to the
    /// per-character/day file. Idempotent — a prior log is closed first.
    /// Never throws: returns false (and leaves logging off) when the file
    /// cannot be opened, with the reason in <see cref="LastError"/>.
    /// </summary>
    public bool Start(GameTextViewModel source, string characterName, string gameName)
    {
        Stop();
        LastError = null;

        _safeChar = Sanitize(characterName, "unknown");
        _safeGame = Sanitize(gameName, string.Empty);
        var day = _now().Date;

        StreamWriter writer;
        string file;
        try
        {
            (writer, file) = OpenFor(day);
        }
        catch (Exception ex)
        {
            ErrorLog.Log("SessionTextLogger.OpenFile", ex);
            LastError = ex.Message;
            return false;
        }
        _openDay = day;

        lock (_gate)
        {
            _writer = writer;
            _dirty  = false;
        }
        _currentFile = file;
        _source = source;
        _handler = (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null) return;
            lock (_gate)
            {
                if (_writer is null) return;
                RollIfNewDay();
                foreach (TextLine line in e.NewItems)
                {
                    try { _writer.WriteLine(line.Text); _dirty = true; }
                    catch (Exception ex) { ErrorLog.Log("SessionTextLogger.Write", ex); }
                }
            }
        };
        source.Lines.CollectionChanged += _handler;
        _flushTimer = new Timer(_ => FlushIfDirty(), null, FlushInterval, FlushInterval);
        return true;
    }

    private (StreamWriter Writer, string File) OpenFor(DateTime day)
    {
        var file = Path.Combine(_logsDir, $"{_safeChar}{_safeGame}_{day:yyyy-MM-dd}.log");
        Directory.CreateDirectory(_logsDir);
        return (new StreamWriter(file, append: true), file);
    }

    /// <summary>Reopen under today's date when local midnight has passed since
    /// the file was opened. Caller holds <see cref="_gate"/>. A failed open leaves
    /// the old writer in place and retries on the next line.</summary>
    private void RollIfNewDay()
    {
        var day = _now().Date;
        if (day == _openDay || _writer is null) return;
        try
        {
            var (next, file) = OpenFor(day);
            try { _writer.Dispose(); }
            catch (Exception ex) { ErrorLog.Log("SessionTextLogger.CloseFile", ex); }
            _writer      = next;
            _currentFile = file;
            _openDay     = day;
            _dirty       = false;
        }
        catch (Exception ex)
        {
            ErrorLog.Log("SessionTextLogger.Roll", ex);
        }
    }

    /// <summary>Push buffered lines to disk now. The flush timer calls this on
    /// its own; it is public for tests and for callers that want the file
    /// current before reading it.</summary>
    public void FlushIfDirty()
    {
        lock (_gate)
        {
            if (_writer is null || !_dirty) return;
            try { _writer.Flush(); }
            catch (Exception ex) { ErrorLog.Log("SessionTextLogger.Flush", ex); }
            _dirty = false;
        }
    }

    /// <summary>Stop logging and close the file. Safe to call when not logging.</summary>
    public void Stop()
    {
        if (_source is not null && _handler is not null)
            _source.Lines.CollectionChanged -= _handler;
        _handler = null;
        _source  = null;

        _flushTimer?.Dispose();
        _flushTimer = null;

        lock (_gate)
        {
            try { _writer?.Dispose(); }   // Dispose flushes the buffered tail
            catch (Exception ex) { ErrorLog.Log("SessionTextLogger.CloseFile", ex); }
            _writer = null;
            _dirty  = false;
        }
        _currentFile = null;
    }

    public void Dispose() => Stop();

    private static string Sanitize(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value;
    }
}
