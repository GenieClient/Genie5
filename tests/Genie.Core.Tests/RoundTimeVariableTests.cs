using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Shroom's live #226 verify (2026-08-13/14) — <c>$roundtime</c> got stuck at
/// the last roundtime length and never walked back down to 0.
///
/// <see cref="ScriptGlobalsSync"/> mirrored the value into the globals
/// dictionary on <see cref="RoundTimeEvent"/>, which fires when roundtime
/// *starts*. Nothing ever wrote it again, so once a 7-second RT landed,
/// <c>$roundtime</c> read "7" forever. The community
/// <c>automapper.cmd</c> gates every move on
/// <c>if ($roundtime &gt; 0) then pause $roundtime</c>, so each step slept the
/// full 7s: "moving like one room every 20 seconds". <c>#var roundtime 0</c>
/// could not rescue it either — globals outrank #var in
/// <c>TryResolveVar</c>, so the frozen mirror kept winning.
///
/// Genie 4 recomputes <c>roundTime − gametime</c> on EVERY prompt and stores
/// an explicit "0" once it lapses (Game.cs:2285-2304). We go one better and
/// resolve live per substitution, the same shape as the <c>$casttimeremaining</c>
/// fix in <see cref="SpellCastTimerVariablesTests"/> (public #224 follow-up),
/// while the prompt refresh keeps the mirrored dictionary honest for the
/// variables panel.
/// </summary>
public class RoundTimeVariableTests
{
    // ── ScriptGlobalsSync fixture (same shape as SpellCastTimerVariablesTests) ──
    private sealed class Feed : IObservable<GameEvent>
    {
        private readonly List<IObserver<GameEvent>> _subs = new();
        public IDisposable Subscribe(IObserver<GameEvent> observer)
        {
            _subs.Add(observer);
            return new Unsub(() => _subs.Remove(observer));
        }
        public void Push(GameEvent e) { foreach (var s in _subs.ToArray()) s.OnNext(e); }
        private sealed class Unsub : IDisposable
        {
            private readonly Action _a;
            public Unsub(Action a) => _a = a;
            public void Dispose() => _a();
        }
    }

    private static (Genie.Core.Models.GameState state, Dictionary<string, string> globals, Feed feed) Fixture()
    {
        var state   = new Genie.Core.Models.GameState();
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var feed    = new Feed();
        _ = new ScriptGlobalsSync(state, globals, feed);
        return (state, globals, feed);
    }

    private static ScriptEngine Engine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_rt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new ScriptEngine(dir, new TypeAheadSession(),
                                sendCommand: _ => { }, echo: _ => { });
    }

    // ── 1. Live resolution (the fix) ───────────────────────────────────────

    [Fact]
    public void Roundtime_resolves_live_from_the_host()
    {
        var se = Engine();
        int remaining = 7;
        se.RoundTimeRemainingSeconds = () => remaining;

        Assert.Equal("7", se.ExpandGlobalVars("$roundtime"));
        remaining = 2;   // simulated tick — the next substitution must see it
        Assert.Equal("2", se.ExpandGlobalVars("$roundtime"));
        remaining = 0;   // RT lapsed — this is what never used to happen
        Assert.Equal("0", se.ExpandGlobalVars("$roundtime"));
    }

    [Fact]
    public void Roundtimeremaining_is_the_same_live_value()
    {
        var se = Engine();
        se.RoundTimeRemainingSeconds = () => 4;

        Assert.Equal("4", se.ExpandGlobalVars("$roundtimeremaining"));
    }

    [Fact]
    public void Roundtime_is_zero_without_a_host()
    {
        var se = Engine();
        Assert.Equal("0", se.ExpandGlobalVars("$roundtime"));
    }

    // ── 2. The stuck-at-7 regression ───────────────────────────────────────

    [Fact]
    public void Roundtime_wins_over_a_frozen_stored_global()
    {
        // Exactly Shroom's state: the mirror froze at the last RT length while
        // the character has been out of roundtime for a while.
        var se = Engine();
        se.Globals["roundtime"] = "7";
        se.RoundTimeRemainingSeconds = () => 0;

        Assert.Equal("0", se.ExpandGlobalVars("$roundtime"));
    }

    [Fact]
    public void Automapper_move_gate_no_longer_pauses_when_rt_has_lapsed()
    {
        // automapper.cmd's per-move gate, verbatim. With the frozen mirror this
        // expanded to `if (7 > 0) then pause 7` on every single move.
        var se = Engine();
        se.Globals["roundtime"] = "7";           // frozen mirror from an old RT
        se.RoundTimeRemainingSeconds = () => 0;  // ...but RT is long gone

        Assert.Equal("if (0 > 0) then pause 0",
                     se.ExpandGlobalVars("if ($roundtime > 0) then pause $roundtime"));
    }

    [Fact]
    public void Typed_echo_of_roundtime_reports_the_live_value()
    {
        // Shroom typed `#echo $roundtime` and got 7 back. The command pipeline
        // expands through ICommandHost.ExpandVariables → ExpandGlobalVars, the
        // same resolver as script text, so it is covered by the same fix.
        var se = Engine();
        se.Globals["roundtime"] = "7";
        se.RoundTimeRemainingSeconds = () => 0;

        Assert.Equal("#echo 0", se.ExpandGlobalVars("#echo $roundtime"));
    }

    // ── 3. The mirrored dictionary decays too (G4 Game.cs:2285-2304) ───────

    [Fact]
    public void Prompt_clears_the_mirrored_roundtime_once_it_lapses()
    {
        var (state, globals, feed) = Fixture();

        // RT starts: 7 seconds out.
        var expires = DateTimeOffset.UtcNow.AddSeconds(7);
        state.Combat.RoundTimeEnd = expires;
        feed.Push(new RoundTimeEvent(expires));
        Assert.Equal("7", globals["roundtime"]);

        // RT lapses, then a prompt arrives. Before the fix nothing wrote these
        // keys again and both stayed at "7" for the rest of the session.
        state.Combat.RoundTimeEnd = DateTimeOffset.UtcNow.AddSeconds(-1);
        feed.Push(new PromptEvent(DateTimeOffset.UtcNow));

        Assert.Equal("0", globals["roundtime"]);
        Assert.Equal("0", globals["roundtimeremaining"]);
    }

    [Fact]
    public void Prompt_mirrors_the_remaining_seconds_while_still_in_roundtime()
    {
        var (state, globals, feed) = Fixture();

        state.Combat.RoundTimeEnd = DateTimeOffset.UtcNow.AddSeconds(5);
        feed.Push(new PromptEvent(DateTimeOffset.UtcNow));

        Assert.Equal("5", globals["roundtime"]);
        Assert.Equal("5", globals["roundtimeremaining"]);
    }
}
