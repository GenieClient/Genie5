using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #241: community travel.cmd occasionally sent the literal text
/// `go %offtransport` at ferry disembark. The variable is set purely
/// script-side by `action var offtransport X when &lt;arrival text&gt;`
/// triggers; the disembark label re-requests the room (`put look`), waits
/// through several pauses, and then sends `put go %offtransport`.
///
/// These tests replay that exact client-side contract deterministically,
/// with the action patterns and the disembark fragment taken from the live
/// travel.cmd (2026-08 pack) and the arrival lines shaped like Genie 5's
/// real rendered output (two-space indent on "You also see" lines, titles
/// in brackets — from RenucciDR session logs):
///
///  1. an arrival line delivered while the script sits in the post-look
///     pauses fires the action, sets the script-LOCAL var, and the later
///     `put go %offtransport` substitutes it — `go pier` goes out;
///  2. every one of travel.cmd's six offtransport patterns matches its
///     realistic rendered line;
///  3. when NO arrival text arrives (the true script-side race), the put
///     goes out with the raw token — the documented pre-existing behavior
///     the `warnrawvars` guard exists to surface.
/// </summary>
public class TransportDisembarkTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _sent = new();
    private readonly List<string> _echoed = new();
    private readonly ScriptEngine _engine;

    public TransportDisembarkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gc_disembark_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _engine = new ScriptEngine(_dir, new TypeAheadSession(),
                                   sendCommand: c => _sent.Add(c), echo: l => _echoed.Add(l));
    }

    public void Dispose()
    {
        _engine.StopAll();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    // The six action registrations, verbatim from travel.cmd (pack of 2026-08).
    private const string OffTransportActions =
        "action var offtransport platform when a barge platform\n" +
        "action var offtransport pier when the Riverhaven pier\n" +
        "action var offtransport beach when You also see the beach|mammoth and the beach\n" +
        "action var offtransport ladder when You also see a ladder|mammoth and a ladder\n" +
        "action var offtransport wharf when the Langenfirth wharf\n" +
        "action var offtransport dock when \\[\"Her Opulence\"\\]|\\[\"Hodierna's Grace\"\\]|\\[\"Kertigen's Honor\"\\]|Baso Docks|a dry dock\n";

    // The OFFTHERIDE tail, condensed but order-faithful: look, waits, send.
    // Pauses shortened (real script: 0.5 + 0.1 + 0.1 + 1 + 1) so the test
    // runs in ~1s of wall clock; the ordering contract is identical.
    private const string DisembarkTail =
        ":OFFTHERIDE\n" +
        "put look\n" +
        "pause 0.2\n" +
        "pause 0.2\n" +
        "put go %offtransport\n";

    private void Start(string body)
    {
        File.WriteAllText(Path.Combine(_dir, "travelx.cmd"), body);
        Assert.True(_engine.TryStart("travelx", Array.Empty<string>()));
    }

    /// <summary>Tick with real elapsed time until the predicate holds (the
    /// script's pauses are wall-clock) or ~3s passes. Feeds a prompt every
    /// cycle: DR prompts continuously in a live session, and the engine's
    /// 1-per-prompt send gate releases queued script puts on prompts — a
    /// promptless pump would wedge the second `put` forever and test a
    /// world that doesn't exist.</summary>
    private void PumpUntil(Func<bool> done, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done() && Environment.TickCount64 < deadline)
        {
            _engine.OnPrompt();
            _engine.Tick();
            Thread.Sleep(15);
        }
        _engine.Tick();
    }

    [Fact]
    public void Arrival_during_the_pauses_resolves_offtransport_before_the_send()
    {
        Start(OffTransportActions + DisembarkTail);

        // The script's own `put look` goes out first.
        PumpUntil(() => _sent.Contains("look"));
        Assert.Contains("look", _sent);

        // The look response arrives while the script sits in its pauses —
        // shaped exactly like Genie 5 renders it (two-space object indent).
        _engine.OnGameLine("[Riverhaven, West Bank Ferry Dock]");
        _engine.OnGameLine("The dock groans under the weight of cargo and passengers.");
        _engine.OnGameLine("  You also see the Riverhaven pier and some pilings.");
        _engine.OnGameLine("Obvious paths: east.");

        PumpUntil(() => _sent.Any(s => s.StartsWith("go ", StringComparison.Ordinal)));

        Assert.Contains("go pier", _sent);
        Assert.DoesNotContain(_sent, s => s.Contains("%offtransport", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("  You also see a barge platform and several crates.",          "go platform")]
    [InlineData("  You also see the Riverhaven pier and some pilings.",         "go pier")]
    [InlineData("  You also see the beach stretching away to the north.",       "go beach")]
    [InlineData("  You also see a ladder leading down and a coil of rope.",     "go ladder")]
    [InlineData("  You also see the Langenfirth wharf and a mooring post.",     "go wharf")]
    [InlineData("[\"Hodierna's Grace\"]",                                       "go dock")]
    public void Every_offtransport_pattern_matches_its_rendered_arrival_line(
        string arrivalLine, string expectedSend)
    {
        Start(OffTransportActions + DisembarkTail);
        PumpUntil(() => _sent.Contains("look"));

        _engine.OnGameLine(arrivalLine);

        PumpUntil(() => _sent.Any(s => s.StartsWith("go ", StringComparison.Ordinal)));
        Assert.Contains(expectedSend, _sent);
    }

    [Fact]
    public void No_arrival_text_still_sends_the_raw_token_after_the_pauses()
    {
        // The genuine script-side race: nothing matched, the variable is
        // unset, and the send goes out raw. The engine must neither hang nor
        // invent a value — this is the case #config warnrawvars surfaces,
        // and only a matchwait in the script itself can close it.
        Start(OffTransportActions + DisembarkTail);
        PumpUntil(() => _sent.Contains("look"));

        PumpUntil(() => _sent.Any(s => s.StartsWith("go ", StringComparison.Ordinal)));
        Assert.Contains(_sent, s => s.Contains("offtransport", StringComparison.Ordinal));
    }
}
