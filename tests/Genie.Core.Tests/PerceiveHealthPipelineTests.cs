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

    /// <summary>
    /// <c>touch &lt;patient&gt;</c>, captured live 2026-09-28: no roundtime, no
    /// vitality line, so the only end of the block is the prompt, which arrives
    /// as a PromptEvent rather than text. The block used to stay open forever,
    /// so the Healing panel never got a reading.
    /// </summary>
    [Fact]
    public void A_touch_block_closes_on_the_prompt()
    {
        var f = new Fixture();

        f.Say("You lay your hand on Renucci's arm.",
              "You sense a successful empathic link has been forged between you and Renucci.",
              "Renucci's injuries include...",
              "Wounds to the LEFT ARM:",
              "  Fresh External:  light scratches -- negligible",
              "  Fresh Internal:  slightly tender -- negligible",
              "Wounds to the LEFT LEG:",
              "  Fresh External:  light scratches -- insignificant");
        Assert.Empty(f.Charts);

        f.Feed.Push(new PromptEvent(DateTimeOffset.UtcNow, "R"));

        var ev = Assert.Single(f.Charts);
        Assert.Equal("Renucci", ev.Health.Patient);
        Assert.Equal(WoundSeverity.Negligible,    ev.Health.Regions["leftArm"][InjuryAxis.FreshExternal]);
        Assert.Equal(WoundSeverity.Negligible,    ev.Health.Regions["leftArm"][InjuryAxis.FreshInternal]);
        Assert.Equal(WoundSeverity.Insignificant, ev.Health.Regions["leftLeg"][InjuryAxis.FreshExternal]);
        Assert.True(f.State.PatientHealth.ContainsKey("Renucci"));
    }

    /// <summary>After the prompt closes it, later game text is ordinary text
    /// again: a poison mention can no longer land on the patient's chart.</summary>
    [Fact]
    public void Text_after_the_prompt_does_not_join_the_closed_block()
    {
        var f = new Fixture();

        f.Say("Renucci's injuries include...",
              "Wounds to the HEAD:",
              "  Fresh External:  a cut -- harmful");
        f.Feed.Push(new PromptEvent(DateTimeOffset.UtcNow));
        f.Say("A merchant cries, \"Antidote for poison, cheap!\"");
        f.Feed.Push(new PromptEvent(DateTimeOffset.UtcNow));

        var ev = Assert.Single(f.Charts);
        Assert.False(ev.Health.IsPoisoned);
    }

    [Fact]
    public void A_prompt_with_no_open_block_publishes_nothing()
    {
        var f = new Fixture();
        f.Feed.Push(new PromptEvent(DateTimeOffset.UtcNow));
        Assert.Empty(f.Charts);
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
    /// <summary>
    /// An empath's <c>touch</c>: DR sends the whole block inside
    /// <c>&lt;pushStream id="familiar"&gt;</c>, twice nested. Verbatim from the
    /// 2026-09-30 live recording. Scoping the parser to <c>main</c> alone meant
    /// no touch ever produced a reading, so the Healing panel and the patient's
    /// injuries window stayed empty under a chart listing three wounds.
    /// </summary>
    [Fact]
    public void A_touch_block_on_the_familiar_stream_is_read_end_to_end()
    {
        var f = new Fixture();
        var parser = new Genie.Core.Parser.DrXmlParser(NullLogger<Genie.Core.Parser.DrXmlParser>.Instance);
        using var _ = parser.GameEvents.Subscribe(e => f.Feed.Push(e));

        parser.Feed(
            "You sense a successful empathic link has been forged between you and Renucci.\n" +
            "<pushStream id=\"familiar\" /><pushStream id=\"familiar\" ifClosedStyle=\"watching\"/>\n" +
            "Renucci's injuries include...\n" +
            "Wounds to the LEFT ARM:\n" +
            "  Fresh External:  light scratches -- negligible\n" +
            "Wounds to the LEFT LEG:\n" +
            "  Fresh External:  light scratches -- insignificant\n" +
            "Wounds to the CHEST:\n" +
            "  Fresh External:  light scratches -- insignificant\n" +
            "\n" +
            "Renucci has normal vitality.\n" +
            "<popStream/><popStream/><prompt time=\"1790789789\">&gt;</prompt>\n");

        var ev = Assert.Single(f.Charts);
        Assert.Equal("Renucci", ev.Health.Patient);
        Assert.Equal(WoundSeverity.Negligible,    ev.Health.Regions["leftArm"][InjuryAxis.FreshExternal]);
        Assert.Equal(WoundSeverity.Insignificant, ev.Health.Regions["leftLeg"][InjuryAxis.FreshExternal]);
        Assert.Equal(WoundSeverity.Insignificant, ev.Health.Regions["chest"][InjuryAxis.FreshExternal]);
    }

    [Fact]
    public void A_block_on_the_familiar_stream_lands_in_game_state()
    {
        var f = new Fixture();

        f.SayOn("familiar",
                "Renucci's injuries include...",
                "Wounds to the LEFT ARM:",
                "  Fresh External:  light scratches -- negligible",
                "Renucci has normal vitality.");

        Assert.True(f.State.PatientHealth.ContainsKey("Renucci"));
    }

    [Theory]
    [InlineData("talk")]
    [InlineData("whispers")]
    [InlineData("thoughts")]
    public void A_block_quoted_on_a_speech_stream_is_still_ignored(string stream)
    {
        var f = new Fixture();

        f.SayOn(stream,
                "Renucci's injuries include...",
                "Wounds to the HEAD:",
                "  Fresh External:  -- useless",
                "Renucci has some vitality.");

        Assert.Empty(f.Charts);
    }

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
