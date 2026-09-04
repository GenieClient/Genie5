using System.Reactive.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Genie.Core.Events;

namespace Genie.Core.Capture;

/// <summary>
/// "Capture for Analyst" recorder. Where <c>SessionRecorder</c> dumps the raw
/// stream verbatim for debugging, this writes a <b>self-describing, redacted</b>
/// capture set intended to be handed to an analyst (human or an AI assistant) and kept in
/// a readable location:
///
/// <list type="number">
///   <item><c>{base}.xml</c> — raw server XML, <b>best-effort redacted</b>
///   (<see cref="CaptureRedactor.RedactRawXml"/>). Ground truth for parser work.</item>
///   <item><c>{base}_streams.txt</c> — the parsed <see cref="TextEvent"/> stream,
///   one line each, <c>[stream]</c>-tagged, with social streams <b>dropped</b>,
///   <b>interleaved with every command the client sent</b> as <c>[SENT]</c>
///   lines. The clean view a reader actually scans — and the only artifact that
///   shows cause as well as effect, so a server reply can be attributed to the
///   command that provoked it (including sends the client made on its own:
///   polls, extension refreshes, <c>;</c>-split segments).</item>
///   <item><c>{base}.meta.json</c> — character, recipe, timings, byte/line counts,
///   and a redaction summary, so the capture needs no external explanation.</item>
/// </list>
///
/// <para>
/// Redaction (policy gate G2) is <b>on by default</b>: an analyst capture exists
/// to be read off-machine, and the project's hard-never list forbids shipping
/// other players' speech to an external AI service. See <see cref="CaptureRedactor"/>.
/// </para>
///
/// <para>
/// Threading: <see cref="GenieCore.RawXmlStream"/> drives the parser synchronously,
/// so the raw and event subscriptions fire on the same connection thread; writes
/// are still guarded by <see cref="_gate"/> against the <see cref="Stop"/> race.
/// The raw file is flushed on <c>&lt;/prompt&gt;</c> boundaries so redaction always
/// sees tag-complete blocks.
/// </para>
/// </summary>
public sealed class AnalystCapture : IDisposable
{
    private const string PromptClose = "</prompt>";

    private readonly string          _captureDir;
    private readonly Action<string>? _diag;
    private readonly object          _gate = new();

    private StreamWriter? _xml;
    private StreamWriter? _streams;
    private IDisposable?  _rawSub;
    private IDisposable?  _eventSub;
    private IDisposable?  _sentSub;
    private string?       _basePath;        // path stem, sans extension
    private string        _rawPending = ""; // buffer awaiting a prompt boundary

    private CaptureRedactor _redactor = new();
    private CaptureRecipe?  _recipe;
    private string          _character = "";
    private DateTime        _startedUtc;
    private long            _rawBytes;
    private long            _keptLines;
    private long            _sentLines;
    private IReadOnlyDictionary<string, string?>? _extraMeta;

    public AnalystCapture(string captureDir, Action<string>? diagnosticLog = null)
    {
        _captureDir = captureDir;
        _diag       = diagnosticLog;
        Directory.CreateDirectory(_captureDir);
    }

    public bool    IsActive => _xml is not null;
    /// <summary>Absolute path stem of the active capture (no extension), or null.</summary>
    public string? BasePath => _basePath;

    /// <summary>
    /// Begin a capture. Pass <see cref="GenieCore.RawXmlStream"/> and
    /// <see cref="GenieCore.Events"/>. <paramref name="extraMeta"/> lets the caller
    /// fold in context it owns (guild, room, connection mode) without this class
    /// depending on the game-state model. Idempotent: stops any prior capture first.
    /// </summary>
    public void Start(
        IObservable<string>     rawXml,
        IObservable<GameEvent>  events,
        string                  characterName,
        DateTime                startedUtc,
        CaptureRecipe?          recipe        = null,
        CaptureRedactor?        redactor      = null,
        IReadOnlyDictionary<string, string?>? extraMeta = null,
        IObservable<string>?    sentCommands  = null)
    {
        Stop();

        _redactor   = redactor ?? recipe?.BuildRedactor() ?? new CaptureRedactor();
        _recipe     = recipe;
        _character  = string.IsNullOrWhiteSpace(characterName) ? "unknown" : characterName;
        _startedUtc = startedUtc;
        _extraMeta  = extraMeta;
        _rawBytes   = 0;
        _keptLines  = 0;
        _sentLines  = 0;
        _rawPending = "";

        var safe = _character;
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        _basePath = Path.Combine(_captureDir, $"capture_{safe}_{startedUtc.ToLocalTime():yyyyMMdd_HHmmss}");

        _xml     = new StreamWriter(_basePath + ".xml",         append: false) { AutoFlush = true };
        _streams = new StreamWriter(_basePath + "_streams.txt", append: false) { AutoFlush = true };

        _rawSub   = rawXml.Subscribe(OnRaw);
        _eventSub = events.OfType<TextEvent>().Subscribe(OnText);
        _sentSub  = sentCommands?.Subscribe(OnSent);

        _diag?.Invoke($"[analyst] capturing → {Path.GetFileName(_basePath)}.*  " +
                      $"(redacting: {string.Join(", ", _redactor.RedactedStreams)})");
    }

    private void OnRaw(string chunk)
    {
        lock (_gate)
        {
            if (_xml is null) return;
            _rawBytes += chunk?.Length ?? 0;
            _rawPending += chunk;

            // Flush only through the last complete prompt so the redactor never
            // sees a half-open <pushStream>/<preset> span. Hard cap guards memory
            // if a prompt somehow never arrives.
            int cut = _rawPending.LastIndexOf(PromptClose, StringComparison.Ordinal);
            if (cut >= 0) FlushRaw(cut + PromptClose.Length);
            else if (_rawPending.Length > 262_144) FlushRaw(_rawPending.Length);
        }
    }

    private void FlushRaw(int upto)
    {
        if (_xml is null || upto <= 0) return;
        var block = _rawPending[..upto];
        _rawPending = _rawPending[upto..];
        try { _xml.Write(_redactor.RedactRawXml(block)); }
        catch (Exception ex) { _diag?.Invoke($"[analyst] raw write failed: {ex.Message}"); }
    }

    private void OnText(TextEvent ev)
    {
        lock (_gate)
        {
            if (_streams is null) return;
            if (_redactor.ShouldDropStream(ev.Stream)) return;  // social stream → dropped
            if (_redactor.ShouldDropContent(ev.Text))  return;  // bare-on-main other-player content → dropped
            var prefix = ev.Stream is "main" or "" ? "" : $"[{ev.Stream}] ";
            try { _streams.WriteLine(prefix + ev.Text); _keptLines++; }
            catch (Exception ex) { _diag?.Invoke($"[analyst] stream write failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Record one outbound command. Redacted first (credentials and the
    /// player's own social traffic never reach a shareable capture — see
    /// <see cref="CaptureRedactor.RedactOutboundCommand"/>), then written to
    /// the transcript under the <c>[SENT]</c> tag, which no DR stream id can
    /// collide with because <see cref="OnText"/> emits stream tags lower-case.
    /// </summary>
    private void OnSent(string command)
    {
        lock (_gate)
        {
            if (_streams is null) return;
            try { _streams.WriteLine("[SENT] " + _redactor.RedactOutboundCommand(command)); _sentLines++; }
            catch (Exception ex) { _diag?.Invoke($"[analyst] sent write failed: {ex.Message}"); }
        }
    }

    /// <summary>Stop the capture, flush the raw tail, and write the meta sidecar.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_xml is null && _streams is null) return;

            _rawSub?.Dispose();   _rawSub   = null;
            _eventSub?.Dispose(); _eventSub = null;
            _sentSub?.Dispose();  _sentSub  = null;

            if (_rawPending.Length > 0) FlushRaw(_rawPending.Length);
            _rawPending = "";

            try { _xml?.Dispose(); }     catch { /* best effort */ }
            try { _streams?.Dispose(); } catch { /* best effort */ }
            _xml = null; _streams = null;

            WriteMeta();

            _diag?.Invoke($"[analyst] stopped → {Path.GetFileName(_basePath)}.*  " +
                          $"({_rawBytes:N0} raw bytes / {_keptLines:N0} kept lines / " +
                          $"{_sentLines:N0} sent / {_redactor.DroppedLines:N0} dropped)");
            _basePath = null;
        }
    }

    private void WriteMeta()
    {
        if (_basePath is null) return;
        var meta = new Dictionary<string, object?>
        {
            ["character"]       = _character,
            ["recipe"]          = _recipe?.Name,
            ["recipeSends"]     = _recipe?.Sends,
            ["startedUtc"]      = _startedUtc.ToString("o"),
            ["durationSeconds"] = Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1),
            ["rawBytes"]        = _rawBytes,
            ["keptLines"]       = _keptLines,
            ["sentCommands"]    = _sentLines,
            ["redaction"] = new Dictionary<string, object?>
            {
                ["streams"]         = _redactor.RedactedStreams,
                ["droppedLines"]    = _redactor.DroppedLines,
                ["droppedXmlSpans"]  = _redactor.DroppedXmlSpans,
                ["redactedCommands"] = _redactor.RedactedCommands,
            },
        };
        if (_extraMeta is not null)
            foreach (var (k, v) in _extraMeta) meta[k] = v;

        try
        {
            File.WriteAllText(_basePath + ".meta.json",
                JsonSerializer.Serialize(meta, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // keep names/UTF-8 readable
                }));
        }
        catch (Exception ex) { _diag?.Invoke($"[analyst] meta write failed: {ex.Message}"); }
    }

    public void Dispose() => Stop();
}
