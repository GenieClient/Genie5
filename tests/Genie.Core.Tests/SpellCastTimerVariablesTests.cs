using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.GameState;
using Genie.Core.Parser;
using Genie.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #224 follow-ups (SaragosDR, 2026-08-10) — the two spell-timer
/// variables that looked right at prep start but wrong afterwards:
///
/// 1. <c>$spellpreptime</c> was one second short (19 for a 20s prep). The
///    server's <c>&lt;castTime&gt;</c> epoch is whole-second but the local
///    prep-start stamp has fractional ms, so the TimeSpan difference is
///    prepLen−0.x and a plain (int) cast truncated it down. Genie 4 floors
///    the start epoch first, then subtracts whole integers (FormMain.cs:6392
///    → Globals.cs:213) — the fix mirrors that arithmetic.
///
/// 2. <c>$casttimeremaining</c> never counted down — it was a stored global
///    written once per CastTimeEvent (= the full prep length at prep start).
///    Genie 4 recomputes <c>@casttimeremaining@</c> live at every variable
///    substitution (Globals.cs:215: spellpreptime − elapsed, clamped at 0);
///    the fix makes it a computed reserved var in ScriptEngine.
///
/// Plus the two parity gaps found alongside: <c>$casttime</c> is the tag's
/// RAW epoch in Genie 4 (Game.cs:2122 — scripts compose
/// <c>$casttime − $spellstarttime</c>), and the server's rare
/// <c>&lt;spelltime value='epoch'/&gt;</c> tag (Game.cs:2131) re-seeds the
/// prep-start server-authoritatively.
/// </summary>
public class SpellCastTimerVariablesTests
{
    // ── ScriptGlobalsSync fixtures (same shape as SpellPrepTimeTests) ──────
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

    // ── 1. $spellpreptime off-by-one ───────────────────────────────────────

    [Fact]
    public void Spellpreptime_is_exact_when_the_local_prep_stamp_has_fractional_ms()
    {
        var (state, globals, feed) = Fixture();

        // The SaragosDR case: 20s standard buff. Server says fully prepped at
        // a whole-second epoch; the local prep stamp landed 0.4s past its
        // second boundary. TimeSpan math gives 19.6 → the old (int) cast said
        // 19; G4's integer-epoch arithmetic says 20.
        state.Combat.SpellTimeStart = DateTimeOffset.FromUnixTimeSeconds(1_779_400_000).AddMilliseconds(400);
        state.Combat.CastTimeEnd    = DateTimeOffset.FromUnixTimeSeconds(1_779_400_020);

        feed.Push(new PromptEvent(DateTimeOffset.UtcNow));

        Assert.Equal("20", globals["spellpreptime"]);
    }

    // ── 2. $casttime = raw epoch (G4 Game.cs:2122) ─────────────────────────

    [Fact]
    public void Casttime_is_the_tags_raw_epoch_not_a_countdown()
    {
        var (_, globals, feed) = Fixture();

        feed.Push(new CastTimeEvent(DateTimeOffset.FromUnixTimeSeconds(1_779_403_385)));

        // G4 stores the attribute verbatim so scripts can do
        // `evalmath $casttime - $spellstarttime`.
        Assert.Equal("1779403385", globals["casttime"]);
    }

    [Fact]
    public void Casttimeremaining_is_not_a_stored_global()
    {
        var (_, globals, feed) = Fixture();

        feed.Push(new CastTimeEvent(DateTimeOffset.UtcNow.AddSeconds(20)));

        // The live countdown resolves in ScriptEngine.TryResolveVar; a stored
        // snapshot here would freeze at the full prep length (the #224 bug).
        Assert.False(globals.ContainsKey("casttimeremaining"));
    }

    // ── 3. $casttimeremaining counts down live ─────────────────────────────

    private static ScriptEngine Engine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_ctr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new ScriptEngine(dir, new TypeAheadSession(),
                                sendCommand: _ => { }, echo: _ => { });
    }

    [Fact]
    public void Casttimeremaining_resolves_live_from_the_host()
    {
        var se = Engine();
        int remaining = 15;
        se.CastTimeRemainingSeconds = () => remaining;

        Assert.Equal("15", se.ExpandGlobalVars("$casttimeremaining"));
        remaining = 3;  // simulated tick — same substitution must see the new value
        Assert.Equal("3", se.ExpandGlobalVars("$casttimeremaining"));
    }

    [Fact]
    public void Casttimeremaining_wins_over_a_stale_stored_global()
    {
        var se = Engine();
        se.Globals["casttimeremaining"] = "99";   // e.g. from an old profile / script
        se.CastTimeRemainingSeconds = () => 7;

        Assert.Equal("7", se.ExpandGlobalVars("$casttimeremaining"));
    }

    [Fact]
    public void Casttimeremaining_is_zero_without_a_host()
    {
        var se = Engine();
        Assert.Equal("0", se.ExpandGlobalVars("$casttimeremaining"));
    }

    // ── 4. <spelltime value='epoch'/> server tag ───────────────────────────

    private sealed class Collector : IObserver<GameEvent>
    {
        private readonly List<GameEvent> _sink;
        public Collector(List<GameEvent> sink) => _sink = sink;
        public void OnNext(GameEvent e) => _sink.Add(e);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private static List<GameEvent> Parse(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        foreach (var chunk in chunks) parser.Feed(chunk);
        return events;
    }

    [Fact]
    public void Spelltime_tag_emits_SpellTimeEvent()
    {
        var events = Parse("<spelltime value='1779403376'/>\n");
        var st = Assert.Single(events.OfType<SpellTimeEvent>());
        Assert.Equal(1_779_403_376, st.StartsAt.ToUnixTimeSeconds());
    }

    [Fact]
    public void Spelltime_tag_is_classified_as_consumed()
    {
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("spelltime"));
    }

    [Fact]
    public void Spelltime_tag_with_zero_value_is_inert()
    {
        var events = Parse("<spelltime value='0'/>\n");
        Assert.Empty(events.OfType<SpellTimeEvent>());
    }

    [Fact]
    public void SpellTimeEvent_reseeds_prep_start_while_a_spell_is_held()
    {
        var state = new Genie.Core.Models.GameState();
        var feed  = new Feed();
        using var _ = new GameStateEngine(feed, state, NullLogger<GameStateEngine>.Instance);

        feed.Push(new SpellEvent("Mental Blast"));
        var serverStart = DateTimeOffset.FromUnixTimeSeconds(1_779_403_376);
        feed.Push(new SpellTimeEvent(serverStart));

        Assert.Equal(serverStart, state.Combat.SpellTimeStart);
    }

    [Fact]
    public void SpellTimeEvent_is_ignored_with_nothing_prepared()
    {
        var state = new Genie.Core.Models.GameState();
        var feed  = new Feed();
        using var _ = new GameStateEngine(feed, state, NullLogger<GameStateEngine>.Instance);

        feed.Push(new SpellEvent("None"));
        feed.Push(new SpellTimeEvent(DateTimeOffset.FromUnixTimeSeconds(1_779_403_376)));

        // G4 forces $spellstarttime to 0 when preparedspell is None — the tag
        // must not resurrect a prep window.
        Assert.Null(state.Combat.SpellTimeStart);
    }
}
