using System.Text.RegularExpressions;

namespace Genie.Core.Capture;

/// <summary>
/// Strips other-player social content from analyst captures.
///
/// <para>
/// This is the concrete implementation of policy gate <b>G2</b> documented on
/// <see cref="AI.AiContextBuffer"/>: <i>"strip other-player content before any
/// external send."</i> Per Simu's ToS, public-area player utterances are
/// licensed to Simu, not to us — and the project's hard-never list includes
/// "no shipping other players' speech to an external AI service." An analyst capture
/// exists precisely so an analyst can read it, so the redactor runs by default on anything
/// destined for the capture folder. It is intentionally reusable: the AI
/// pipeline's eventual G2 filter should call <see cref="RedactRawXml"/> too.
/// </para>
///
/// <para>Three surfaces across the two capture artifacts:</para>
/// <list type="bullet">
///   <item><see cref="ShouldDropStream"/> — authoritative, stream-level drop for
///   the parsed <c>_streams.txt</c> (each <see cref="Events.TextEvent"/> carries
///   its <c>Stream</c>, so this is exact).</item>
///   <item><see cref="ShouldDropContent"/> — content-level drop for parsed lines
///   whose text is other-player content delivered <i>bare on the <c>main</c>
///   stream</i> (no stream/preset wrapper), which the stream check can't see.</item>
///   <item><see cref="RedactRawXml"/> — best-effort strip for the raw <c>.xml</c>:
///   social <c>pushStream</c> blocks, whisper/speech <c>preset</c> spans, AND the
///   same bare-on-<c>main</c> content lines. Operates on tag-complete blocks, so
///   the caller buffers to a safe boundary (e.g. a <c>&lt;prompt&gt;</c>) first.</item>
/// </list>
///
/// <para>
/// <b>Closed gap (the former G2 leak):</b> some other-player content arrives bare
/// on the <c>main</c> stream with no <c>pushStream</c>/<c>preset</c> wrapper —
/// departed-soul (<c>DEAD&gt;</c>) chat, and inline whisper/speech whose
/// <see cref="Events.TextEvent"/> is tagged <c>main</c>. It used to leak into both
/// artifacts. <see cref="ShouldDropContent"/> and the content pass in
/// <see cref="RedactRawXml"/> now catch it via the <c>DEAD&gt;</c> prefix and bare
/// third-person-speech patterns — verified to 0 leaks by <c>TestHarness CAPTUREVERIFY</c>.
/// </para>
/// </summary>
public sealed class CaptureRedactor
{
    /// <summary>
    /// Default "social" stream set — other players' speech and presence. These
    /// are dropped from the parsed transcript unless the caller overrides.
    /// (DR stream ids, lower-case; matched case-insensitively.)
    /// </summary>
    public static readonly IReadOnlySet<string> SocialStreams =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "talk", "whispers", "thoughts", "familiar",
            "ooc", "conversation", "group", "logons", "death",
        };

    /// <summary>
    /// <c>&lt;preset id="…"&gt;</c> ids that wrap other-player speech on the
    /// <c>main</c> stream. Stripped from the raw XML by <see cref="RedactRawXml"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> SpeechPresets =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "whisper", "speech" };

    private readonly IReadOnlySet<string> _streams;

    /// <summary>Number of parsed lines dropped by <see cref="ShouldDropStream"/>.</summary>
    public int DroppedLines { get; private set; }

    /// <summary>Number of raw-XML spans blanked by <see cref="RedactRawXml"/>.</summary>
    public int DroppedXmlSpans { get; private set; }

    /// <summary>Number of outbound commands whose argument tail was blanked by
    /// <see cref="RedactOutboundCommand"/>.</summary>
    public int RedactedCommands { get; private set; }

    /// <param name="redactStreams">
    /// Streams to drop. Null uses <see cref="SocialStreams"/>. Pass an empty set
    /// to disable stream redaction entirely (raw-XML speech-preset stripping
    /// still applies).
    /// </param>
    public CaptureRedactor(IEnumerable<string>? redactStreams = null)
    {
        _streams = redactStreams is null
            ? SocialStreams
            : new HashSet<string>(redactStreams, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The effective set of streams this redactor drops.</summary>
    public IReadOnlySet<string> RedactedStreams => _streams;

    /// <summary>
    /// True if a parsed line on <paramref name="stream"/> should be dropped from
    /// the transcript. Increments <see cref="DroppedLines"/> when it returns true.
    /// </summary>
    public bool ShouldDropStream(string stream)
    {
        if (_streams.Contains(stream)) { DroppedLines++; return true; }
        return false;
    }

    // ── Content-level redaction (the bare-on-`main` leak) ─────────────────────
    // DR inlines some other-player content directly on the `main` stream with NO
    // pushStream/preset wrapper, so the stream/span passes above miss it entirely.
    // The confirmed leak: departed-soul ("Dead") chat, prefixed "DEAD>", plus bare
    // third-person speech. These content patterns catch it on both artifacts.

    // Any line DR prefixes with "DEAD>" — departed-soul speech AND emotes.
    private static readonly Regex DeadLine = new(
        @"(?m)^[ \t]*DEAD>.*$",
        RegexOptions.Compiled);

    // Bare third-person speech: "Name [adverb] says/asks/whispers/…, "…"".
    // The third-person verb (says, not "You say") excludes self; the required
    // quote excludes narrative. NPCs ("The guard says,") match too — deliberate
    // over-redaction: for a G2 gate a redacted NPC line is harmless, a leaked
    // player line is a policy breach. `[^"\r\n]` keeps each match on one line.
    private const string SpeechVerbs =
        "says|asks|whispers|exclaims|shouts|mutters|murmurs|growls|hisses|states|" +
        "remarks|replies|answers|responds|sneers|snaps|recites|sings|chants|" +
        "yells|cries|calls|drawls|stammers|sobs";
    private static readonly Regex OtherPlayerSpeechLine = new(
        @"(?m)^[ \t]*(?!You\b)[A-Z][\w'’.\-]*\b[^""\r\n]*?\b(?:" + SpeechVerbs + @")\b[^""\r\n]*""[^\r\n]*$",
        RegexOptions.Compiled);

    /// <summary>
    /// True if a parsed line's <b>content</b> is other-player speech/emote that
    /// arrived bare on the <c>main</c> stream (so <see cref="ShouldDropStream"/>
    /// can't see it). Increments <see cref="DroppedLines"/> when it returns true.
    /// </summary>
    public bool ShouldDropContent(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (DeadLine.IsMatch(text) || OtherPlayerSpeechLine.IsMatch(text))
        {
            DroppedLines++;
            return true;
        }
        return false;
    }

    // <pushStream id='talk'/> … <popStream/>  (push is usually self-closing)
    private static readonly Regex PushStreamBlock = new(
        @"<pushStream\s+id=['""](?<id>[^'""]+)['""][^>]*>.*?<popStream\s*/?>",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // <preset id='whisper'>…</preset>  /  <preset id='speech'>…</preset>
    private static readonly Regex SpeechPresetSpan = new(
        @"<preset\s+id=['""](?<id>whisper|speech)['""][^>]*>.*?</preset>",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Best-effort removal of complete social <c>pushStream</c> blocks and
    /// whisper/speech <c>preset</c> spans from a tag-complete block of raw XML.
    /// The structural tags are kept (so parser analysis still sees the shape);
    /// only the inner content is replaced with a redaction marker.
    /// </summary>
    public string RedactRawXml(string block)
    {
        block = PushStreamBlock.Replace(block, m =>
        {
            var id = m.Groups["id"].Value;
            if (!_streams.Contains(id)) return m.Value; // not a redacted stream
            DroppedXmlSpans++;
            return $"<pushStream id='{id}'/><!--[redacted]--><popStream/>";
        });

        block = SpeechPresetSpan.Replace(block, m =>
        {
            DroppedXmlSpans++;
            return $"<preset id='{m.Groups["id"].Value}'><!--[redacted]--></preset>";
        });

        // Content-level pass for bare-on-`main` other-player text (no wrapper) —
        // the leak the two span passes above miss. Replace the whole line with a
        // marker so the parser-shape stays intact but no other-player text ships.
        block = DeadLine.Replace(block, _ =>
        {
            DroppedXmlSpans++;
            return "<!--[redacted: dead]-->";
        });
        block = OtherPlayerSpeechLine.Replace(block, _ =>
        {
            DroppedXmlSpans++;
            return "<!--[redacted: speech]-->";
        });

        return block;
    }

    // ── Outbound redaction ───────────────────────────────────────────────────
    // The sent-command log is the outbound twin of the raw XML, and it carries
    // two things a shareable capture must never contain: credentials (the
    // `#connect`/`#lichconnect` family takes an account and password as bare
    // arguments) and the player's own social traffic (the inbound side of which
    // is already dropped by the stream/preset passes above — logging the
    // outgoing half would reinstate exactly the leak G2 closes).
    //
    // Both are redacted verb-first: the verb survives so an analyst can still
    // see WHEN a command was sent and correlate it with the server's reply,
    // which is the whole point of the log; only the tail is blanked.

    /// <summary>Commands whose entire argument tail is credentials.</summary>
    private static readonly Regex CredentialCommand = new(
        @"^\s*(?<verb>#(?:lich)?connect|#reconnect|#account|#password)\b.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Player-initiated social sends. The message body is the redacted part;
    /// a leading target (whisper/tell/send take one) goes with it, since a
    /// recipient name is itself other-player information.
    /// </summary>
    private static readonly Regex SocialCommand = new(
        @"^\s*(?<verb>whisper|whis|tell|say|'|says|send|reply|gweth|amulet|esp|think|" +
        @"thinkto|ooc|group|gtell|answer|hug|kiss|beckon|pray)\b.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Redact one outbound command for the capture's sent-command log. Returns
    /// the command unchanged when nothing matches (the common case — movement,
    /// combat, polls and every other mechanical command must survive verbatim,
    /// or the log cannot answer the question it exists to answer).
    /// Increments <see cref="RedactedCommands"/> on a hit.
    /// </summary>
    public string RedactOutboundCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return command;

        var m = CredentialCommand.Match(command);
        if (m.Success)
        {
            RedactedCommands++;
            return m.Groups["verb"].Value + " [redacted: credentials]";
        }

        m = SocialCommand.Match(command);
        if (m.Success)
        {
            RedactedCommands++;
            return m.Groups["verb"].Value + " [redacted: social]";
        }

        return command;
    }
}
