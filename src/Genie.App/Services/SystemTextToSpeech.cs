using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using Genie.App.Diagnostics;

namespace Genie.App.Services;

/// <summary>
/// The OS's own installed voices (public #368) — the voices a screen-reader user
/// has already chosen and tuned, which Piper model folders can never surface.
///
/// <list type="bullet">
///   <item><b>Windows</b> — SAPI5 via <c>System.Speech</c>: Microsoft David /
///         Zira and third-party SAPI5 voices such as Ivona 2.</item>
///   <item><b>macOS</b> — the <c>say</c> CLI (<c>say -v '?'</c> lists voices).</item>
///   <item><b>Linux</b> — speech-dispatcher's <c>spd-say</c> (<c>spd-say -L</c>),
///         else <c>espeak-ng</c>.</item>
/// </list>
///
/// Every backend degrades to "no voices" when its engine is absent. The CLI
/// backends always pass game text through <see cref="ProcessStartInfo.ArgumentList"/>
/// or stdin, never a shell string.
/// </summary>
public static class SystemTextToSpeech
{
    /// <summary>This OS's system-voice backend, or null on an OS with none.</summary>
    public static ITextToSpeech? Create()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new SapiTextToSpeech();
            if (OperatingSystem.IsMacOS())   return new MacSayTextToSpeech();
            if (OperatingSystem.IsLinux())   return new LinuxTextToSpeech();
        }
        catch (Exception ex) { ErrorLog.Log("SystemTextToSpeech.Create", ex); }
        return null;
    }

    // ── Rate / volume mapping (ttsrate is a 0.5–3 multiplier, 1 = natural) ──

    /// <summary>SAPI <c>Rate</c> is -10..10 where ±10 is roughly 3× faster /
    /// slower, so the multiplier maps on a log-3 scale: 1 → 0, 3 → 10, 0.5 → -6.</summary>
    public static int SapiRate(float rate) =>
        Math.Clamp((int)Math.Round(10 * Math.Log(Math.Max(rate, 0.01f)) / Math.Log(3)), -10, 10);

    /// <summary>SAPI <c>Volume</c> is 0..100 — the same scale as ttsvolume.</summary>
    public static int SapiVolume(float gain) => Math.Clamp((int)Math.Round(gain * 100), 0, 100);

    /// <summary>Words per minute for <c>say -r</c> / <c>espeak-ng -s</c>, both of
    /// which default to about 175 wpm.</summary>
    public static int Wpm(float rate) => Math.Clamp((int)Math.Round(175 * rate), 80, 600);

    /// <summary>speech-dispatcher <c>-r</c> is -100..100, same log-3 shape as SAPI.</summary>
    public static int SpdRate(float rate) =>
        Math.Clamp((int)Math.Round(100 * Math.Log(Math.Max(rate, 0.01f)) / Math.Log(3)), -100, 100);

    /// <summary>speech-dispatcher <c>-i</c> is -100..100; 100% maps to its
    /// loudest, 0% to its quietest (it has no true mute — 0% is skipped instead).</summary>
    public static int SpdVolume(float gain) => Math.Clamp((int)Math.Round(gain * 200 - 100), -100, 100);

    /// <summary>espeak-ng <c>-a</c> amplitude, 0..200 with 100 the default.</summary>
    public static int EspeakAmplitude(float gain) => Math.Clamp((int)Math.Round(gain * 100), 0, 200);

    // ── Voice-list parsers (pure, unit-tested against captured output) ──────

    private static readonly Regex SayLine =
        new(@"^(?<name>\S.*?)\s+(?<lang>[a-z]{2,3}(?:[_-][A-Za-z0-9]+)*)\s+#", RegexOptions.Compiled);

    /// <summary>Parse <c>say -v '?'</c>: <c>Name   en_US    # sample</c>. Names
    /// may contain spaces and parentheses ("Eddy (English (UK))").</summary>
    public static IReadOnlyList<TtsVoiceOption> ParseSayVoices(string output)
    {
        var list = new List<TtsVoiceOption>();
        foreach (var line in Lines(output))
        {
            var m = SayLine.Match(line);
            if (!m.Success) continue;
            string name = m.Groups["name"].Value.Trim();
            list.Add(new TtsVoiceOption(TtsVoiceSetting.SystemBackend, name, name));
        }
        return Distinct(list);
    }

    /// <summary>Parse <c>spd-say -L</c>: a <c>NAME LANGUAGE VARIANT</c> header
    /// then right-aligned rows. The name may contain spaces; language and variant
    /// never do, so the row is read from the right.</summary>
    public static IReadOnlyList<TtsVoiceOption> ParseSpdVoices(string output)
    {
        var list = new List<TtsVoiceOption>();
        bool header = false;   // rows only count after the header, so an error message isn't a voice
        foreach (var line in Lines(output))
        {
            var tok = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 3) continue;
            if (tok[0] == "NAME" && tok[^2] == "LANGUAGE") { header = true; continue; }
            if (!header) continue;
            string name = string.Join(' ', tok[..^2]);
            string lang = tok[^2];
            list.Add(new TtsVoiceOption(TtsVoiceSetting.SystemBackend, name, $"{name} [{lang}]"));
        }
        return Distinct(list);
    }

    /// <summary>Parse <c>espeak-ng --voices</c>:
    /// <c>Pty Language Age/Gender VoiceName File Other…</c>. The language code is
    /// what <c>-v</c> takes; the voice name (underscores for spaces) is shown.</summary>
    public static IReadOnlyList<TtsVoiceOption> ParseEspeakVoices(string output)
    {
        var list = new List<TtsVoiceOption>();
        foreach (var line in Lines(output))
        {
            var tok = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 4 || tok[0] == "Pty" || !int.TryParse(tok[0], out _)) continue;
            string lang = tok[1];
            string display = tok[3].Replace('_', ' ');
            list.Add(new TtsVoiceOption(TtsVoiceSetting.SystemBackend, lang, $"{display} [{lang}]"));
        }
        return Distinct(list);
    }

    /// <summary>Game text with <c>say</c>'s embedded-command opener defanged, so a
    /// line containing <c>[[…]]</c> is spoken rather than obeyed.</summary>
    public static string DefangSayCommands(string text) => text.Replace("[[", "[ [");

    private static IEnumerable<string> Lines(string s) =>
        (s ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0);

    private static IReadOnlyList<TtsVoiceOption> Distinct(List<TtsVoiceOption> list) =>
        list.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
}

/// <summary>Windows SAPI5 voices via <c>System.Speech.Synthesis</c>. Lists the
/// voices registered for desktop SAPI (David, Zira, SAPI5 third-party voices);
/// the OneCore-only voices Windows 10/11 add for Narrator are not SAPI5 and
/// don't appear.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SapiTextToSpeech : ITextToSpeech
{
    private readonly System.Threading.Lock _gate = new();
    private IReadOnlyList<TtsVoiceOption>? _voices;
    private System.Speech.Synthesis.SpeechSynthesizer? _synth;   // worker-thread use only

    public string Id => TtsVoiceSetting.SystemBackend;
    public string OriginLabel => "system";

    public IReadOnlyList<TtsVoiceOption> GetVoices()
    {
        lock (_gate)
        {
            if (_voices is not null) return _voices;
            try
            {
                using var s = new System.Speech.Synthesis.SpeechSynthesizer();
                _voices = s.GetInstalledVoices()
                    .Where(v => v.Enabled)
                    .Select(v => new TtsVoiceOption(TtsVoiceSetting.SystemBackend, v.VoiceInfo.Name, v.VoiceInfo.Name))
                    .ToList();
            }
            catch (Exception ex)
            {
                ErrorLog.Log("SapiTextToSpeech.GetVoices", ex);   // no speech subsystem (e.g. Server Core)
                _voices = Array.Empty<TtsVoiceOption>();
            }
            return _voices;
        }
    }

    public bool Speak(string text, string voice, TtsSpeechParams p, Func<bool> shouldStop)
    {
        int volume = SystemTextToSpeech.SapiVolume(p.Volume);
        if (volume == 0) return true;   // 0% = silent, as Piper skips playback

        _synth ??= CreateSynth();
        var synth = _synth;
        if (!string.IsNullOrEmpty(voice)) synth.SelectVoice(voice);
        synth.Rate = SystemTextToSpeech.SapiRate(p.Rate);
        synth.Volume = volume;

        // Plain-text SpeakAsync (not SSML), so game text is never parsed as markup.
        var prompt = synth.SpeakAsync(text);
        while (!prompt.IsCompleted)
        {
            if (shouldStop())
            {
                synth.SpeakAsyncCancelAll();
                for (int i = 0; i < 50 && !prompt.IsCompleted; i++) Thread.Sleep(10);
                break;
            }
            Thread.Sleep(15);
        }
        return true;
    }

    private static System.Speech.Synthesis.SpeechSynthesizer CreateSynth()
    {
        var s = new System.Speech.Synthesis.SpeechSynthesizer();
        s.SetOutputToDefaultAudioDevice();
        return s;
    }

    public void Reset()
    {
        lock (_gate) _voices = null;
    }

    public void Dispose()
    {
        try { _synth?.SpeakAsyncCancelAll(); _synth?.Dispose(); } catch { /* best-effort */ }
        _synth = null;
    }
}

/// <summary>Shared plumbing for the CLI backends: find a tool on PATH, capture
/// a listing, and run one utterance that is killed on interrupt.</summary>
internal abstract class ProcessTextToSpeech : ITextToSpeech
{
    private readonly System.Threading.Lock _gate = new();
    private IReadOnlyList<TtsVoiceOption>? _voices;

    public string Id => TtsVoiceSetting.SystemBackend;
    public string OriginLabel => "system";

    protected abstract IReadOnlyList<TtsVoiceOption> ListVoicesUncached();

    public IReadOnlyList<TtsVoiceOption> GetVoices()
    {
        lock (_gate)
        {
            if (_voices is not null) return _voices;
            try { _voices = ListVoicesUncached(); }
            catch (Exception ex)
            {
                ErrorLog.Log(GetType().Name + ".GetVoices", ex);
                _voices = Array.Empty<TtsVoiceOption>();
            }
            return _voices;
        }
    }

    public abstract bool Speak(string text, string voice, TtsSpeechParams p, Func<bool> shouldStop);

    public void Reset()
    {
        lock (_gate) _voices = null;
    }

    public virtual void Dispose() { }

    /// <summary>Full path of <paramref name="exe"/> on PATH, or null.</summary>
    protected static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>Run <paramref name="exe"/> and return its stdout, or "" when it
    /// can't start or doesn't finish within <paramref name="timeoutMs"/>.</summary>
    protected static string Capture(string exe, IEnumerable<string> args, int timeoutMs = 3000)
    {
        var psi = NewStartInfo(exe, args);
        psi.RedirectStandardOutput = true;
        using var proc = Process.Start(psi);
        if (proc is null) return "";
        var read = proc.StandardOutput.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return "";
        }
        return read.Wait(500) ? read.Result : "";
    }

    /// <summary>Speak one line: start the process (text on stdin when
    /// <paramref name="stdin"/> is set), then wait, killing it the moment
    /// <paramref name="shouldStop"/> turns true.</summary>
    protected static bool Run(string exe, IEnumerable<string> args, string? stdin, Func<bool> shouldStop)
    {
        var psi = NewStartInfo(exe, args);
        psi.RedirectStandardInput = stdin is not null;
        using var proc = Process.Start(psi);
        if (proc is null) return false;
        if (stdin is not null)
        {
            try { proc.StandardInput.Write(stdin); proc.StandardInput.Close(); }
            catch { /* the tool exited early — nothing to speak */ }
        }
        while (!proc.WaitForExit(20))
        {
            if (shouldStop())
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
                break;
            }
        }
        return true;
    }

    private static ProcessStartInfo NewStartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,   // keep tool chatter out of the console
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}

/// <summary>macOS: the built-in <c>say</c> CLI.</summary>
internal sealed class MacSayTextToSpeech : ProcessTextToSpeech
{
    private readonly string? _say = FindOnPath("say") ?? (File.Exists("/usr/bin/say") ? "/usr/bin/say" : null);

    protected override IReadOnlyList<TtsVoiceOption> ListVoicesUncached() =>
        _say is null ? Array.Empty<TtsVoiceOption>()
                     : SystemTextToSpeech.ParseSayVoices(Capture(_say, new[] { "-v", "?" }));

    public override bool Speak(string text, string voice, TtsSpeechParams p, Func<bool> shouldStop)
    {
        if (_say is null) return false;
        if (p.Volume <= 0f) return true;
        var args = new List<string>();
        if (!string.IsNullOrEmpty(voice)) { args.Add("-v"); args.Add(voice); }
        args.Add("-r"); args.Add(SystemTextToSpeech.Wpm(p.Rate).ToString(System.Globalization.CultureInfo.InvariantCulture));

        // say has no volume flag; its [[volm]] embedded command sets it. The text
        // goes on stdin (no message argument = read stdin), so a line that starts
        // with "-" can't be taken for an option; game text's own [[ is defanged.
        string body = SystemTextToSpeech.DefangSayCommands(text);
        if (p.Volume < 1f)
            body = $"[[volm {p.Volume.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}]] {body}";
        return Run(_say, args, body, shouldStop);
    }
}

/// <summary>Linux: speech-dispatcher's <c>spd-say</c>, else <c>espeak-ng</c>.
/// Whichever tool supplied the voice list speaks, so a listed name is always
/// one that tool's voice flag accepts.</summary>
internal sealed class LinuxTextToSpeech : ProcessTextToSpeech
{
    private readonly string? _spd = FindOnPath("spd-say");
    private readonly string? _espeak = FindOnPath("espeak-ng");
    private volatile bool _voicesFromEspeak;

    protected override IReadOnlyList<TtsVoiceOption> ListVoicesUncached()
    {
        _voicesFromEspeak = false;
        if (_spd is not null)
        {
            var spd = SystemTextToSpeech.ParseSpdVoices(Capture(_spd, new[] { "-L" }));
            if (spd.Count > 0) return spd;
        }
        if (_espeak is not null)
        {
            var es = SystemTextToSpeech.ParseEspeakVoices(Capture(_espeak, new[] { "--voices" }));
            _voicesFromEspeak = es.Count > 0;
            return es;
        }
        return Array.Empty<TtsVoiceOption>();
    }

    public override bool Speak(string text, string voice, TtsSpeechParams p, Func<bool> shouldStop)
    {
        if (p.Volume <= 0f) return true;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        GetVoices();   // settles which tool owns the voice names

        if (_spd is not null && !_voicesFromEspeak)
        {
            // -w waits until the line is spoken, so the process lifetime is the
            // utterance and killing it is the interrupt. "--" ends option parsing
            // so game text beginning with "-" is spoken, not parsed.
            var args = new List<string> { "-w",
                "-r", SystemTextToSpeech.SpdRate(p.Rate).ToString(inv),
                "-i", SystemTextToSpeech.SpdVolume(p.Volume).ToString(inv) };
            if (!string.IsNullOrEmpty(voice)) { args.Add("-y"); args.Add(voice); }
            args.Add("--");
            args.Add(text);
            return Run(_spd, args, null, shouldStop);
        }
        if (_espeak is not null)
        {
            var args = new List<string> {
                "-s", SystemTextToSpeech.Wpm(p.Rate).ToString(inv),
                "-a", SystemTextToSpeech.EspeakAmplitude(p.Volume).ToString(inv) };
            if (!string.IsNullOrEmpty(voice)) { args.Add("-v"); args.Add(voice); }
            args.Add("--stdin");   // text on stdin, never on the command line
            return Run(_espeak, args, text, shouldStop);
        }
        return false;
    }
}
