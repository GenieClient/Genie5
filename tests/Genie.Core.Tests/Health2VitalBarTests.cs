using System;
using System.Collections.Generic;
using Genie.Core.Events;
using Genie.Core.GameState;
using Genie.Core.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #333: DR reports health under two bar ids — <c>health</c> from
/// <c>minivitals</c> and <c>health2</c> from the bar inside the
/// <c>injuries</c> dialog. Only <c>health</c> was mapped, so every
/// <c>health2</c> event fell through to the "Unknown progressBar id" debug log
/// (67 times in a single 45-minute recorded session).
///
/// The two never disagreed once both were flowing, but <c>health2</c> starts
/// FIRST — 13 samples landed before the first minivitals bar in that
/// recording — so <c>Vitals.Health</c>, <c>$health</c>, and the vitals panel
/// all held their default through session opening. That default is
/// <c>100</c> (GameState.cs:46), so an injured character read as FULL HEALTH
/// until the first minivitals bar arrived.
///
/// The parser still reports the server's id verbatim (the injuries dialog
/// renders that control, locked by
/// <c>InjuriesParsingTests.HealthProgressBar_InsideInjuriesDialog_StillEmitsVitals</c>);
/// the alias is applied by <see cref="VitalBars.Normalize"/> at each consumer
/// that maps bars onto vitals.
/// </summary>
public class Health2VitalBarTests
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

    private static (Feed feed, Genie.Core.Models.GameState state) StateFixture()
    {
        var state = new Genie.Core.Models.GameState();
        var feed  = new Feed();
        _ = new GameStateEngine(feed, state, NullLogger<GameStateEngine>.Instance);
        return (feed, state);
    }

    private static (Feed feed, Dictionary<string, string> globals) GlobalsFixture()
    {
        var state   = new Genie.Core.Models.GameState();
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var feed    = new Feed();
        _ = new ScriptGlobalsSync(state, globals, feed);
        return (feed, globals);
    }

    // ── Normalize ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("health2", "health")]
    [InlineData("HEALTH2", "health")]
    [InlineData("health", "health")]
    [InlineData("Mana", "mana")]
    [InlineData("encumbrance", "encumbrance")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_folds_health2_and_passes_everything_else_through(string? raw, string expected)
        => Assert.Equal(expected, VitalBars.Normalize(raw));

    // ── GameState ────────────────────────────────────────────────────────────

    [Fact]
    public void Health2_bar_updates_Vitals_Health()
    {
        var (feed, state) = StateFixture();

        feed.Push(new ProgressBarEvent("health2", 62, "HEALTH 62%"));

        Assert.Equal(62, state.Vitals.Health);
    }

    [Fact]
    public void Health2_seeds_health_before_the_first_minivitals_bar()
    {
        var (feed, state) = StateFixture();

        // The injuries dialog opens first — this is the recorded ordering.
        feed.Push(new ProgressBarEvent("health2", 47, "HEALTH 47%"));
        Assert.Equal(47, state.Vitals.Health);

        // minivitals then takes over; neither id is privileged, last write wins.
        feed.Push(new ProgressBarEvent("health", 100, "100"));
        Assert.Equal(100, state.Vitals.Health);

        feed.Push(new ProgressBarEvent("health2", 88, "HEALTH 88%"));
        Assert.Equal(88, state.Vitals.Health);
    }

    [Fact]
    public void Other_bars_are_unaffected_by_the_alias()
    {
        var (feed, state) = StateFixture();

        feed.Push(new ProgressBarEvent("mana", 71, "71"));
        feed.Push(new ProgressBarEvent("spirit", 90, "90"));
        feed.Push(new ProgressBarEvent("stamina", 55, "55"));
        feed.Push(new ProgressBarEvent("concentration", 12, "12"));
        feed.Push(new ProgressBarEvent("encumbrance", 30, "Light"));

        Assert.Equal(71, state.Vitals.Mana);
        Assert.Equal(90, state.Vitals.Spirit);
        Assert.Equal(55, state.Vitals.StaminaFatigue);
        Assert.Equal(12, state.Vitals.Concentration);
        Assert.Equal(30, state.Vitals.Encumbrance);

        // Untouched — and note the default is 100, i.e. "full health": before
        // this fix an injured character read as unhurt until minivitals began.
        Assert.Equal(100, state.Vitals.Health);
    }

    // ── Script globals ───────────────────────────────────────────────────────

    [Fact]
    public void Health2_bar_sets_dollar_health_and_never_invents_dollar_health2()
    {
        var (feed, globals) = GlobalsFixture();

        feed.Push(new ProgressBarEvent("health2", 62, "HEALTH 62%"));

        Assert.Equal("62", globals["health"]);
        Assert.Equal("HEALTH 62%", globals["healthBarText"]);

        // Genie 4 has no $health2 — the un-normalized id used to create one.
        Assert.False(globals.ContainsKey("health2"));
        Assert.False(globals.ContainsKey("health2BarText"));
    }
}
