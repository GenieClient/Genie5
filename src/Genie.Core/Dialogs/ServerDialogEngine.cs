using System.Reactive.Subjects;
using Genie.Core.Events;

namespace Genie.Core.Dialogs;

/// <summary>Why a <see cref="ServerDialogState"/> changed.</summary>
public enum ServerDialogChangeKind
{
    /// <summary>DR sent <c>&lt;openDialog&gt;</c> — title and geometry are set.</summary>
    Opened,
    /// <summary>A <c>&lt;dialogData&gt;</c> delta merged into the control list.</summary>
    Data,
    /// <summary>DR sent <c>&lt;closeDialog&gt;</c>. State is KEPT, only hidden.</summary>
    Closed,
    /// <summary>DR sent <c>&lt;exposeDialog&gt;</c> — bring this one to the front.</summary>
    Exposed,
    /// <summary>Text routed into one of the dialog's streamBoxes changed.</summary>
    StreamChanged,
    /// <summary>Everything was dropped (disconnect). <c>DialogId</c> is empty.</summary>
    Reset,
}

/// <summary>One change published by <see cref="ServerDialogEngine"/>.</summary>
public sealed record ServerDialogChange(
    string DialogId,
    ServerDialogChangeKind Kind,
    ServerDialogState? State);

/// <summary>
/// An immutable snapshot of one server dialog. Handed to the UI thread as-is,
/// so a renderer never reads engine state while the game thread is writing it.
/// </summary>
public sealed class ServerDialogState
{
    internal ServerDialogState(
        string id, string? title, string? location, string? width, string? height,
        string? dialogType, bool resident, bool isOpen,
        IReadOnlyList<DialogControl> controls,
        IReadOnlyDictionary<string, string> streams,
        long revision)
    {
        Id = id; Title = title; Location = location; Width = width; Height = height;
        DialogType = dialogType; Resident = resident; IsOpen = isOpen;
        Controls = controls; Streams = streams; Revision = revision;
    }

    public string  Id         { get; }
    public string? Title      { get; }
    /// <summary>Server placement hint — <c>right</c>, <c>center</c>,
    /// <c>force-center</c>, <c>quickBar</c>, <c>detach</c> (float), …</summary>
    public string? Location   { get; }
    public string? Width      { get; }
    public string? Height     { get; }
    public string? DialogType { get; }
    public bool    Resident   { get; }

    /// <summary>Whether DR currently considers the window open. Content is kept
    /// either way — see the class remarks on <see cref="ServerDialogEngine"/>.</summary>
    public bool IsOpen { get; }

    /// <summary>Controls in first-seen order, merged by control id.</summary>
    public IReadOnlyList<DialogControl> Controls { get; }

    /// <summary>Text routed into this dialog's streamBoxes, keyed by control id.</summary>
    public IReadOnlyDictionary<string, string> Streams { get; }

    /// <summary>Bumped on every change — cheap staleness check for a renderer.</summary>
    public long Revision { get; }

    private DialogGrid? _grid;

    /// <summary>The inferred layout. Computed once per snapshot, on first use.</summary>
    public DialogGrid Grid => _grid ??= DialogGridLayout.Infer(Controls);
}

/// <summary>
/// Holds the live state of every server-driven dialog DR has described
/// (#156 Phase 1). Pure and UI-free: parser events in, immutable snapshots out
/// over <see cref="Changes"/>, so this can back an Avalonia window, a plugin
/// host, or a Mudlet embed without change.
///
/// <para><b>State outlives the window.</b> The Genie 4 DynamicWindows plugin
/// discarded any <c>dialogData</c> whose window was not currently open, so a
/// dialog opened later showed stale or empty content. This engine always
/// merges, and the window reads whatever is current whenever it opens.</para>
///
/// <para><b>Deltas, not redraws.</b> A <c>dialogData</c> block carries only the
/// controls that changed (the minivitals captures send one or two at a time);
/// controls merge by id and keep their first-seen order. The reset is the
/// <c>clear</c> ATTRIBUTE on the block.</para>
///
/// <para><b><c>clearContainer</c> is NOT that reset</b> — a note in the design
/// doc had it wrong, and the maintained plugin source settles it: it names a
/// container/streamBox by id and clears THAT control's text, leaving the
/// dialog's control list alone.</para>
/// </summary>
public sealed class ServerDialogEngine
{
    /// <summary>
    /// Dialogs a bespoke Genie 5 surface already owns, which must never reach
    /// the generic renderer: <c>injuries</c> is the #18 panel and
    /// <c>minivitals</c> is the vitals bar.
    ///
    /// <para>Matched EXACTLY, deliberately not by prefix. DR also sends
    /// <c>injuries-&lt;charnum&gt;</c> (seen 2026-09-02 as
    /// <c>injuries-10224090</c>, "Renucci's Injuries", its images carrying
    /// <c>cmd="transfer …"</c>) — that is a per-character window the empath
    /// view uses, which the plugin routes to a SEPARATE form, and #18 does not
    /// render it. A prefix exclusion here would silently swallow it.</para>
    /// </summary>
    public static readonly IReadOnlyCollection<string> BespokeDialogIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "injuries", "minivitals" };

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _dialogs = new(StringComparer.OrdinalIgnoreCase);

    // Stream text is keyed by CONTROL id and lives outside any one dialog,
    // matching the plugin's document cache: <dynaStream id='spellInfo'>…</> can
    // arrive before the dialog that displays it exists.
    private readonly Dictionary<string, string> _streams = new(StringComparer.OrdinalIgnoreCase);

    private readonly Subject<ServerDialogChange> _changes = new();
    private long _revision;

    /// <summary>Snapshots as they change. Published off the caller's thread —
    /// marshal to the UI thread in the host, as the other Core seams do.</summary>
    public IObservable<ServerDialogChange> Changes => _changes;

    public static bool IsBespoke(string dialogId) => BespokeDialogIds.Contains(dialogId);

    // ── Ingestion ────────────────────────────────────────────────────────────

    public void Observe(OpenDialogEvent e)
    {
        if (string.IsNullOrEmpty(e.Id) || IsBespoke(e.Id)) return;
        Publish(e.Id, ServerDialogChangeKind.Opened, entry =>
        {
            entry.Title      = NullIfEmpty(e.Title)    ?? entry.Title;
            entry.Location   = NullIfEmpty(e.Location) ?? entry.Location;
            entry.Width      = NullIfEmpty(e.Width)    ?? entry.Width;
            entry.Height     = NullIfEmpty(e.Height)   ?? entry.Height;
            entry.DialogType = NullIfEmpty(e.DialogType) ?? entry.DialogType;
            entry.Resident   = e.Resident;
            entry.IsOpen     = true;
        });
    }

    public void Observe(DialogDataEvent e)
    {
        if (string.IsNullOrEmpty(e.DialogId) || IsBespoke(e.DialogId)) return;
        Publish(e.DialogId, ServerDialogChangeKind.Data, entry =>
        {
            // The `clear` attribute is the dialog reset: drop what we had, then
            // apply this block (which may itself be empty — a bare reset).
            if (e.Clear) entry.ClearControls();

            int idless = 0;
            foreach (var control in e.Controls)
            {
                if (control.Type == DialogControlType.ClearContainer)
                {
                    // Clears the NAMED container's text, not the control list.
                    if (!string.IsNullOrEmpty(control.Id)) _streams.Remove(control.Id);
                    continue;
                }

                // Real dialogs always id their controls; a stray idless one gets
                // a stable synthetic slot so repeated blocks overwrite it rather
                // than growing the list without bound.
                var key = string.IsNullOrEmpty(control.Id)
                    ? $" {control.Type}#{idless++}"
                    : control.Id;

                entry.Upsert(key, control);
            }
        });
    }

    public void Observe(CloseDialogEvent e)
    {
        if (string.IsNullOrEmpty(e.Id) || IsBespoke(e.Id)) return;
        // Closing a dialog we never saw described is not worth inventing state for.
        if (!Exists(e.Id)) return;
        Publish(e.Id, ServerDialogChangeKind.Closed, entry => entry.IsOpen = false);
    }

    public void Observe(ExposeDialogEvent e)
    {
        if (string.IsNullOrEmpty(e.Id) || IsBespoke(e.Id)) return;
        Publish(e.Id, ServerDialogChangeKind.Exposed, entry => entry.IsOpen = true);
    }

    /// <summary>
    /// Text routed into a named streamBox (<c>&lt;dynaStream id='…'&gt;</c>).
    /// Cached whether or not a dialog displays it yet — public #324 has to land
    /// before the parser emits these, so nothing calls this from the pump today.
    /// </summary>
    public void SetStream(string controlId, string text)
    {
        if (string.IsNullOrEmpty(controlId)) return;
        NotifyStream(controlId, () => _streams[controlId] = text ?? "");
    }

    /// <summary><c>&lt;clearStream&gt;</c> / <c>&lt;clearDynaStream&gt;</c>.</summary>
    public void ClearStream(string controlId)
    {
        if (string.IsNullOrEmpty(controlId)) return;
        NotifyStream(controlId, () => _streams.Remove(controlId));
    }

    /// <summary>Drop everything — a disconnect invalidates every dialog, since
    /// the next session's server re-describes its own.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_dialogs.Count == 0 && _streams.Count == 0) return;
            _dialogs.Clear();
            _streams.Clear();
            _revision++;
        }
        _changes.OnNext(new ServerDialogChange("", ServerDialogChangeKind.Reset, null));
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    public ServerDialogState? Get(string dialogId)
    {
        if (string.IsNullOrEmpty(dialogId)) return null;
        lock (_gate)
            return _dialogs.TryGetValue(dialogId, out var entry) ? Snapshot(entry) : null;
    }

    /// <summary>Every dialog described so far, ordered by id.</summary>
    public IReadOnlyList<ServerDialogState> Snapshot()
    {
        lock (_gate)
            return _dialogs.Values
                .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .Select(Snapshot)
                .ToList();
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private bool Exists(string id)
    {
        lock (_gate) return _dialogs.ContainsKey(id);
    }

    private void Publish(string id, ServerDialogChangeKind kind, Action<Entry> mutate)
    {
        ServerDialogState snapshot;
        lock (_gate)
        {
            if (!_dialogs.TryGetValue(id, out var entry))
                _dialogs[id] = entry = new Entry(id);
            mutate(entry);
            entry.Revision = ++_revision;
            snapshot = Snapshot(entry);
        }
        // Published outside the lock: a subscriber that reads back through Get()
        // would otherwise re-enter it on the same thread.
        _changes.OnNext(new ServerDialogChange(id, kind, snapshot));
    }

    private void NotifyStream(string controlId, Action mutate)
    {
        List<ServerDialogChange> pending;
        lock (_gate)
        {
            mutate();
            _revision++;
            // Only dialogs that actually own a control by this id care.
            pending = _dialogs.Values
                .Where(e => e.HasControl(controlId))
                .Select(e =>
                {
                    e.Revision = _revision;
                    return new ServerDialogChange(
                        e.Id, ServerDialogChangeKind.StreamChanged, Snapshot(e));
                })
                .ToList();
        }
        foreach (var change in pending) _changes.OnNext(change);
    }

    private ServerDialogState Snapshot(Entry e)
    {
        var streams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in e.ControlKeys)
            if (_streams.TryGetValue(key, out var text)) streams[key] = text;

        return new ServerDialogState(
            e.Id, e.Title, e.Location, e.Width, e.Height, e.DialogType,
            e.Resident, e.IsOpen, e.Controls.ToList(), streams, e.Revision);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>Mutable per-dialog state. Never leaves the lock.</summary>
    private sealed class Entry(string id)
    {
        public string Id { get; } = id;
        public string? Title      { get; set; }
        public string? Location   { get; set; }
        public string? Width      { get; set; }
        public string? Height     { get; set; }
        public string? DialogType { get; set; }
        public bool    Resident   { get; set; }
        public bool    IsOpen     { get; set; }
        public long    Revision   { get; set; }

        // A list plus a key→position index, NOT a Dictionary on its own:
        // dictionary enumeration order is not part of the contract, and the
        // control order here is what the renderer lays out.
        private readonly List<DialogControl>  _controls = [];
        private readonly Dictionary<string, int> _index =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<DialogControl> Controls    => _controls;
        public IEnumerable<string>          ControlKeys => _index.Keys;

        public bool HasControl(string key) => _index.ContainsKey(key);

        /// <summary>Replace in place if the id is known, else append.</summary>
        public void Upsert(string key, DialogControl control)
        {
            if (_index.TryGetValue(key, out int at)) _controls[at] = control;
            else { _index[key] = _controls.Count; _controls.Add(control); }
        }

        public void ClearControls()
        {
            _controls.Clear();
            _index.Clear();
        }
    }
}
