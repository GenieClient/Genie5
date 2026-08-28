using System;
using System.Collections.Generic;
using Genie.Core.Config;
using Genie.Core.Events;
using Genie.Core.GameState;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #261: roundtime, cast time, and spell-prep time arrive as ABSOLUTE
/// server epochs but were compared against the LOCAL clock, so any PC-clock
/// skew landed straight in the values — a machine 97s behind the server
/// displayed `roundtime 3` as a 100-second RT, wedged #send behind the RT
/// gate, and stalled every RT-gated script. Genie 4 was immune by
/// construction (rt = roundTime − gametime, two server values;
/// Game.cs:2278-2288).
///
/// The fix: GameStateEngine learns `offset = serverNow − localNow` from every
/// plausible &lt;prompt time=…/&gt; and converts each server instant to a
/// local instant at ingestion. Consumers are untouched. $gametime, $casttime,
/// and $spellstarttime stay RAW server epochs (scripts compose them as
/// server-minus-server differences).
/// </summary>
public class ServerClockOffsetTests
{
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

    private static readonly DateTimeOffset LocalNow =
        new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    private static (Feed feed, Genie.Core.Models.GameState state, GameStateEngine engine)
        Build(GenieConfig? config = null)
    {
        var state = new Genie.Core.Models.GameState();
        var feed  = new Feed();
        var engine = new GameStateEngine(feed, state,
                                         NullLogger<GameStateEngine>.Instance,
                                         utcNow: () => LocalNow)
                     { Config = config };
        return (feed, state, engine);
    }

    private static DateTimeOffset Epoch(long secondsFromLocalNow)
        => LocalNow.AddSeconds(secondsFromLocalNow);

    [Fact]
    public void Clock_behind_server_no_longer_inflates_roundtime()
    {
        // The reported case: PC ~97s behind the server. Server says "now" is
        // localNow+97; a 3-second RT expires at localNow+100 in server terms.
        var (feed, state, _) = Build();
        feed.Push(new PromptEvent(Epoch(97)));
        feed.Push(new RoundTimeEvent(Epoch(100)));

        // Pre-fix this read as a 100-second RT; corrected it is 3 seconds.
        Assert.Equal(Epoch(3), state.Combat.RoundTimeEnd);
    }

    [Fact]
    public void Clock_ahead_of_server_no_longer_erases_roundtime()
    {
        // Mirror failure: PC 97s ahead → RT read 0 and scripts fired into it.
        var (feed, state, _) = Build();
        feed.Push(new PromptEvent(Epoch(-97)));
        feed.Push(new RoundTimeEvent(Epoch(-94)));   // 3s RT in server terms

        Assert.Equal(Epoch(3), state.Combat.RoundTimeEnd);
    }

    [Fact]
    public void RoundTime_before_any_prompt_passes_through_unconverted()
    {
        // <roundTime> can precede the first <prompt>; without an offset the
        // pre-fix behavior is kept rather than guessing.
        var (feed, state, _) = Build();
        feed.Push(new RoundTimeEvent(Epoch(100)));

        Assert.Equal(Epoch(100), state.Combat.RoundTimeEnd);
    }

    [Fact]
    public void Implausible_prompt_stamps_never_teach_the_offset()
    {
        var (feed, state, _) = Build();
        // Epoch 0 (a parser fallback for a missing/бad time attr) and ancient
        // garbage must not become a clock source.
        feed.Push(new PromptEvent(DateTimeOffset.UnixEpoch));
        feed.Push(new RoundTimeEvent(Epoch(100)));

        Assert.Equal(Epoch(100), state.Combat.RoundTimeEnd);
    }

    [Fact]
    public void Replay_roundtimes_land_at_their_relative_position()
    {
        // Replayed recordings carry prompts from days ago. The learned offset
        // is large and negative, and recorded RTs now come out at the correct
        // RELATIVE position instead of reading as long-expired.
        var (feed, state, _) = Build();
        var recordedPrompt = LocalNow.AddDays(-3);
        feed.Push(new PromptEvent(recordedPrompt));
        feed.Push(new RoundTimeEvent(recordedPrompt.AddSeconds(5)));

        Assert.Equal(Epoch(5), state.Combat.RoundTimeEnd);
    }

    [Fact]
    public void RoundTimeOffset_config_composes_after_the_conversion()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                          "genie_rtoffset_" + Guid.NewGuid().ToString("N"));
        var lds  = new Genie.Core.Runtime.LocalDirectoryService("GenieRtOffsetTest", root);
        lds.UseExplicitRoot(root);
        var cfg = new GenieConfig(lds) { RoundTimeOffset = 2 };
        var (feed, state, _) = Build(cfg);
        feed.Push(new PromptEvent(Epoch(97)));
        feed.Push(new RoundTimeEvent(Epoch(100)));

        // 3s corrected RT + the user's 2s safety margin.
        Assert.Equal(Epoch(5), state.Combat.RoundTimeEnd);
    }

    [Fact]
    public void CastTime_is_converted_like_roundtime()
    {
        var (feed, state, _) = Build();
        feed.Push(new PromptEvent(Epoch(97)));
        feed.Push(new CastTimeEvent(Epoch(97 + 20)));

        Assert.Equal(Epoch(20), state.Combat.CastTimeEnd);
    }

    [Fact]
    public void SpellTime_converts_the_countup_but_keeps_the_raw_epoch_for_scripts()
    {
        var (feed, state, _) = Build();
        feed.Push(new PromptEvent(Epoch(97)));
        feed.Push(new SpellEvent("Mental Blast"));
        var serverStart = Epoch(97);               // prep began "now" in server terms
        feed.Push(new SpellTimeEvent(serverStart));

        // Count-up anchor is local — $spelltime reads 0 at prep start, not ±97.
        Assert.Equal(Epoch(0), state.Combat.SpellTimeStart);
        // $spellstarttime publishes the tag's own raw server epoch.
        Assert.Equal(serverStart.ToUnixTimeSeconds(), state.Combat.SpellTimeStartServerEpoch);
    }

    [Fact]
    public void Local_prep_stamp_publishes_a_server_equivalent_epoch()
    {
        // No <spelltime> tag: the prep start is stamped locally, but the raw
        // twin must still be composable with the raw $casttime — so it gets
        // the server-equivalent instant (localNow + offset).
        var (feed, state, _) = Build();
        feed.Push(new PromptEvent(Epoch(97)));
        feed.Push(new SpellEvent("Mental Blast"));

        Assert.Equal(LocalNow, state.Combat.SpellTimeStart);
        Assert.Equal(Epoch(97).ToUnixTimeSeconds(), state.Combat.SpellTimeStartServerEpoch);
    }

    [Fact]
    public void Prompt_learned_offset_updates_with_each_prompt()
    {
        var (feed, state, _) = Build();
        feed.Push(new PromptEvent(Epoch(97)));
        feed.Push(new RoundTimeEvent(Epoch(100)));
        Assert.Equal(Epoch(3), state.Combat.RoundTimeEnd);

        // Clock resynced mid-session (skew gone): next prompt re-teaches.
        feed.Push(new PromptEvent(Epoch(0)));
        feed.Push(new RoundTimeEvent(Epoch(4)));
        Assert.Equal(Epoch(4), state.Combat.RoundTimeEnd);
    }
}
