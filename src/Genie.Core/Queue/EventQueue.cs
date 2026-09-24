namespace Genie.Core.Queue;

public sealed class EventQueue
{
    private readonly List<EventItem> _events = new();
    private readonly System.Threading.Lock _lock = new();

    public void Add(double delaySeconds, string action)
    {
        lock (_lock)
        {
            var time = DateTime.UtcNow.AddMilliseconds(delaySeconds * 1000);
            _events.Add(new EventItem(time, action));
            _events.Sort((a, b) => a.ExecuteAt.CompareTo(b.ExecuteAt));
        }
    }

    public string Poll()
    {
        lock (_lock)
        {
            if (_events.Count == 0) return string.Empty;
            var next = _events[0];
            if (DateTime.UtcNow < next.ExecuteAt) return string.Empty;
            _events.RemoveAt(0);
            return next.Action;
        }
    }
}

public sealed class EventItem
{
    public EventItem(DateTime executeAt, string action) { ExecuteAt = executeAt; Action = action; }
    public DateTime ExecuteAt { get; }
    public string   Action    { get; }
}
