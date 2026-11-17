namespace Genie.App;

/// <summary>
/// Decides whether an exception nothing caught on the UI thread may be logged
/// and survived, instead of closing the app.
/// </summary>
/// <remarks>
/// A single failure in a panel (one game line a helper could not handle, one
/// refresh that raced another thread) used to close the whole session (public
/// #398, #399). One such failure is now written to the crash log and the
/// session carries on.
/// <para>
/// A burst is different: the same handler failing again and again means the
/// app's state is broken, and carrying on would only spin and fill the log. So
/// only <see cref="MaxPerWindow"/> failures inside <see cref="Window"/> are
/// survived; the next one closes the app exactly as before.
/// </para>
/// </remarks>
internal sealed class UiCrashGuard
{
    internal const int MaxPerWindow = 5;
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly Queue<DateTime> _recent = new();

    /// <summary>Records a failure at <paramref name="now"/>. True when the app
    /// should carry on; false when failures are arriving in a burst and the
    /// exception should be left to close the app.</summary>
    public bool ShouldSurvive(DateTime now)
    {
        while (_recent.Count > 0 && now - _recent.Peek() > Window) _recent.Dequeue();
        _recent.Enqueue(now);
        return _recent.Count <= MaxPerWindow;
    }
}
