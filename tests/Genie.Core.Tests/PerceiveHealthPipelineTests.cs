using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core;
using Genie.Core.Events;
using Genie.Core.GameState;
using Genie.Core.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #277, engine level: a <c>perceive health</c> block arriving as
/// ordinary main-stream text reaches <c>GameState.PatientHealth</c> and raises
/// a <see cref="PatientHealthEvent"/>.
///
/// <para>The parser has its own tests; these cover the WIRING, which is where
/// this kind of feature actually fails. The block has no XML tag of its own, so
/// it has to be recognised inside plain text, on the right stream, and the
/// synthesized event has to reach the same stream parser events go out on.</para>
/// </summary>
public class PerceiveHealthPipelineTests
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
        private sealed class Unsub(Action a) : IDisposable
        {
            public void Dispose() => a();
        }
    }

    private sealed class Fixture
    {
        public Feed                      Feed     { get; } = new();
        public Genie.Core.Models.GameState State  { get; } = new();
        public List<GameEvent>           Emitted  { get; } = new();

        public Fixture()
        {
            var engine = new GameStateEngine(Feed, State, NullLogger<GameStateEngine>.Instance);
            engine.Emit = Emitted.Add;
        }

        /// <summary>Feed lines the way DR delivers them — main-stream text.</summary>
        public void Say(params string[] lines)
        {
            foreach (var l in lines) Feed.Push(new TextEvent("main", l));
        }

        /// <summary>The same lines on a different stream, to prove scoping.</summary>
        public void SayOn(string stream, params string[] lines)
        {
            foreach (var l in lines) Feed.Push(new TextEvent(stream, l));
        }

        public IReadOnlyList<PatientHealthEvent> Charts =>
            Emitted.OfType<PatientHealthEvent>().ToList();
    }

    [Fact]
    public void A_block_fed_as_game_text_lands_in_game_state()
    {
        var f = new Fixture();

        f.Say("Renucci's injuries include...",
              "Wounds to the LEFT ARM:",
              "  Fresh External:  a deep gash -- very severe",
              "  Scars Internal:  old scarring -- minor",
              "Renucci has little vitality (60%).");

        Assert.True(f.State.PatientHealth.TryGetValue("Renucci", out var chart));
        Assert.Equal("Renucci", chart!.Patient);
        Assert.Equal(WoundSeverity.VerySevere, chart.Regions["leftArm"][InjuryAxis.FreshExternal]);
        Assert.Equal(WoundSeverity.Minor,      chart.Regions["leftArm"][InjuryAxis.ScarInternal]);
        Assert.Equal(40, chart.VitalityPercent);
    }

    [Fact]
    public void A_completed_block_raises_a_patient_health_event()
    {
        var f = new Fixture();

        f.Say("Your injuries include...",
              "Wounds to the HEAD:",
              "  Fresh External:  a cut -- harmful",
              "You have some vitality.");

        var ev = Assert.Single(f.Charts);
        Assert.Equal(PatientHealth.SelfPatient, ev.Health.Patient);
        Assert.Equal(WoundSeverity.Harmful, ev.Health.Regions["head"][InjuryAxis.FreshExternal]);
    }

    /// <summary>Nothing fires until the block CLOSES — a half-read chart is not
    /// a reading, and publishing one would have consumers act on a patient
    /// whose worst wound has not arrived yet.</summary>
    [Fact]
    public void Nothing_is_published_until_the_block_closes()
    {
        var f = new Fixture();

        f.Say("Your injuries include...",
              "Wounds to the HEAD:",
              "  Fresh External:  a cut -- harmful");
        Assert.Empty(f.Charts);

        f.Say("Roundtime: 3 sec.");
        Assert.Single(f.Charts);
    }

    /// <summary>Two patients coexist — the map is keyed by name, which is the
    /// point of the shape being different from GameState.Injuries.</summary>
    [Fact]
    public void Readings_for_different_patients_coexist()
    {
        var f = new Fixture();

        f.Say("Renucci's injuries include...",
              "Wounds to the HEAD:",
              "  Fresh External:  -- minor",
              "Renucci has some vitality.");
        f.Say("Naper's injuries include...",
              "Wounds to the CHEST:",
              "  Fresh External:  -- severe",
              "Naper has some vitality.");

        Assert.Equal(2, f.State.PatientHealth.Count);
        Assert.Equal(WoundSeverity.Minor,
            f.State.PatientHealth["Renucci"].Regions["head"][InjuryAxis.FreshExternal]);
        Assert.Equal(WoundSeverity.Severe,
            f.State.PatientHealth["Naper"].Regions["chest"][InjuryAxis.FreshExternal]);
    }

    /// <summary>The dialog path is untouched — #277 is explicitly additive, and
    /// a perceive must not overwrite what the injuries dialog reported.</summary>
    [Fact]
    public void A_perceive_does_not_disturb_the_injuries_dialog_state()
    {
        var f = new Fixture();
        f.State.Injuries["head"] = new Genie.Core.Models.InjuryReading(InjuryKind.Wound, 2);

        f.Say("Your injuries include...",
              "Wounds to the HEAD:",
              "  Fresh External:  -- useless",
              "You have some vitality.");

        var dialog = f.State.Injuries["head"];
        Assert.Equal(InjuryKind.Wound, dialog.Kind);
        Assert.Equal(2, dialog.Severity);
    }

    /// <summary>A thought or whisper quoting these lines must not open a block
    /// or contribute a wound to anybody's chart.</summary>
    [Fact]
    public void A_block_on_another_stream_is_ignored()
    {
        var f = new Fixture();

        f.SayOn("thoughts",
                "Renucci's injuries include...",
                "Wounds to the HEAD:",
                "  Fresh External:  -- useless",
                "Renucci has some vitality.");

        Assert.Empty(f.State.PatientHealth);
        Assert.Empty(f.Charts);
    }

    [Fact]
    public void Ordinary_text_produces_no_reading()
    {
        var f = new Fixture();

        f.Say("A kobold arrives.",
              "You feel fully rested.",
              "Roundtime: 3 sec.");

        Assert.Empty(f.State.PatientHealth);
        Assert.Empty(f.Charts);
    }
}
