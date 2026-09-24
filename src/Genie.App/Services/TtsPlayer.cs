using System;
using System.Runtime.InteropServices;
using System.Threading;
using Genie.App.Diagnostics;
using PortAudioSharp;

namespace Genie.App.Services;

/// <summary>
/// Streaming PCM output for TTS via PortAudio. Plays one mono float32 clip at a
/// time, blocking the calling (worker) thread until the clip finishes or is
/// interrupted — the interrupt predicate is checked inside the audio callback,
/// so barge-in is near-instant (one callback buffer, a few ms). This is the
/// piece the v1 temp-WAV/winmm path couldn't do: stop mid-utterance.
///
/// <para>One output stream is kept open and reused; it's reopened only when the
/// sample rate changes (i.e. switching to a voice with a different rate).</para>
/// </summary>
public sealed class TtsPlayer : IDisposable
{
    private readonly System.Threading.Lock _lock = new();
    private bool _paInitialized;
    private PortAudioSharp.Stream? _stream;
    private int _streamRate = -1;

    // Current clip state — only mutated under the contract that Play() runs one
    // clip at a time and sets these before Start(); the callback reads them.
    private float[] _data = Array.Empty<float>();
    private int _pos;
    private Func<bool>? _interrupted;
    private readonly ManualResetEventSlim _finished = new(false);

    // Held in fields so the GC can't collect the delegates while native holds them.
    private PortAudioSharp.Stream.Callback? _callback;
    private PortAudioSharp.Stream.FinishedCallback? _finishedCallback;

    /// <summary>Play <paramref name="samples"/> (mono float32) at
    /// <paramref name="sampleRate"/>. Blocks until done or
    /// <paramref name="interrupted"/> returns true. Never throws.</summary>
    public void Play(float[] samples, int sampleRate, Func<bool> interrupted)
    {
        if (samples is null || samples.Length == 0) return;
        lock (_lock)
        {
            if (!EnsureStream(sampleRate) || _stream is null) return;

            _data = samples;
            _pos = 0;
            _interrupted = interrupted;
            _finished.Reset();

            try
            {
                _stream.Start();
                _finished.Wait();                       // callback signals via _finishedCallback
                if (!_stream.IsStopped) _stream.Stop();
            }
            catch (Exception ex)
            {
                ErrorLog.Log("TtsPlayer.Play", ex);
            }
        }
    }

    private bool EnsureStream(int sampleRate)
    {
        try
        {
            if (!_paInitialized) { PortAudio.Initialize(); _paInitialized = true; }
            if (_stream is not null && _streamRate == sampleRate) return true;

            _stream?.Dispose();
            _stream = null;
            _streamRate = -1;

            int device = PortAudio.DefaultOutputDevice;
            if (device == PortAudio.NoDevice)
            {
                ErrorLog.Log("TtsPlayer.EnsureStream",
                    new InvalidOperationException("No default audio output device."));
                return false;
            }

            var info = PortAudio.GetDeviceInfo(device);
            var p = new StreamParameters
            {
                device = device,
                channelCount = 1,
                sampleFormat = SampleFormat.Float32,
                suggestedLatency = info.defaultLowOutputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero,
            };

            _callback ??= OnCallback;
            _finishedCallback ??= _ => _finished.Set();

            _stream = new PortAudioSharp.Stream(
                inParams: null, outParams: p, sampleRate: sampleRate,
                framesPerBuffer: 0, streamFlags: StreamFlags.ClipOff,
                callback: _callback, userData: IntPtr.Zero);
            _stream.SetFinishedCallback(_finishedCallback);
            _streamRate = sampleRate;
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Log("TtsPlayer.EnsureStream", ex);
            _stream = null;
            _streamRate = -1;
            return false;
        }
    }

    private StreamCallbackResult OnCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        int fc = (int)frameCount;

        if (_interrupted?.Invoke() == true)
        {
            ZeroFill(output, 0, fc);
            return StreamCallbackResult.Complete;
        }

        int n = Math.Min(fc, _data.Length - _pos);
        if (n > 0) { Marshal.Copy(_data, _pos, output, n); _pos += n; }
        if (n < fc)
        {
            ZeroFill(output, n, fc - n);           // pad the final buffer with silence
            return StreamCallbackResult.Complete;
        }
        return StreamCallbackResult.Continue;
    }

    private static void ZeroFill(IntPtr output, int frameOffset, int frames)
    {
        if (frames <= 0) return;
        Marshal.Copy(new float[frames], 0, IntPtr.Add(output, frameOffset * sizeof(float)), frames);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _stream?.Stop(); } catch { /* best-effort */ }
            try { _stream?.Dispose(); } catch { /* best-effort */ }
            _stream = null;
            if (_paInitialized)
            {
                try { PortAudio.Terminate(); } catch { /* best-effort */ }
                _paInitialized = false;
            }
        }
    }
}
