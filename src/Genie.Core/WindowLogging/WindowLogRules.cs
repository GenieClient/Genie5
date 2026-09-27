using System.Text.Encodings.Web;
using System.Text.Json;

namespace Genie.Core.WindowLogging;

/// <summary>One per-window logging rule (public #270): route every line of
/// <see cref="Stream"/> to the file <see cref="File"/> expands to.</summary>
public sealed class WindowLogRule
{
    /// <summary>The stream / window id (<c>talk</c>, <c>thoughts</c>, <c>main</c>, …).
    /// Matched without regard to case.</summary>
    public string Stream { get; set; } = string.Empty;
    public bool   Enabled { get; set; } = true;
    /// <summary>Filename template; see <see cref="WindowLogPath"/> for tokens.</summary>
    public string File { get; set; } = WindowLogPath.DefaultTemplate;
    /// <summary>.NET date format prefixed to each line in brackets; empty = none.</summary>
    public string TimestampFormat { get; set; } = WindowLogPath.DefaultTimestampFormat;

    public WindowLogRule Clone() => new()
    {
        Stream = Stream, Enabled = Enabled, File = File, TimestampFormat = TimestampFormat,
    };
}

/// <summary>
/// The per-window logging rules for the connected profile, persisted as
/// <see cref="FileName"/> in <c>GenieConfig.ConfigProfileDir</c> (the same home
/// and load/save contract as the server-dialog mappings). One rule per stream;
/// several rules may name the same file, which is how <c>talk</c> and
/// <c>whispers</c> share one conversation log.
///
/// <para>Readers on the game thread use <see cref="Snapshot"/>, an immutable
/// copy swapped on every change, so the per-line check takes no lock.</para>
/// </summary>
public sealed class WindowLogRules
{
    public const string FileName = "windowlog.json";

    /// <summary>The Genie 4 Window Logger's shipped defaults (its
    /// <c>WindowLoggerConfig.xml</c>), with the leading <c>\GenieWindows</c>
    /// kept as a folder under Logs. <c>talk</c> and <c>whispers</c> share a file
    /// on purpose, so a conversation reads in order.</summary>
    public static IReadOnlyList<WindowLogRule> Genie4Defaults { get; } =
    [
        new() { Stream = "thoughts", File = @"GenieWindows\Thoughts\{charactername}\Thoughts-{charactername}-{yyyy}.txt" },
        new() { Stream = "talk",     File = @"GenieWindows\Conversations\{charactername}\Conversations-{charactername}-{yyyy}.txt" },
        new() { Stream = "whispers", File = @"GenieWindows\Conversations\{charactername}\Conversations-{charactername}-{yyyy}.txt" },
        new() { Stream = "logons",   File = @"GenieWindows\Logons\Logons-{charactername}-{yyyy}.txt" },
        new() { Stream = "death",    File = @"GenieWindows\Deaths\Deaths-{charactername}-{yyyy}.txt" },
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Matches PersistenceService: these files are shared and hand-edited.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, WindowLogRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyDictionary<string, WindowLogRule> _snapshot =
        new Dictionary<string, WindowLogRule>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Immutable view of the current rules, keyed by stream.</summary>
    public IReadOnlyDictionary<string, WindowLogRule> Snapshot => _snapshot;

    /// <summary>Raised after any change or load.</summary>
    public event Action? Changed;

    public IReadOnlyList<WindowLogRule> All() =>
        _snapshot.Values.OrderBy(r => r.Stream, StringComparer.OrdinalIgnoreCase).ToList();

    public WindowLogRule? TryGet(string stream) =>
        _snapshot.TryGetValue(stream, out var r) ? r.Clone() : null;

    /// <summary>Add or replace the rule for its stream.</summary>
    public void Set(WindowLogRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Stream)) return;
        lock (_gate) { _rules[rule.Stream.Trim()] = Normalize(rule.Clone()); Publish(); }
        Changed?.Invoke();
    }

    public bool Remove(string stream)
    {
        bool removed;
        lock (_gate) { removed = _rules.Remove(stream); if (removed) Publish(); }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Change one rule through <paramref name="edit"/>; false when no
    /// rule exists for <paramref name="stream"/>.</summary>
    public bool Update(string stream, Action<WindowLogRule> edit)
    {
        lock (_gate)
        {
            if (!_rules.TryGetValue(stream, out var existing)) return false;
            var copy = existing.Clone();
            edit(copy);
            _rules[stream] = Normalize(copy);
            Publish();
        }
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        lock (_gate) { _rules.Clear(); Publish(); }
        Changed?.Invoke();
    }

    private static WindowLogRule Normalize(WindowLogRule r)
    {
        r.Stream = r.Stream.Trim();
        r.File ??= string.Empty;
        r.TimestampFormat ??= string.Empty;
        return r;
    }

    // Callers hold _gate. Rules are cloned so a published rule never changes.
    private void Publish() =>
        _snapshot = _rules.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(),
                                        StringComparer.OrdinalIgnoreCase);

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>
    /// Which file a connect loads: the character's own <see cref="FileName"/>
    /// when it has one, else the shared one in the Config folder — which is
    /// where <c>#windowlog</c> saves while disconnected, so rules set up before
    /// logging in apply to every character that has none of its own. The first
    /// change made while connected saves a per-character copy.
    /// </summary>
    public static string PathToLoad(string profileDir, string sharedConfigDir)
    {
        var own = Path.Combine(profileDir, FileName);
        return System.IO.File.Exists(own) ? own : Path.Combine(sharedConfigDir, FileName);
    }

    /// <summary>
    /// Load from <paramref name="path"/>, replacing the rules. A missing file
    /// means "no rules for this profile" and clears them (so a reconnect as a
    /// different character does not inherit, then save, the previous one's).
    /// An unreadable or malformed file returns false and LEAVES THE CURRENT
    /// RULES ALONE — a torn file must not silently wipe a user's setup.
    /// </summary>
    public bool Load(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            lock (_gate) { _rules.Clear(); Publish(); }
            Changed?.Invoke();
            return false;
        }
        try
        {
            var loaded = JsonSerializer.Deserialize<List<WindowLogRule>>(
                System.IO.File.ReadAllText(path), JsonOptions);
            if (loaded is null) return false;
            lock (_gate)
            {
                _rules.Clear();
                foreach (var r in loaded)
                    if (r is not null && !string.IsNullOrWhiteSpace(r.Stream))
                        _rules[r.Stream.Trim()] = Normalize(r);
                Publish();
            }
            Changed?.Invoke();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Write the rules to <paramref name="path"/>. Returns false rather
    /// than throwing — a settings write must not take the session down.</summary>
    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(All(), JsonOptions));
            return true;
        }
        catch { return false; }
    }
}
