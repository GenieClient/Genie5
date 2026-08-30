using Genie.Core.Events;

namespace Genie.Core.Dialogs;

/// <summary>
/// Session-scoped inventory of the server dialogs seen so far (#156 Phase 0c)
/// — backs <c>#dialogs list</c> (the DynamicWindows <c>/debugwindows</c>
/// parity) and supplies the raw sample + control census for
/// <c>#dialogs report</c>. Thread-safe; fed by GenieCore's event pump.
/// </summary>
public sealed class DialogSessionTracker
{
    public sealed class Row
    {
        public string  Id       { get; init; } = "";
        public string? Title    { get; set; }
        public int     Blocks   { get; set; }
        public string  LastRaw  { get; set; } = "";
        public Dictionary<DialogControlType, int> ControlCounts { get; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Row> _rows = new(StringComparer.OrdinalIgnoreCase);

    public void Observe(OpenDialogEvent e)
    {
        if (string.IsNullOrEmpty(e.Id)) return;
        lock (_gate) { GetRow(e.Id).Title = e.Title; }
    }

    public void Observe(DialogDataEvent e)
    {
        if (string.IsNullOrEmpty(e.DialogId)) return;
        lock (_gate)
        {
            var row = GetRow(e.DialogId);
            row.Blocks++;
            if (!string.IsNullOrEmpty(e.RawXml)) row.LastRaw = e.RawXml;
            foreach (var c in e.Controls)
                row.ControlCounts[c.Type] = row.ControlCounts.GetValueOrDefault(c.Type) + 1;
        }
    }

    /// <summary>Copy of the rows, ordered by id.</summary>
    public IReadOnlyList<Row> Snapshot()
    {
        lock (_gate)
            return _rows.Values
                .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToList();
    }

    public Row? TryGet(string id)
    {
        lock (_gate)
            return _rows.TryGetValue(id, out var row) ? Clone(row) : null;
    }

    private Row GetRow(string id)
    {
        if (!_rows.TryGetValue(id, out var row)) _rows[id] = row = new Row { Id = id };
        return row;
    }

    private static Row Clone(Row r)
    {
        var copy = new Row { Id = r.Id, Title = r.Title, Blocks = r.Blocks, LastRaw = r.LastRaw };
        foreach (var kv in r.ControlCounts) copy.ControlCounts[kv.Key] = kv.Value;
        return copy;
    }
}
