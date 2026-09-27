using System;
using System.Collections.Generic;
using System.Linq;

namespace Genie.App.Services;

/// <summary>
/// One speech engine behind <see cref="TtsService"/> (public #368). The service
/// owns the queue, priorities and barge-in; a backend only lists its voices and
/// speaks one line at a time on the TTS worker thread.
///
/// <para>Two backends exist: <see cref="PiperTextToSpeech"/> (the bundled offline
/// neural voices, the default and the fallback) and a per-OS system backend
/// (<see cref="SystemTextToSpeech.Create"/>: SAPI5 on Windows, <c>say</c> on
/// macOS, <c>spd-say</c> / <c>espeak-ng</c> on Linux).</para>
/// </summary>
public interface ITextToSpeech : IDisposable
{
    /// <summary>Backend id written into <c>ttsvoice</c> —
    /// <see cref="TtsVoiceSetting.PiperBackend"/> or
    /// <see cref="TtsVoiceSetting.SystemBackend"/>.</summary>
    string Id { get; }

    /// <summary>Origin shown after a voice's name in the voice list:
    /// "Piper" or "system".</summary>
    string OriginLabel { get; }

    /// <summary>The voices this backend can speak with right now. Empty (never
    /// throws) when the engine isn't present on this machine.</summary>
    IReadOnlyList<TtsVoiceOption> GetVoices();

    /// <summary>Speak <paramref name="text"/> with <paramref name="voice"/> (a
    /// <see cref="TtsVoiceOption.Name"/>; empty = the backend's default), blocking
    /// until it finishes or <paramref name="shouldStop"/> turns true — then stop
    /// the audio as soon as the engine allows. Returns false when nothing could be
    /// spoken (the backend reports why through the service's notifier).</summary>
    bool Speak(string text, string voice, TtsSpeechParams p, Func<bool> shouldStop);

    /// <summary>Drop any cached engine or voice list so the next call re-scans
    /// (after a voice install, a folder change or a voice switch).</summary>
    void Reset();
}

/// <summary>Per-utterance speech settings, read live from config.</summary>
/// <param name="Rate">Speed multiplier, 1 = natural (ttsrate, 0.5–3).</param>
/// <param name="Volume">Linear gain 0..1 (ttsvolume / 100).</param>
public readonly record struct TtsSpeechParams(float Rate, float Volume);

/// <summary>One entry in the merged voice list.</summary>
/// <param name="Backend">Owning backend id.</param>
/// <param name="Name">The backend's own voice id (Piper folder, SAPI voice name,
/// <c>say -v</c> name, …).</param>
/// <param name="Display">Human-facing name, without the origin suffix.</param>
public sealed record TtsVoiceOption(string Backend, string Name, string Display)
{
    /// <summary>What <c>ttsvoice</c> stores for this voice — a bare folder name
    /// for Piper (unchanged from before system voices existed), else
    /// <c>system:&lt;name&gt;</c>.</summary>
    public string SettingValue => TtsVoiceSetting.Format(Backend, Name);
}

/// <summary>
/// The <c>ttsvoice</c> setting's grammar. A bare value is a Piper voice folder,
/// exactly as every settings.cfg written before public #368 holds it, so those
/// load unchanged with no migration. A system voice is <c>system:&lt;name&gt;</c>;
/// <c>piper:&lt;folder&gt;</c> is accepted too.
/// </summary>
public static class TtsVoiceSetting
{
    public const string PiperBackend  = "piper";
    public const string SystemBackend = "system";

    public static (string Backend, string Name) Parse(string? raw)
    {
        var s = (raw ?? "").Trim();
        foreach (var backend in new[] { SystemBackend, PiperBackend })
            if (s.StartsWith(backend + ":", StringComparison.OrdinalIgnoreCase))
                return (backend, s[(backend.Length + 1)..].Trim());
        return (PiperBackend, s);
    }

    public static string Format(string backend, string name) =>
        string.Equals(backend, PiperBackend, StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{backend}:{name}";
}

/// <summary>Merge and label the per-backend voice lists for the UI and
/// <c>#tts voices</c>, and resolve the saved selection to a backend.</summary>
public static class TtsVoiceList
{
    /// <summary>"Amy (Piper)", "Microsoft Zira Desktop (system)".</summary>
    public static string Label(TtsVoiceOption v, IEnumerable<ITextToSpeech> backends)
    {
        var origin = backends.FirstOrDefault(b => string.Equals(b.Id, v.Backend, StringComparison.OrdinalIgnoreCase))
                         ?.OriginLabel ?? v.Backend;
        return $"{v.Display} ({origin})";
    }

    /// <summary>Every backend's voices in one list — Piper first (it is the
    /// default), then system voices by name. A backend that throws contributes
    /// nothing rather than hiding the others.</summary>
    public static IReadOnlyList<TtsVoiceOption> Merge(IEnumerable<ITextToSpeech> backends)
    {
        var all = new List<TtsVoiceOption>();
        foreach (var b in backends.OrderBy(b => b.Id == TtsVoiceSetting.PiperBackend ? 0 : 1))
        {
            IReadOnlyList<TtsVoiceOption> voices;
            try { voices = b.GetVoices(); }
            catch { continue; }
            var ordered = b.Id == TtsVoiceSetting.PiperBackend
                ? voices                                   // catalog order, then folders
                : voices.OrderBy(v => v.Display, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var v in ordered)
                if (!all.Any(a => string.Equals(a.SettingValue, v.SettingValue, StringComparison.OrdinalIgnoreCase)))
                    all.Add(v);
        }
        return all;
    }

    /// <summary>Find a voice by what a user typed for <c>#tts use</c>: its saved
    /// value, its exact name, then a unique name prefix / substring match
    /// ("zira" → "Microsoft Zira Desktop"). Null when nothing (or more than one
    /// fuzzy candidate) matches.</summary>
    public static TtsVoiceOption? Find(IReadOnlyList<TtsVoiceOption> voices, string typed)
    {
        var t = (typed ?? "").Trim();
        if (t.Length == 0) return null;
        var (backend, name) = TtsVoiceSetting.Parse(t);
        bool explicitBackend = t.StartsWith(backend + ":", StringComparison.OrdinalIgnoreCase);
        var pool = explicitBackend
            ? voices.Where(v => string.Equals(v.Backend, backend, StringComparison.OrdinalIgnoreCase)).ToList()
            : voices.ToList();

        var exact = pool.FirstOrDefault(v =>
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v.Display, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var fuzzy = pool.Where(v =>
            v.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            v.Display.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return fuzzy.Count == 1 ? fuzzy[0] : null;
    }
}

/// <summary>What the worker should speak with for the saved selection.</summary>
/// <param name="Backend">The backend to use.</param>
/// <param name="Voice">Voice name for that backend ("" = its default).</param>
/// <param name="FellBack">True when a saved system voice was unavailable and
/// Piper stands in.</param>
public readonly record struct TtsResolution(ITextToSpeech Backend, string Voice, bool FellBack);

public static class TtsBackendSelector
{
    /// <summary>Resolve <c>ttsvoice</c> to a backend. A Piper value (bare or
    /// <c>piper:</c>) goes to Piper exactly as before. A <c>system:</c> value
    /// goes to the system backend when that voice is listed; otherwise Piper
    /// speaks with its default voice and <see cref="TtsResolution.FellBack"/> is
    /// set so the caller can say so once.</summary>
    public static TtsResolution Resolve(string? saved, ITextToSpeech piper, ITextToSpeech? system)
    {
        var (backend, name) = TtsVoiceSetting.Parse(saved);
        if (backend == TtsVoiceSetting.PiperBackend)
            return new TtsResolution(piper, name, false);

        if (system is not null && name.Length > 0)
        {
            try
            {
                var match = system.GetVoices().FirstOrDefault(v =>
                    string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    return new TtsResolution(system, match.Name, false);
            }
            catch { /* treat a failing enumerator as "voice gone" */ }
        }
        return new TtsResolution(piper, "", true);
    }
}
