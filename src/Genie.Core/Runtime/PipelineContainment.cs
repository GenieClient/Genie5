using System;

namespace Genie.Core.Runtime;

/// <summary>
/// Per-subscriber fault containment for the hot pipeline observables (2026-08-31 stability review).
///
/// <para><b>Why this exists.</b> <c>Subject&lt;T&gt;.OnNext</c> walks its subscriber
/// list inline and does NOT isolate them: the first subscriber to throw aborts the
/// walk, so every LATER subscriber silently misses that value. On the game-event
/// stream the UI relay subscribes last, which means one throwing consumer — a user's
/// trigger action, a state-apply edge case, a parser corner — makes the player's game
/// text simply stop appearing, with no error and nothing in the window to explain it.
/// With the game loop disabled (<c>#config gamethread off</c>, and every unit test)
/// the same throw escapes further still, into <c>GameConnection.ReadLoopAsync</c>,
/// which exits and emits Disconnected — one bad line tearing down a live session,
/// mid-combat.</para>
///
/// <para><b>What it does.</b> <see cref="Contained{T}"/> returns a view of a source
/// stream that wraps each subscriber in its own guard, so a fault is confined to the
/// consumer that raised it: that one consumer misses the value, every other consumer
/// still receives it, and the connection is untouched. Because the guard is applied
/// per <c>Subscribe</c> call, handing the contained view to a consumer isolates it
/// without that consumer knowing anything about containment — which is how the four
/// engines that subscribe inside their own constructors (state, mapper, globals sync,
/// relays) are covered without touching them.</para>
///
/// <para>Containment is a backstop, not a licence: a fault reaching here is still a
/// bug, which is why <paramref name="onFault"/> both logs and tells the player once.</para>
/// </summary>
internal static class PipelineContainment
{
    /// <summary>Wrap <paramref name="source"/> so each subscriber's callbacks are
    /// guarded independently. Faults are reported to <paramref name="onFault"/> and
    /// swallowed; the stream itself continues.</summary>
    public static IObservable<T> Contained<T>(this IObservable<T> source, Action<Exception> onFault)
        => new ContainedObservable<T>(source, onFault);

    private sealed class ContainedObservable<T> : IObservable<T>
    {
        private readonly IObservable<T>   _source;
        private readonly Action<Exception> _onFault;

        public ContainedObservable(IObservable<T> source, Action<Exception> onFault)
        {
            _source  = source;
            _onFault = onFault;
        }

        public IDisposable Subscribe(IObserver<T> observer)
            => _source.Subscribe(new GuardedObserver<T>(observer, _onFault));
    }

    private sealed class GuardedObserver<T> : IObserver<T>
    {
        private readonly IObserver<T>     _inner;
        private readonly Action<Exception> _onFault;

        public GuardedObserver(IObserver<T> inner, Action<Exception> onFault)
        {
            _inner   = inner;
            _onFault = onFault;
        }

        public void OnNext(T value)
        {
            try { _inner.OnNext(value); }
            catch (Exception ex) { Report(ex); }
        }

        /// <summary>Rx's <c>Subscribe(Action&lt;T&gt;)</c> overload builds an observer
        /// whose OnError RETHROWS on the calling thread. Guarding it here keeps a
        /// stream fault from becoming the very teardown this class exists to prevent;
        /// it is still reported, never silently dropped.</summary>
        public void OnError(Exception error)
        {
            try { _inner.OnError(error); }
            catch (Exception ex) { Report(ex); }
        }

        public void OnCompleted()
        {
            try { _inner.OnCompleted(); }
            catch (Exception ex) { Report(ex); }
        }

        private void Report(Exception ex)
        {
            // The reporter itself must never fault — it runs on the pipeline thread,
            // and throwing here would defeat the containment.
            try { _onFault(ex); } catch { /* nothing safe left to do */ }
        }
    }
}
