using System;
using System.Collections.Generic;
using System.Threading;
using Genie.App.Diagnostics;

namespace Genie.App.Services;

/// <summary>Relative urgency of a spoken line. Higher interrupts lower in flight
/// and is spoken first from the queue. <c>#speak</c> is <see cref="Normal"/>;
/// per-stream read-aloud will map whispers→High, atmospherics→Low later.</summary>
public enum TtsPriority { Low = 0, Normal = 1, High = 2 }

/// <summary>
/// Text-to-speech front end: a bounded priority queue and a single speaking
/// worker over interchangeable <see cref="ITextToSpeech"/> backends (public
/// #368) — the bundled Piper voices (default + fallback) and the OS's own
/// installed voices.
///
/// <list type="bullet">
///   <item>Synthesis + playback run on one background worker — the UI / game
///         loop never blocks.</item>
///   <item><b>Bounded queue</b> (cap 24, drop lowest-priority-oldest on overflow)
///         so a busy combat stream can't build an unbounded backlog.</item>
///   <item><b>Priority + barge-in</b>: a higher-priority line interrupts a
///         lower-priority one mid-utterance and jumps the queue. Every backend
///         polls the same stop predicate, so <c>#tts stop</c> and barge-in work
///         whichever engine is speaking.</item>
///   <item>The selected voice (<c>ttsvoice</c>) is resolved per utterance, so
///         <c>#tts use</c> / <c>#config ttsvoicedir</c> apply without a
///         reconnect. A saved system voice that is no longer installed falls
///         back to Piper, and says so once.</item>
/// </list>
/// </summary>
public sealed class TtsService : IDisposable
{
    private readonly ITextToSpeech _piper;
    private readonly ITextToSpeech? _system;
    private readonly Func<string?>? _selectedVoiceProvider;
    private readonly Func<float>? _rateProvider;     // speed multiplier, 1 = natural
    private readonly Func<float>? _volumeProvider;   // linear gain 0..1
    private readonly Action<string>? _notify;
    private volatile string? _fallbackAnnouncedFor;  // saved value the fallback notice was given for

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

    /// <summary>The shipping wiring: Piper over <paramref name="voiceDirProvider"/>
    /// plus this OS's system-voice backend.</summary>
    public TtsService(
        Func<string> voiceDirProvider,
        Func<string?>? selectedVoiceProvider = null,
        Action<string>? notify = null,
        Func<float>? rateProvider = null,
        Func<float>? volumeProvider = null)
        : this(new PiperTextToSpeech(voiceDirProvider, notify), SystemTextToSpeech.Create(),
               selectedVoiceProvider, notify, rateProvider, volumeProvider)
    {
    }

    /// <summary>Explicit backends — used by the shipping constructor and by tests
    /// (fake backends). <paramref name="system"/> may be null (no system voices).</summary>
    public TtsService(
        ITextToSpeech piper,
        ITextToSpeech? system,
        Func<string?>? selectedVoiceProvider = null,
        Action<string>? notify = null,
        Func<float>? rateProvider = null,
        Func<float>? volumeProvider = null)
    {
        _piper = piper;
        _system = system;
        _selectedVoiceProvider = selectedVoiceProvider;
        _rateProvider = rateProvider;
        _volumeProvider = volumeProvider;
        _notify = notify;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "TTS" };
        _worker.Start();
    }

    /// <summary>The backends, Piper first.</summary>
    public IReadOnlyList<ITextToSpeech> Backends =>
        _system is null ? new[] { _piper } : new[] { _piper, _system };

    /// <summary>Every voice from every backend, in one list (Piper first).</summary>
    public IReadOnlyList<TtsVoiceOption> ListVoices() => TtsVoiceList.Merge(Backends);

    /// <summary>"Amy (Piper)" / "Microsoft Zira Desktop (system)".</summary>
    public string Label(TtsVoiceOption voice) => TtsVoiceList.Label(voice, Backends);

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

    /// <summary>Drop cached engines, voice lists and the fallback notice so the
    /// next utterance re-scans. Call after installing or switching a voice.</summary>
    public void Reset()
    {
        _piper.Reset();
        _system?.Reset();
        _fallbackAnnouncedFor = null;
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

            _currentPriority = req.Priority;
            _interruptCurrent = false;
            try
            {
                var target = ResolveBackend();

                // Rate + volume are read live per utterance so #tts rate/volume
                // apply from the very next spoken line, no engine rebuild needed.
                float rate = _rateProvider?.Invoke() ?? 1.0f;
                float gain = Math.Clamp(_volumeProvider?.Invoke() ?? 1.0f, 0f, 1f);
                target.Backend.Speak(req.Text, target.Voice,
                    new TtsSpeechParams(rate > 0 ? rate : 1.0f, gain),
                    () => _interruptCurrent || !_running);
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

    /// <summary>The backend + voice for the saved selection, announcing a
    /// system-voice fallback to Piper once per saved value.</summary>
    private TtsResolution ResolveBackend()
    {
        string saved = _selectedVoiceProvider?.Invoke() ?? "";
        var r = TtsBackendSelector.Resolve(saved, _piper, _system);
        if (r.FellBack)
        {
            if (!string.Equals(saved, _fallbackAnnouncedFor, StringComparison.OrdinalIgnoreCase))
            {
                _fallbackAnnouncedFor = saved;
                _notify?.Invoke(
                    $"[tts] system voice '{TtsVoiceSetting.Parse(saved).Name}' isn't available on this " +
                    "computer — speaking with the Piper voice instead. See #tts voices to pick another.");
            }
        }
        else
            _fallbackAnnouncedFor = null;   // voice is back: losing it again is news again
        return r;
    }

    public void Dispose()
    {
        _running = false;
        _interruptCurrent = true;
        try { _signal.Release(); } catch { /* disposed */ }
        try { _worker.Join(750); } catch { /* best-effort */ }
        _piper.Dispose();
        _system?.Dispose();
    }
}
