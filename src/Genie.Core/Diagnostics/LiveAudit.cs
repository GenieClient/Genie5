using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Genie.Core.Events;
using Genie.Core.Parser;

namespace Genie.Core.Diagnostics;

/// <summary>Live Audit verbosity, set by <c>#audit on|off|xmlhunting</c>.</summary>
public enum AuditMode
{
    /// <summary>Auditing off.</summary>
    Off,
    /// <summary>Normal tee — raw XML + parsed events + zone/room.</summary>
    On,
    /// <summary>Normal tee PLUS XML tag-coverage hunting: every distinct element
    /// DR sends is classified (consumed / dropped-data / dropped-setting /
    /// unknown) on first sighting, with a coverage summary on stop — so XML we
    /// silently drop announces itself instead of hiding.</summary>
    XmlHunting,
}

/// <summary>
/// Developer troubleshooting aid: when enabled, tees the live session into a
/// single timestamped, human-readable log so a collaborator can
/// follow exactly what the server sent and how the parser routed it — without
/// the user pasting XML/screenshots.
///
/// <para>Each line is one of:</para>
/// <list type="bullet">
///   <item><c>XML</c> — a raw chunk straight off the wire (newlines folded to ⏎).</item>
///   <item><c>TEXT [stream]</c> — a parsed <see cref="TextEvent"/> with the stream
///         it was routed to (so a "wrong window" leak is obvious at a glance).</item>
///   <item><c>NAV</c> — a room change, annotated with the live <c>$zoneid</c> /
///         <c>$roomid</c> / <c>$zonename</c> so a stale-at-the-boundary mapper
///         (the travel-stalls-at-zone-edge symptom) shows up immediately.</item>
///   <item><c>IND</c> / <c>PROMPT</c> / <c>IMG</c> / <c>COMPASS</c> — the other
///         structured events.</item>
/// </list>
///
/// Off by default; toggled with <c>#audit on|off</c>. Writes to
/// <c>&lt;LogDir&gt;/live_audit.log</c> (truncated on each enable for a clean
/// read). Local-only — never leaves the machine.
/// </summary>
public sealed partial class LiveAudit : IDisposable
{
    private readonly object                  _lock = new();
    private readonly string                  _path;
    private readonly IObservable<string>     _rawXml;
    private readonly IObservable<GameEvent>  _events;
    private readonly Func<string, string>    _global;   // read a script global ($zoneid …)

    private StreamWriter? _writer;
    private IDisposable?  _rawSub;
    private IDisposable?  _evtSub;

    public bool Enabled { get; private set; }

    /// <summary>True when running in <see cref="AuditMode.XmlHunting"/> — the
    /// tag-coverage pass is active.</summary>
    public bool Hunting { get; private set; }

    public string Path => _path;

    // Tag-coverage state (only used while Hunting). First-sighting drives the
    // live HUNT lines; the running tally drives the stop-time summary.
    private static readonly Regex _tagRx = TagRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"<([A-Za-z][A-Za-z0-9]*)", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex TagRegex();
    private readonly Dictionary<string, int> _huntCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="path">Full path to the audit log file.</param>
    /// <param name="rawXml">The raw on-the-wire XML stream.</param>
    /// <param name="events">The parsed game-event stream.</param>
    /// <param name="globalLookup">Reads a script global by name (e.g. "zoneid").</param>
    public LiveAudit(string path, IObservable<string> rawXml,
                     IObservable<GameEvent> events, Func<string, string> globalLookup)
    {
        _path   = path;
        _rawXml = rawXml;
        _events = events;
        _global = globalLookup;
    }

    /// <param name="hunting">When true, also run the XML tag-coverage pass
    /// (<see cref="AuditMode.XmlHunting"/>).</param>
    public void Enable(bool hunting = false)
    {
        if (Enabled) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        // Truncate for a clean slate each time auditing is turned on.
        _writer = new StreamWriter(_path, append: false) { AutoFlush = true };
        Hunting = hunting;
        _huntCounts.Clear();
        Write(hunting ? "==== LIVE AUDIT START (XML HUNTING) ====" : "==== LIVE AUDIT START ====");
        if (hunting)
            Write("HUNT  legend: [consumed]=typed event · [DROP-DATA]=game data we discard · [drop-set]=settings noise · [UNKNOWN]=unhandled");

        // Raw first so an XML chunk and the events it produced sit adjacent.
        _rawSub = _rawXml.Subscribe(WriteRaw, ex => Write($"!! raw stream error: {ex.Message}"));
        _evtSub = _events.Subscribe(WriteEvent, ex => Write($"!! event stream error: {ex.Message}"));
        Enabled = true;
    }

    public void Disable()
    {
        if (!Enabled) return;
        _rawSub?.Dispose(); _rawSub = null;
        _evtSub?.Dispose(); _evtSub = null;
        if (Hunting) WriteHuntSummary();
        Write("==== LIVE AUDIT STOP ====");
        lock (_lock) { _writer?.Dispose(); _writer = null; }
        Enabled = false;
        Hunting = false;
    }

    /// <summary>Append an arbitrary annotation (e.g. the App's mapper LoadStatus)
    /// to the audit stream. No-op when auditing is off.</summary>
    public void Note(string tag, string message)
    {
        if (Enabled) Write($"{tag,-5} {message}");
    }

    private void WriteRaw(string chunk)
    {
        if (Hunting) HuntTags(chunk);
        var c = chunk.Replace("\r", "").Replace("\n", " ⏎ ").Trim();
        if (c.Length > 0) Write("XML   " + c);
    }

    // Tag-coverage pass: tally every element name and, the first time each is
    // seen this session, log how the parser treats it. Cheap — only runs while
    // hunting, never on the normal hot path.
    private void HuntTags(string chunk)
    {
        foreach (Match m in _tagRx.Matches(chunk))
        {
            var tag = m.Groups[1].Value.ToLowerInvariant();
            if (_huntCounts.TryGetValue(tag, out var n))
            {
                _huntCounts[tag] = n + 1;
            }
            else
            {
                _huntCounts[tag] = 1;
                var fate = DrXmlParser.ClassifyTag(tag);
                var flag = fate is DrXmlParser.TagFate.DroppedData or DrXmlParser.TagFate.Unknown ? "  ◄ NOT CONSUMED" : "";
                Write($"HUNT  new <{tag}>  {FateLabel(fate)}{flag}");
            }
        }
    }

    private void WriteHuntSummary()
    {
        Write("==== XML COVERAGE SUMMARY ====");
        var byFate = new Dictionary<DrXmlParser.TagFate, List<string>>();
        foreach (var (tag, count) in _huntCounts)
        {
            var fate = DrXmlParser.ClassifyTag(tag);
            if (!byFate.TryGetValue(fate, out var list))
                byFate[fate] = list = new List<string>();
            list.Add($"{tag}×{count}");
        }

        foreach (var fate in new[] { DrXmlParser.TagFate.Unknown, DrXmlParser.TagFate.DroppedData,
                                     DrXmlParser.TagFate.Consumed, DrXmlParser.TagFate.DroppedSetting })
        {
            if (!byFate.TryGetValue(fate, out var tags)) continue;
            tags.Sort(StringComparer.OrdinalIgnoreCase);
            Write($"HUNT  {FateLabel(fate)} ({tags.Count}): {string.Join(", ", tags)}");
        }

        var leaks = (byFate.TryGetValue(DrXmlParser.TagFate.DroppedData, out var d) ? d.Count : 0)
                  + (byFate.TryGetValue(DrXmlParser.TagFate.Unknown, out var u) ? u.Count : 0);
        Write(leaks == 0
            ? "HUNT  ✓ every element DR sent this session is consumed or known-discardable."
            : $"HUNT  ⚠ {leaks} element type(s) carry data we don't consume — see ◄ NOT CONSUMED lines above.");
    }

    private static string FateLabel(DrXmlParser.TagFate fate) => fate switch
    {
        DrXmlParser.TagFate.Consumed       => "[consumed]",
        DrXmlParser.TagFate.DroppedData    => "[DROP-DATA]",
        DrXmlParser.TagFate.DroppedSetting => "[drop-set]",
        _                                  => "[UNKNOWN] ",
    };

    private void WriteEvent(GameEvent e)
    {
        switch (e)
        {
            case TextEvent t:
                Write($"TEXT  [{t.Stream}] {t.Text}");
                break;
            case NavEvent n:
                Write($"NAV   rm={n.RoomId}  → $zoneid={_global("zoneid")} $roomid={_global("roomid")} $zonename=\"{_global("zonename")}\"");
                break;
            case IndicatorEvent i:
                Write($"IND   {i.IndicatorId}={(i.Visible ? "y" : "n")}");
                break;
            case PromptEvent p:
                Write($"PROMPT '{p.Indicator}'");
                break;
            case RoomImageEvent r:
                Write($"IMG   picture={r.PictureId}");
                break;
            case CompassEvent c:
                Write($"COMP  {c.RawXml}");
                break;
        }
    }

    private void Write(string line)
    {
        lock (_lock)
        {
            try { _writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}"); }
            catch { /* never let the audit log throw into the session */ }
        }
    }

    public void Dispose() => Disable();
}
