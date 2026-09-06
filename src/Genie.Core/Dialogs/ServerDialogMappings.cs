using System.Text.Encodings.Web;
using System.Text.Json;

namespace Genie.Core.Dialogs;

/// <summary>Where a server dialog should be rendered.</summary>
public enum ServerDialogMode
{
    /// <summary>Not answered yet — buffer the state, render nothing. Never
    /// persisted: an unanswered dialog asks again next session.</summary>
    AskLater,
    /// <summary>Its own dedicated dock window.</summary>
    NewWindow,
    /// <summary>Honour the server's own <c>openDialog</c> placement hints —
    /// <c>right</c>/<c>left</c>/<c>center</c> dock, <c>detach</c> floats.</summary>
    WhereDrProposes,
    /// <summary>Render into an existing window named by <c>Target</c>.</summary>
    ExistingWindow,
    /// <summary>Never render this dialog, and stop asking.</summary>
    Ignore,
}

/// <summary>One persisted decision about one dialog id.</summary>
public sealed class ServerDialogMapping
{
    public string Id { get; set; } = "";
    public ServerDialogMode Mode { get; set; } = ServerDialogMode.NewWindow;

    /// <summary>Window name for <see cref="ServerDialogMode.ExistingWindow"/>.</summary>
    public string? Target { get; set; }

    /// <summary>Open the window when DR sends <c>openDialog</c>; when false it
    /// populates silently and is opened from the Window menu.</summary>
    public bool AutoOpen { get; set; } = true;

    /// <summary>Last title the server gave this dialog — so the settings grid can
    /// show something friendlier than a bare id for a dialog not seen yet.</summary>
    public string? Title { get; set; }
}

/// <summary>What to do with a dialog right now, mapping and session state combined.</summary>
public sealed record ServerDialogDisposition(
    ServerDialogMode Mode,
    string? Target,
    bool AutoOpen,
    bool NeedsPrompt)
{
    /// <summary>Whether a renderer should draw this dialog at all.</summary>
    public bool ShouldRender =>
        Mode is ServerDialogMode.NewWindow
             or ServerDialogMode.WhereDrProposes
             or ServerDialogMode.ExistingWindow;
}

/// <summary>
/// The per-profile Dialog Layout Mapping (#156 Phase 1): dialog id → where it
/// renders, plus the session bookkeeping behind the first-seen prompt.
///
/// <para>Persisted to <c>dialogmappings.json</c> in the connected profile's
/// directory. Pure Core — it decides <em>what</em> should happen; the chooser
/// UI, the windows and the settings grid live in the host.</para>
///
/// <para>The Genie 4 plugin had no mapping layer: every
/// <c>openDialog type="dynamic"</c> created a window immediately, and the only
/// recourse was an opt-out Ignore list you edited AFTER being surprised. Asking
/// once, up front, is the improvement.</para>
/// </summary>
public sealed class ServerDialogMappings
{
    public const string FileName = "dialogmappings.json";

    /// <summary>
    /// DR's login block declares a quick-bar launcher menu as four separate
    /// dialogs (<c>quick-simu</c>, <c>quick-char</c>, <c>quick-blank</c>,
    /// <c>quick-tip</c> in the 2026-08-30 journal), all with
    /// <c>location='quickBar'</c>. Without a default these would fire four
    /// first-seen prompts at every user's very first login, before they have
    /// any idea what a server dialog is. They are ignored unless the user
    /// deliberately maps them — keyed off the LOCATION, not the ids, since
    /// those are not a closed set.
    /// </summary>
    public const string QuickBarLocation = "quickBar";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Matches PersistenceService: these files are shared and hand-edited.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly object _gate = new();

    private readonly Dictionary<string, ServerDialogMapping> _mappings =
        new(StringComparer.OrdinalIgnoreCase);

    // Session-only, never persisted.
    private readonly HashSet<string> _deferred = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _prompted = new(StringComparer.OrdinalIgnoreCase);

    // ── Resolution ───────────────────────────────────────────────────────────

    /// <summary>
    /// What to do with <paramref name="dialogId"/> right now.
    /// <paramref name="location"/> is the server's placement hint from
    /// <c>openDialog</c>, used only to apply the quick-bar default.
    /// </summary>
    public ServerDialogDisposition Resolve(string dialogId, string? location = null)
    {
        if (string.IsNullOrEmpty(dialogId))
            return new(ServerDialogMode.Ignore, null, false, NeedsPrompt: false);

        lock (_gate)
        {
            if (_mappings.TryGetValue(dialogId, out var m))
                return new(m.Mode, m.Target, m.AutoOpen, NeedsPrompt: false);

            // "Ask me later" — hold off for the rest of this session.
            if (_deferred.Contains(dialogId))
                return new(ServerDialogMode.AskLater, null, false, NeedsPrompt: false);

            if (string.Equals(location, QuickBarLocation, StringComparison.OrdinalIgnoreCase))
                return new(ServerDialogMode.Ignore, null, false, NeedsPrompt: false);

            // Unmapped: buffer it and ask, but only if we have not already.
            return new(ServerDialogMode.AskLater, null, false,
                       NeedsPrompt: !_prompted.Contains(dialogId));
        }
    }

    /// <summary>
    /// Claim the right to prompt for this dialog id. True at most ONCE per id
    /// per session, so a burst of dialogData cannot queue a stack of choosers
    /// for the same dialog. The host queues these one at a time and must never
    /// let one block the game loop.
    /// </summary>
    public bool TryClaimPrompt(string dialogId)
    {
        if (string.IsNullOrEmpty(dialogId)) return false;
        lock (_gate)
        {
            if (_mappings.ContainsKey(dialogId) || _deferred.Contains(dialogId)) return false;
            return _prompted.Add(dialogId);
        }
    }

    /// <summary>The user chose "ask me later" — skip for this session only.</summary>
    public void DeferForSession(string dialogId)
    {
        if (string.IsNullOrEmpty(dialogId)) return;
        lock (_gate) _deferred.Add(dialogId);
    }

    /// <summary>Forget every session-only decision (a new connection).</summary>
    public void ResetSession()
    {
        lock (_gate)
        {
            _deferred.Clear();
            _prompted.Clear();
        }
    }

    // ── Mapping table ────────────────────────────────────────────────────────

    public ServerDialogMapping? Find(string dialogId)
    {
        if (string.IsNullOrEmpty(dialogId)) return null;
        lock (_gate)
            return _mappings.TryGetValue(dialogId, out var m) ? Clone(m) : null;
    }

    /// <summary>Record a decision. <see cref="ServerDialogMode.AskLater"/> is not
    /// a decision — it defers for the session instead of persisting.</summary>
    public void Set(ServerDialogMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (string.IsNullOrEmpty(mapping.Id)) return;

        if (mapping.Mode == ServerDialogMode.AskLater)
        {
            DeferForSession(mapping.Id);
            return;
        }

        lock (_gate)
        {
            _mappings[mapping.Id] = Clone(mapping);
            _deferred.Remove(mapping.Id);
        }
    }

    /// <summary>Drop a mapping so the dialog is asked about again — the settings
    /// grid's "re-prompt" action.</summary>
    public bool Remove(string dialogId)
    {
        if (string.IsNullOrEmpty(dialogId)) return false;
        lock (_gate)
        {
            _deferred.Remove(dialogId);
            _prompted.Remove(dialogId);
            return _mappings.Remove(dialogId);
        }
    }

    /// <summary>Note the server's title for a dialog, for the settings grid.</summary>
    public void NoteTitle(string dialogId, string? title)
    {
        if (string.IsNullOrEmpty(dialogId) || string.IsNullOrEmpty(title)) return;
        lock (_gate)
            if (_mappings.TryGetValue(dialogId, out var m)) m.Title = title;
    }

    /// <summary>Every mapping, ordered by id.</summary>
    public IReadOnlyList<ServerDialogMapping> All()
    {
        lock (_gate)
            return _mappings.Values
                .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToList();
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>
    /// Load from <paramref name="path"/>, replacing the table. Returns false and
    /// LEAVES THE EXISTING TABLE ALONE if the file is unreadable or malformed —
    /// a torn file must not silently wipe a user's decisions (the lesson from
    /// the rule-file live reload).
    /// </summary>
    public bool Load(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<ServerDialogMapping>>(
                File.ReadAllText(path), JsonOptions);
            if (loaded is null) return false;

            lock (_gate)
            {
                _mappings.Clear();
                foreach (var m in loaded)
                    if (!string.IsNullOrEmpty(m.Id)) _mappings[m.Id] = m;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Write the table to <paramref name="path"/>. Returns false rather
    /// than throwing — a settings write must not take the session down.</summary>
    public bool Save(string path)
    {
        try
        {
            var data = All();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
            return true;
        }
        catch { return false; }
    }

    private static ServerDialogMapping Clone(ServerDialogMapping m) => new()
    {
        Id = m.Id, Mode = m.Mode, Target = m.Target, AutoOpen = m.AutoOpen, Title = m.Title,
    };
}
