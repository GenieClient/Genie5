using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Genie.App.Diagnostics;
using SherpaOnnx;

namespace Genie.App.Services;

/// <summary>Relative urgency of a spoken line. Higher interrupts lower in flight
/// and is spoken first from the queue. <c>#speak</c> is <see cref="Normal"/>;
/// per-stream read-aloud will map whispers→High, atmospherics→Low later.</summary>
public enum TtsPriority { Low = 0, Normal = 1, High = 2 }

/// <summary>
/// Offline neural text-to-speech backend (Piper VITS via sherpa-onnx) with a
/// bounded priority queue and a single synthesis/playback worker.
///
/// <list type="bullet">
///   <item>Synthesis + playback run on one background worker — the UI / game
///         loop never blocks.</item>
///   <item><b>Bounded queue</b> (cap 24, drop lowest-priority-oldest on overflow)
///         so a busy combat stream can't build an unbounded backlog.</item>
///   <item><b>Priority + barge-in</b>: a higher-priority line interrupts a
///         lower-priority one mid-utterance (via <see cref="TtsPlayer"/>) and
///         jumps the queue.</item>
///   <item>Engine + voice are created lazily and read live, so
///         <c>#config ttsvoicedir</c> / <c>#tts use</c> apply without a
///         reconnect (the engine rebuilds when dir or selection changes).</item>
/// </list>
/// </summary>
public sealed class TtsService : IDisposable
{
    private readonly Func<string> _voiceDirProvider;
    private readonly Func<string?>? _selectedVoiceProvider;
    private readonly Func<float>? _rateProvider;     // speed multiplier, 1 = natural
    private readonly Func<float>? _volumeProvider;   // linear gain 0..1
    private readonly Action<string>? _notify;
    private readonly TtsPlayer _player = new();

    // ── Engine (lazy, rebuilt on dir/voice change) ───────────────────────────
    private readonly System.Threading.Lock _engineGate = new();
    private OfflineTts? _engine;
    private string? _attemptedKey;   // dir|selected the engine / failure latch is for
    private bool _initFailed;

    // ── Queue + worker ───────────────────────────────────────────────────────
    private const int MaxQueue = 24;
    private readonly System.Threading.Lock _qlock = new();
    private readonly List<Request> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _worker;
    private long _seq;
    private volatile int _currentPriority = -1;   // priority of the clip playing now (-1 idle)
    private volatile bool _interruptCurrent;
    private volatile bool _running = true;

    private readonly record struct Request(string Text, int Priority, long Seq);

    public TtsService(
        Func<string> voiceDirProvider,
        Func<string?>? selectedVoiceProvider = null,
        Action<string>? notify = null,
        Func<float>? rateProvider = null,
        Func<float>? volumeProvider = null)
    {
        _voiceDirProvider = voiceDirProvider;
        _selectedVoiceProvider = selectedVoiceProvider;
        _rateProvider = rateProvider;
        _volumeProvider = volumeProvider;
        _notify = notify;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "TTS" };
        _worker.Start();
    }

    /// <summary>Queue <paramref name="text"/> to be spoken. Returns immediately.
    /// A higher <paramref name="priority"/> interrupts a lower one in flight.</summary>
    public void Speak(string text, TtsPriority priority = TtsPriority.Normal)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (_qlock)
        {
            _queue.Add(new Request(text.Trim(), (int)priority, _seq++));
            if (_queue.Count > MaxQueue)
            {
                // Drop the lowest-priority, oldest item (may be the one just added).
                int worst = 0;
                for (int i = 1; i < _queue.Count; i++)
                    if (_queue[i].Priority < _queue[worst].Priority ||
                        (_queue[i].Priority == _queue[worst].Priority && _queue[i].Seq < _queue[worst].Seq))
                        worst = i;
                _queue.RemoveAt(worst);
            }
        }

        if ((int)priority > _currentPriority)   // barge-in over a lower-priority clip
            _interruptCurrent = true;
        _signal.Release();
    }

    /// <summary>Stop the current utterance and clear anything queued.</summary>
    public void Stop()
    {
        lock (_qlock) _queue.Clear();
        _interruptCurrent = true;
    }

    /// <summary>Drop the engine + failure latch so the next utterance re-scans the
    /// voice dir. Call after installing or switching a voice.</summary>
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

    private void WorkerLoop()
    {
        while (_running)
        {
            try { _signal.Wait(); } catch { break; }
            if (!_running) break;

            Request req;
            lock (_qlock)
            {
                if (_queue.Count == 0) continue;          // spurious wake (e.g. after a drop)
                int best = 0;
                for (int i = 1; i < _queue.Count; i++)
                    if (_queue[i].Priority > _queue[best].Priority ||
                        (_queue[i].Priority == _queue[best].Priority && _queue[i].Seq < _queue[best].Seq))
                        best = i;
                req = _queue[best];
                _queue.RemoveAt(best);
            }

            var engine = EnsureEngine();
            if (engine is null) continue;

            _currentPriority = req.Priority;
            _interruptCurrent = false;
            try
            {
                // Rate + volume are read live per utterance so #tts rate/volume
                // apply from the very next spoken line, no engine rebuild needed.
                float rate = _rateProvider?.Invoke() ?? 1.0f;
                var gen = new OfflineTtsGenerationConfig { Sid = 0, Speed = rate > 0 ? rate : 1.0f };
                var audio = engine.GenerateWithConfig(req.Text, gen, null);

                float gain = Math.Clamp(_volumeProvider?.Invoke() ?? 1.0f, 0f, 1f);
                if (gain > 0f)
                {
                    if (gain < 1f)
                    {
                        var s = audio.Samples;
                        for (int i = 0; i < s.Length; i++) s[i] *= gain;
                    }
                    _player.Play(audio.Samples, audio.SampleRate, () => _interruptCurrent || !_running);
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Log("TtsService.Worker", ex);
            }
            finally
            {
                _currentPriority = -1;
            }
        }
    }

    /// <summary>Build the engine for the current voice dir + selection. Rebuilds
    /// when either changes; latches per-config when no usable voice exists.</summary>
    private OfflineTts? EnsureEngine()
    {
        lock (_engineGate)
        {
            string dir = _voiceDirProvider() ?? "";
            string selected = _selectedVoiceProvider?.Invoke() ?? "";
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
                ErrorLog.Log("TtsService.EnsureEngine", ex);
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
        _running = false;
        _interruptCurrent = true;
        try { _signal.Release(); } catch { /* disposed */ }
        try { _worker.Join(750); } catch { /* best-effort */ }
        _player.Dispose();
        lock (_engineGate) { _engine?.Dispose(); _engine = null; }
    }
}
