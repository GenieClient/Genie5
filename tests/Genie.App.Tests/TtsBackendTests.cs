using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Genie.App.Services;
using Genie.Core.Config;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #368 — TTS could only speak with bundled Piper models, never the voices
/// already installed on the computer (SAPI5 David / Zira / Ivona 2, macOS
/// <c>say</c>, speech-dispatcher). These pin the backend abstraction with fake
/// backends: backend selection + the Piper fallback, the <c>ttsvoice</c>
/// back-compat, the merged / labelled voice list, stop and barge-in across
/// backends, and the CLI voice-list parsers against captured output.
/// </summary>
public sealed class TtsBackendTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "genie_ttsbackend_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    /// <summary>A backend that records what it was asked to say. With
    /// <see cref="BlockFirst"/>, the first lines hold until the stop predicate fires.</summary>
    private sealed class FakeBackend : ITextToSpeech
    {
        public FakeBackend(string id, string origin, params string[] voices)
        {
            Id = id; OriginLabel = origin;
            Voices = voices.Select(v => new TtsVoiceOption(id, v, v)).ToList();
        }
        public string Id { get; }
        public string OriginLabel { get; }
        public List<TtsVoiceOption> Voices;
        public bool Throws;
        public int BlockFirst;   // the first N lines hold until stopped
        public int Resets;
        public readonly List<(string Text, string Voice, TtsSpeechParams P)> Spoken = new();
        public readonly ManualResetEventSlim Started = new(false);
        public volatile bool StoppedEarly;
        public readonly SemaphoreSlim Done = new(0);

        public IReadOnlyList<TtsVoiceOption> GetVoices() =>
            Throws ? throw new InvalidOperationException("engine missing") : Voices;

        public bool Speak(string text, string voice, TtsSpeechParams p, Func<bool> shouldStop)
        {
            lock (Spoken) Spoken.Add((text, voice, p));
            Started.Set();
            bool block; lock (Spoken) block = Spoken.Count <= BlockFirst;
            if (block)
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < until)
                {
                    if (shouldStop()) { StoppedEarly = true; break; }
                    Thread.Sleep(5);
                }
            }
            Done.Release();
            return true;
        }

        public void Reset() => Resets++;
        public void Dispose() { }
    }

    // ── ttsvoice grammar + back-compat ───────────────────────────────────────

    [Theory]
    [InlineData("", "piper", "")]
    [InlineData("vits-piper-en_US-amy-low", "piper", "vits-piper-en_US-amy-low")]
    [InlineData("my-hand-installed-voice", "piper", "my-hand-installed-voice")]
    [InlineData("piper:vits-piper-en_US-glados", "piper", "vits-piper-en_US-glados")]
    [InlineData("system:Microsoft Zira Desktop", "system", "Microsoft Zira Desktop")]
    [InlineData("SYSTEM: IVONA 2 Amy ", "system", "IVONA 2 Amy")]
    public void Ttsvoice_parses_bare_values_as_piper_and_prefixed_values_by_backend(string raw, string backend, string name)
    {
        var parsed = TtsVoiceSetting.Parse(raw);
        Assert.Equal(backend, parsed.Backend);
        Assert.Equal(name, parsed.Name);
    }

    [Fact]
    public void A_piper_voice_is_still_stored_bare_and_a_system_voice_carries_its_backend()
    {
        Assert.Equal("vits-piper-en_US-amy-low",
            new TtsVoiceOption("piper", "vits-piper-en_US-amy-low", "Amy").SettingValue);
        Assert.Equal("system:Microsoft Zira Desktop",
            new TtsVoiceOption("system", "Microsoft Zira Desktop", "Microsoft Zira Desktop").SettingValue);
    }

    [Fact]
    public void A_settings_cfg_written_before_system_voices_loads_unchanged_onto_piper()
    {
        Directory.CreateDirectory(_root);
        var cfgPath = Path.Combine(_root, "settings.cfg");
        File.WriteAllLines(cfgPath, new[] { "#config {ttsvoice} {vits-piper-en_US-lessac-medium}" });
        var cfg = NewConfig();
        Assert.True(cfg.Load(cfgPath));

        Assert.Equal("vits-piper-en_US-lessac-medium", cfg.TtsVoice);
        var piper = new FakeBackend("piper", "Piper");
        var system = new FakeBackend("system", "system", "vits-piper-en_US-lessac-medium");   // even a same-named system voice
        var r = TtsBackendSelector.Resolve(cfg.TtsVoice, piper, system);
        Assert.Same(piper, r.Backend);
        Assert.Equal("vits-piper-en_US-lessac-medium", r.Voice);
        Assert.False(r.FellBack);
    }

    [Fact]
    public void A_system_voice_round_trips_through_settings_cfg()
    {
        var cfg = NewConfig();
        cfg.SetSetting("ttsvoice", "system:Microsoft Zira Desktop");
        var cfgPath = Path.Combine(_root, "settings.cfg");
        Assert.True(cfg.Save(cfgPath));

        var back = NewConfig();
        Assert.True(back.Load(cfgPath));
        Assert.Equal("system:Microsoft Zira Desktop", back.TtsVoice);
    }

    private GenieConfig NewConfig()
    {
        var lds = new Genie.Core.Runtime.LocalDirectoryService("GenieTtsBackendTest", _root);
        lds.UseExplicitRoot(_root);
        return new GenieConfig(lds);
    }

    // ── Backend selection + fallback ─────────────────────────────────────────

    [Fact]
    public void A_listed_system_voice_selects_the_system_backend()
    {
        var piper = new FakeBackend("piper", "Piper", "vits-piper-en_US-amy-low");
        var system = new FakeBackend("system", "system", "Microsoft David Desktop", "Microsoft Zira Desktop");

        var r = TtsBackendSelector.Resolve("system:microsoft zira desktop", piper, system);

        Assert.Same(system, r.Backend);
        Assert.Equal("Microsoft Zira Desktop", r.Voice);   // the backend's own casing
        Assert.False(r.FellBack);
    }

    [Fact]
    public void A_missing_system_voice_falls_back_to_the_default_piper_voice()
    {
        var piper = new FakeBackend("piper", "Piper", "vits-piper-en_US-amy-low");
        var system = new FakeBackend("system", "system", "Microsoft David Desktop");

        var r = TtsBackendSelector.Resolve("system:IVONA 2 Amy", piper, system);

        Assert.Same(piper, r.Backend);
        Assert.Equal("", r.Voice);   // Piper's first installed voice
        Assert.True(r.FellBack);
    }

    [Fact]
    public void No_system_backend_or_a_failing_one_also_falls_back_to_piper()
    {
        var piper = new FakeBackend("piper", "Piper");
        Assert.True(TtsBackendSelector.Resolve("system:Zira", piper, null).FellBack);

        var broken = new FakeBackend("system", "system", "Zira") { Throws = true };
        var r = TtsBackendSelector.Resolve("system:Zira", piper, broken);
        Assert.Same(piper, r.Backend);
        Assert.True(r.FellBack);
    }

    [Fact]
    public void Service_speaks_through_the_selected_backend_with_live_rate_and_volume()
    {
        var piper = new FakeBackend("piper", "Piper", "amy");
        var system = new FakeBackend("system", "system", "Microsoft Zira Desktop");
        string selected = "system:Microsoft Zira Desktop";
        using var tts = new TtsService(piper, system, () => selected,
            rateProvider: () => 1.5f, volumeProvider: () => 0.4f);

        tts.Speak("You feel fully rested.");
        Assert.True(system.Done.Wait(2000));

        var said = Assert.Single(system.Spoken);
        Assert.Equal("You feel fully rested.", said.Text);
        Assert.Equal("Microsoft Zira Desktop", said.Voice);
        Assert.Equal(1.5f, said.P.Rate);
        Assert.Equal(0.4f, said.P.Volume);
        Assert.Empty(piper.Spoken);

        selected = "amy";   // #tts use back to Piper applies from the next line
        tts.Speak("Hello.");
        Assert.True(piper.Done.Wait(2000));
        Assert.Equal("amy", Assert.Single(piper.Spoken).Voice);
    }

    [Fact]
    public void Service_falls_back_to_piper_and_says_so_once()
    {
        var piper = new FakeBackend("piper", "Piper", "amy");
        var system = new FakeBackend("system", "system", "Microsoft David Desktop");
        var notes = new List<string>();
        using var tts = new TtsService(piper, system, () => "system:IVONA 2 Amy",
            notify: m => { lock (notes) notes.Add(m); });

        tts.Speak("one");
        Assert.True(piper.Done.Wait(2000));
        tts.Speak("two");
        Assert.True(piper.Done.Wait(2000));

        Assert.Equal(2, piper.Spoken.Count);
        Assert.Empty(system.Spoken);
        var note = Assert.Single(notes);
        Assert.Contains("IVONA 2 Amy", note);
        Assert.Contains("Piper", note);

        tts.Reset();   // a voice switch / install re-arms the notice
        tts.Speak("three");
        Assert.True(piper.Done.Wait(2000));
        Assert.Equal(2, notes.Count);
        Assert.True(piper.Resets >= 1 && system.Resets >= 1);
    }

    [Fact]
    public void Stop_interrupts_a_system_voice_mid_line()
    {
        var piper = new FakeBackend("piper", "Piper");
        var system = new FakeBackend("system", "system", "Zira") { BlockFirst = 1 };
        using var tts = new TtsService(piper, system, () => "system:Zira");

        tts.Speak("a very long room description");
        Assert.True(system.Started.Wait(2000));
        tts.Stop();

        Assert.True(system.Done.Wait(2000));
        Assert.True(system.StoppedEarly);
    }

    [Fact]
    public void A_higher_priority_line_barges_in_over_a_system_voice()
    {
        var piper = new FakeBackend("piper", "Piper");
        var system = new FakeBackend("system", "system", "Zira") { BlockFirst = 1 };
        using var tts = new TtsService(piper, system, () => "system:Zira");

        tts.Speak("the wind blows", TtsPriority.Low);
        Assert.True(system.Started.Wait(2000));
        tts.Speak("Someone whispers to you.", TtsPriority.High);

        Assert.True(system.Done.Wait(2000));
        Assert.True(system.StoppedEarly);
        Assert.True(system.Done.Wait(2000));
        Assert.Equal("Someone whispers to you.", system.Spoken.Last().Text);
    }

    // ── Merged, labelled voice list ──────────────────────────────────────────

    [Fact]
    public void Voice_list_merges_both_sources_piper_first_and_labels_the_origin()
    {
        var piper = new FakeBackend("piper", "Piper") { Voices = { new("piper", "vits-piper-en_US-amy-low", "Amy") } };
        var system = new FakeBackend("system", "system", "Microsoft Zira Desktop", "IVONA 2 Amy", "Microsoft David Desktop");
        var backends = new ITextToSpeech[] { system, piper };   // order given doesn't matter

        var merged = TtsVoiceList.Merge(backends);
        var labels = merged.Select(v => TtsVoiceList.Label(v, backends)).ToList();

        Assert.Equal(new[]
        {
            "Amy (Piper)",
            "IVONA 2 Amy (system)",
            "Microsoft David Desktop (system)",
            "Microsoft Zira Desktop (system)",
        }, labels);
        Assert.Equal("vits-piper-en_US-amy-low", merged[0].SettingValue);
        Assert.Equal("system:IVONA 2 Amy", merged[1].SettingValue);
    }

    [Fact]
    public void A_backend_that_fails_to_list_drops_out_without_hiding_the_other()
    {
        var piper = new FakeBackend("piper", "Piper", "amy");
        var system = new FakeBackend("system", "system", "Zira") { Throws = true };
        var merged = TtsVoiceList.Merge(new ITextToSpeech[] { piper, system });
        Assert.Equal("amy", Assert.Single(merged).Name);
    }

    [Fact]
    public void Service_lists_and_labels_voices_from_every_backend()
    {
        var piper = new FakeBackend("piper", "Piper", "amy");
        var system = new FakeBackend("system", "system", "Zira");
        using var tts = new TtsService(piper, system);
        Assert.Equal(new[] { "amy (Piper)", "Zira (system)" }, tts.ListVoices().Select(tts.Label));
    }

    [Theory]
    [InlineData("Microsoft Zira Desktop", "Microsoft Zira Desktop")]
    [InlineData("zira", "Microsoft Zira Desktop")]
    [InlineData("system:ivona", "IVONA 2 Amy")]
    [InlineData("microsoft", null)]   // ambiguous: David and Zira
    [InlineData("nobody", null)]
    public void Tts_use_matches_a_system_voice_by_name_or_a_unique_part(string typed, string? expected)
    {
        var voices = new FakeBackend("system", "system", "Microsoft David Desktop", "Microsoft Zira Desktop", "IVONA 2 Amy").Voices;
        Assert.Equal(expected, TtsVoiceList.Find(voices, typed)?.Name);
    }

    [Fact]
    public void Piper_voice_folder_lists_catalog_voices_by_short_name_then_raw_folders()
    {
        var dir = Path.Combine(_root, "Voices");
        MakePiperVoice(Path.Combine(dir, VoiceCatalog.Default.Id));
        MakePiperVoice(Path.Combine(dir, "my-own-voice"));
        Directory.CreateDirectory(Path.Combine(dir, "not-a-voice"));

        var voices = PiperTextToSpeech.ListVoices(dir);

        Assert.Equal(new[] { "Amy", "my-own-voice" }, voices.Select(v => v.Display));
        Assert.All(voices, v => Assert.Equal("piper", v.Backend));
        Assert.Empty(PiperTextToSpeech.ListVoices(Path.Combine(_root, "missing")));
    }

    private static void MakePiperVoice(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "espeak-ng-data"));
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "");
        File.WriteAllText(Path.Combine(dir, "tokens.txt"), "");
    }

    // ── Per-OS voice-list parsers + rate/volume mapping ──────────────────────

    [Fact]
    public void Say_voice_list_parses_names_with_spaces_and_parentheses()
    {
        const string output =
            "Albert              en_US    # Hello! My name is Albert.\n" +
            "Bad News            en_US    # Hello! My name is Bad News.\n" +
            "Eddy (English (UK)) en_GB    # Hello! My name is Eddy.\n" +
            "Amélie              fr_CA    # Bonjour, je m’appelle Amélie.\n" +
            "\n";
        Assert.Equal(new[] { "Albert", "Bad News", "Eddy (English (UK))", "Amélie" },
            SystemTextToSpeech.ParseSayVoices(output).Select(v => v.Name));
    }

    [Fact]
    public void Spd_say_voice_list_reads_rows_from_the_right_and_skips_the_header()
    {
        const string output =
            "            NAME     LANGUAGE     VARIANT\n" +
            "       Afrikaans           af        none\n" +
            "English (America)        en-US        none\n";
        var voices = SystemTextToSpeech.ParseSpdVoices(output);
        Assert.Equal(new[] { "Afrikaans", "English (America)" }, voices.Select(v => v.Name));
        Assert.Equal("English (America) [en-US]", voices[1].Display);
    }

    [Fact]
    public void Espeak_voice_list_uses_the_language_code_as_the_voice_id()
    {
        const string output =
            "Pty Language       Age/Gender VoiceName          File                 Other Languages\n" +
            " 5  af              --/M      Afrikaans          gmw/af\n" +
            " 2  en-us           --/M      English_(America)  gmw/en-US            (en 3)\n";
        var voices = SystemTextToSpeech.ParseEspeakVoices(output);
        Assert.Equal(new[] { "af", "en-us" }, voices.Select(v => v.Name));
        Assert.Equal("English (America) [en-us]", voices[1].Display);
    }

    [Fact]
    public void Garbage_or_empty_tool_output_lists_no_voices()
    {
        Assert.Empty(SystemTextToSpeech.ParseSayVoices(""));
        Assert.Empty(SystemTextToSpeech.ParseSpdVoices("Failed to connect to Speech Dispatcher"));
        Assert.Empty(SystemTextToSpeech.ParseEspeakVoices("espeak-ng: command not found"));
    }

    [Theory]
    [InlineData(1.0f, 0, 175, 0)]
    [InlineData(3.0f, 10, 525, 100)]
    [InlineData(0.5f, -6, 88, -63)]
    public void Rate_multiplier_maps_onto_each_engine(float rate, int sapi, int wpm, int spd)
    {
        Assert.Equal(sapi, SystemTextToSpeech.SapiRate(rate));
        Assert.Equal(wpm, SystemTextToSpeech.Wpm(rate));
        Assert.Equal(spd, SystemTextToSpeech.SpdRate(rate));
    }

    [Theory]
    [InlineData(1.0f, 100, 100, 100)]
    [InlineData(0.5f, 50, 0, 50)]
    [InlineData(0.0f, 0, -100, 0)]
    public void Volume_maps_onto_each_engine(float gain, int sapi, int spd, int espeak)
    {
        Assert.Equal(sapi, SystemTextToSpeech.SapiVolume(gain));
        Assert.Equal(spd, SystemTextToSpeech.SpdVolume(gain));
        Assert.Equal(espeak, SystemTextToSpeech.EspeakAmplitude(gain));
    }

    [Fact]
    public void Say_embedded_commands_in_game_text_are_spoken_not_obeyed()
    {
        Assert.Equal("a [ [rate 900]] b", SystemTextToSpeech.DefangSayCommands("a [[rate 900]] b"));
    }

    [Fact]
    public void This_os_system_backend_enumerates_without_throwing()
    {
        // Real SAPI / say / spd-say can't be driven in CI; only the listing is
        // exercised, and an absent engine must mean "no voices", not a throw.
        using var sys = SystemTextToSpeech.Create();
        if (sys is null) return;
        Assert.Equal("system", sys.Id);
        Assert.All(sys.GetVoices(), v => Assert.Equal("system", v.Backend));
    }
}
