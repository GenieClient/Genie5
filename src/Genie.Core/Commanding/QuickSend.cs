namespace Genie.Core.Commanding;

/// <summary>
/// Genie 4's <b>quick-send</b> prefix. G4 declares <c>cQuickSendChar = '-'</c>
/// (Lists/Config.cs:15 — hardcoded, no #config knob) and its command parser
/// rewrites any ';'-chain segment starting with it to <c>#send</c> + remainder
/// (Core/Command.cs:254). G4's <c>#send</c> scanner (Send(), Core/Command.cs:2626)
/// then peels a leading run of digits/'.' — <b>no separating space required</b>,
/// so glued forms like <c>-3knock concealed door</c> work — as a POSITIVE
/// wait-before-send in seconds, queueing the remainder on the roundtime-gated
/// CommandQueue. The dash is a prefix marker, not a negative sign.
///
/// <para>This is how community scripts pace multi-command lines (public #278):
/// <c>put health;-0.05 encumbrance</c> = send <c>health</c> now, then
/// <c>encumbrance</c> 0.05s later (after any roundtime clears). Every G4 path
/// funnels through the rewrite — typed input, aliases, triggers, script
/// put/send, and queued commands all re-enter ParseCommand — so Genie 5 applies
/// it at the matching choke points: CommandEngine.ProcessInput's row loop, the
/// <c>#send</c> body, and ScriptEngine.HandleSendPut's chain segments.</para>
/// </summary>
public static class QuickSend
{
    /// <summary>G4 <c>cQuickSendChar</c>. Hardcoded '-' in G4 as well.</summary>
    public const char Char = '-';

    /// <summary>
    /// Split a quick-send segment into delay + command, mirroring G4's Send()
    /// scanner: after the dash, a leading run of digits (one '.' allowed) is the
    /// delay even with no separating space (<c>-3knock</c>); whitespace between
    /// the dash and the number is tolerated (<c>- 0.5 cast</c>). A lone dot is
    /// not a delay (G4: <c>sNumber != "."</c>). True only when a non-empty
    /// command remains — degenerate segments ("-", "-3", "-.") return false and
    /// should be left literal (a deliberate, visible deviation from G4, which
    /// drops them silently).
    /// </summary>
    public static bool TryParse(string? segment, out string delay, out string cmd)
    {
        delay = string.Empty;
        cmd = string.Empty;
        if (segment is null || segment.Length < 2 || segment[0] != Char) return false;

        var rest = segment[1..].TrimStart();
        var i = 0;
        bool dot = false, sawDigit = false;
        while (i < rest.Length && (char.IsDigit(rest[i]) || (rest[i] == '.' && !dot)))
        {
            if (rest[i] == '.') dot = true; else sawDigit = true;
            i++;
        }
        if (!sawDigit) i = 0;   // lone "." or no number at all — no delay

        delay = rest[..i];
        cmd = rest[i..].Trim();
        return cmd.Length > 0;
    }

    /// <summary>
    /// Rewrite a quick-send segment into its <c>{commandChar}send</c> form
    /// (<c>-0.05 encumbrance</c> → <c>#send 0.05 encumbrance</c>, <c>-pray</c> →
    /// <c>#send pray</c>). A space always separates delay and command because
    /// Genie 5's ParseSendDelay — unlike G4's character scanner — requires the
    /// boundary. False when <paramref name="segment"/> isn't a quick-send form.
    /// </summary>
    public static bool TryRewrite(string? segment, char commandChar, out string rewritten)
    {
        rewritten = string.Empty;
        if (!TryParse(segment, out var delay, out var cmd)) return false;
        rewritten = delay.Length > 0
            ? $"{commandChar}send {delay} {cmd}"
            : $"{commandChar}send {cmd}";
        return true;
    }
}
