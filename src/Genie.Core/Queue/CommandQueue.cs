namespace Genie.Core.Queue;

public sealed class CommandQueue
{
    public readonly List<CommandQueueItem> EventList = new();
    private readonly object _threadLock = new();
    private DateTime? _nextTime;
    private readonly Func<DateTime> _utcNow;

    /// <param name="utcNow">Clock for the per-item delays; defaults to the wall
    /// clock. Tests pass a manual clock so "has the delay elapsed yet" does not
    /// depend on how fast the machine gets from one line to the next.</param>
    public CommandQueue(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    public void AddToQueue(double delaySeconds, string action, bool waitForRoundtime, bool waitForWebbed, bool waitForStunned)
    {
        lock (_threadLock)
        {
            var restrictions = new CommandRestrictions
            {
                WaitForRoundtime = waitForRoundtime,
                WaitForWebbed    = waitForWebbed,
                WaitForStunned   = waitForStunned
            };
            EventList.Add(new CommandQueueItem(delaySeconds, action, restrictions));
            if (EventList.Count == 1) SetNextTime(delaySeconds);
        }
    }

    public void Clear() { lock (_threadLock) { EventList.Clear(); _nextTime = null; } }

    public string Poll(bool inRoundtime, bool isWebbed, bool isStunned)
    {
        lock (_threadLock)
        {
            if (EventList.Count == 0 || _nextTime is null) return string.Empty;
            if (_utcNow() < _nextTime.Value) return string.Empty;
            var item = EventList[0];
            if (item.IsRestricted(inRoundtime, isWebbed, isStunned)) return string.Empty;
            EventList.RemoveAt(0);
            if (EventList.Count > 0) SetNextTime(EventList[0].Delay);
            else _nextTime = null;
            return item.Action;
        }
    }

    private void SetNextTime(double delaySeconds) => _nextTime = _utcNow().AddSeconds(delaySeconds);
}

public sealed class CommandQueueItem
{
    public CommandQueueItem(double delay, string action, CommandRestrictions restrictions)
    { Delay = delay; Action = action; Restrictions = restrictions; }
    public double Delay { get; }
    public string Action { get; }
    public CommandRestrictions Restrictions { get; }
    public bool IsRestricted(bool inRoundtime, bool isWebbed, bool isStunned)
    {
        if (Restrictions.WaitForRoundtime && inRoundtime) return true;
        if (Restrictions.WaitForStunned   && isStunned)   return true;
        if (Restrictions.WaitForWebbed    && isWebbed)    return true;
        return false;
    }
}

public sealed class CommandRestrictions
{
    public bool WaitForRoundtime { get; set; }
    public bool WaitForStunned  { get; set; }
    public bool WaitForWebbed   { get; set; }
}
