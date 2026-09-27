using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.App.Diagnostics;
using SherpaOnnx;

namespace Genie.App.Services;

/// <summary>
/// The bundled offline neural backend: Piper VITS models via sherpa-onnx,
/// played through <see cref="TtsPlayer"/>. Genie's default and the fallback
/// when a saved system voice is gone (public #368).
///
/// <para>The engine is created lazily and rebuilt when the voice folder
/// (<c>#config ttsvoicedir</c>) or the selected voice changes; a folder with no
/// usable voice latches a failure (reported once) until either changes or
/// <see cref="Reset"/> runs.</para>
/// </summary>
public sealed class PiperTextToSpeech : ITextToSpeech
{
    private readonly Func<string> _voiceDirProvider;
    private readonly Action<string>? _notify;
    private readonly TtsPlayer _player = new();

    private readonly System.Threading.Lock _engineGate = new();
    private OfflineTts? _engine;
    private string? _attemptedKey;   // dir|selected the engine / failure latch is for
    private bool _initFailed;

    public PiperTextToSpeech(Func<string> voiceDirProvider, Action<string>? notify = null)
    {
        _voiceDirProvider = voiceDirProvider;
        _notify = notify;
    }

    public string Id => TtsVoiceSetting.PiperBackend;
    public string OriginLabel => "Piper";

    public IReadOnlyList<TtsVoiceOption> GetVoices() => ListVoices(_voiceDirProvider() ?? "");

    /// <summary>The installed Piper voices in <paramref name="voiceDir"/>: catalog
    /// voices first (in catalog order), then hand-installed folders — the same
    /// set <c>#tts use</c> accepts.</summary>
    public static IReadOnlyList<TtsVoiceOption> ListVoices(string voiceDir)
    {
        var items = new List<TtsVoiceOption>();
        try
        {
            foreach (var v in VoiceCatalog.All)
                if (VoiceInstaller.IsInstalled(Path.Combine(voiceDir, v.Id)))
                    items.Add(new TtsVoiceOption(TtsVoiceSetting.PiperBackend, v.Id, ShortName(v.DisplayName)));

            if (Directory.Exists(voiceDir))
                foreach (var sub in Directory.GetDirectories(voiceDir))
                {
                    string name = Path.GetFileName(sub);
                    if (VoiceInstaller.IsInstalled(sub) &&
                        !items.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
                        items.Add(new TtsVoiceOption(TtsVoiceSetting.PiperBackend, name, name));
                }
        }
        catch { /* unreadable voice dir — whatever was found still stands */ }
        return items;
    }

    /// <summary>"Amy (US, low)" → "Amy", so the list reads "Amy (Piper)".</summary>
    private static string ShortName(string display)
    {
        int i = display.IndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? display[..i] : display;
    }

    public bool Speak(string text, string voice, TtsSpeechParams p, Func<bool> shouldStop)
    {
        var engine = EnsureEngine(voice ?? "");
        if (engine is null) return false;

        var gen = new OfflineTtsGenerationConfig { Sid = 0, Speed = p.Rate > 0 ? p.Rate : 1.0f };
        var audio = engine.GenerateWithConfig(text, gen, null);

        float gain = Math.Clamp(p.Volume, 0f, 1f);
        if (gain > 0f)
        {
            if (gain < 1f)
            {
                var s = audio.Samples;
                for (int i = 0; i < s.Length; i++) s[i] *= gain;
            }
            _player.Play(audio.Samples, audio.SampleRate, shouldStop);
        }
        return true;
    }

    public void Reset()
    {
        lock (_engineGate)
        {
            _engine?.Dispose();
            _engine = null;
            _initFailed = false;
            _attemptedKey = null;
        }
    }

    /// <summary>Build the engine for the current voice dir + selection. Rebuilds
    /// when either changes; latches per-config when no usable voice exists.</summary>
    private OfflineTts? EnsureEngine(string selected)
    {
        lock (_engineGate)
        {
            string dir = _voiceDirProvider() ?? "";
            string key = dir + "|" + selected;

            if (!string.Equals(key, _attemptedKey, StringComparison.OrdinalIgnoreCase))
            {
                _engine?.Dispose();
                _engine = null;
                _initFailed = false;
                _attemptedKey = key;
            }

            if (_engine is not null) return _engine;
            if (_initFailed) return null;

            try
            {
                if (!Directory.Exists(dir) || LocateVoice(dir, selected) is not { } voice)
                {
                    _initFailed = true;
                    _notify?.Invoke(
                        $"[tts] no voice installed in '{dir}'. Run #tts install to download " +
                        "one, or #config ttsvoicedir <path> to point at an existing voice folder.");
                    return null;
                }

                var config = new OfflineTtsConfig();
                config.Model.Vits.Model = voice.Onnx;
                config.Model.Vits.Tokens = voice.Tokens;
                config.Model.Vits.DataDir = voice.DataDir;
                config.Model.NumThreads = 1;
                config.Model.Debug = 0;   // int, not bool, in this API

                _engine = new OfflineTts(config);
                _notify?.Invoke($"[tts] voice loaded: {Path.GetFileName(voice.Onnx)}");
                return _engine;
            }
            catch (Exception ex)
            {
                _initFailed = true;
                ErrorLog.Log("PiperTextToSpeech.EnsureEngine", ex);
                _notify?.Invoke($"[tts] failed to load voice: {ex.Message}");
                return null;
            }
        }
    }

    private readonly record struct Voice(string Onnx, string Tokens, string DataDir);

    /// <summary>Find the voice to load: <paramref name="selected"/> if it names an
    /// installed folder, else the first installed voice (the dir itself or its
    /// first valid subfolder).</summary>
    private static Voice? LocateVoice(string dir, string selected)
    {
        if (!string.IsNullOrWhiteSpace(selected) &&
            TryVoice(Path.Combine(dir, selected)) is { } chosen)
            return chosen;

        foreach (var candidate in Prepend(dir, Directory.EnumerateDirectories(dir)))
            if (TryVoice(candidate) is { } v)
                return v;
        return null;
    }

    private static Voice? TryVoice(string candidate)
    {
        if (!Directory.Exists(candidate)) return null;
        string? onnx = Directory.EnumerateFiles(candidate, "*.onnx").FirstOrDefault();
        string tokens = Path.Combine(candidate, "tokens.txt");
        string data = Path.Combine(candidate, "espeak-ng-data");
        if (onnx is not null && File.Exists(tokens) && Directory.Exists(data))
            return new Voice(onnx, tokens, data);
        return null;
    }

    private static IEnumerable<string> Prepend(string first, IEnumerable<string> rest)
    {
        yield return first;
        foreach (var r in rest) yield return r;
    }

    public void Dispose()
    {
        _player.Dispose();
        lock (_engineGate) { _engine?.Dispose(); _engine = null; }
    }
}
