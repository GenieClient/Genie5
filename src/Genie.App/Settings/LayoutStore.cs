namespace Genie.App.Settings;

/// <summary>
/// Disk-backed store for named <see cref="SavedLayout"/>s. Each layout
/// is one JSON file at <c>{LayoutsDir}/{Name}.json</c>; the dir is
/// usually <c>{AppData}/Genie5/Layouts/</c>.
///
/// <para>
/// Per-character vs global: layouts live at the global Genie5 root so
/// the same set of presets is available to every character — Genie 4
/// muscle memory. If we ever need per-character layouts (e.g. an
/// Empath wants different defaults from a Barbarian), point this at
/// the per-profile config dir instead.
/// </para>
///
/// <para>
/// Built-in overlay: the global store can additionally be pointed at a
/// read-only directory of layouts shipped beside the executable
/// (<c>{app}/Layouts/</c> — "Shadowveil" lives there). Built-ins appear
/// in <see cref="List"/> alongside user saves; saving under a built-in's
/// name writes a user copy that shadows it, and deleting that copy
/// reverts to the shipped version. The shipped files themselves are
/// never written or deleted (they typically sit under Program Files).
/// </para>
/// </summary>
public sealed class LayoutStore
{
    /// <summary>Name of the shipped built-in that mirrors the classic
    /// out-of-box arrangement — the factory value of
    /// <see cref="DisplaySettings.GlobalDefaultLayout"/>.</summary>
    public const string ShippedDefaultLayoutName = "Strongbox";

    private readonly string  _dir;
    private readonly string? _builtinDir;

    public LayoutStore(string layoutsDir, string? builtinDir = null)
    {
        _dir        = layoutsDir;
        _builtinDir = builtinDir;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>List all saved layouts (file basename without `.json`),
    /// including shipped built-ins not shadowed by a user save.
    /// Sorted alphabetically for stable menu order.</summary>
    public IReadOnlyList<string> List()
    {
        var names = new List<string>();
        if (Directory.Exists(_dir))
            names.AddRange(Directory.EnumerateFiles(_dir, "*.json")
                .Select(p => Path.GetFileNameWithoutExtension(p) ?? "")
                .Where(n => !string.IsNullOrEmpty(n)));
        if (_builtinDir is not null && Directory.Exists(_builtinDir))
            names.AddRange(Directory.EnumerateFiles(_builtinDir, "*.json")
                .Select(p => Path.GetFileNameWithoutExtension(p) ?? "")
                .Where(n => !string.IsNullOrEmpty(n)
                         && !names.Contains(n, StringComparer.OrdinalIgnoreCase)));
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Load a saved layout by name — the user's save wins over a
    /// shipped built-in with the same name. Returns null if not found
    /// or the file is unreadable / malformed.</summary>
    public SavedLayout? Load(string name)
    {
        var path = ResolvePath(name);
        if (!File.Exists(path)) path = ResolveBuiltinPath(name);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var layout = SavedLayout.FromJson(json);
            // Keep the file name authoritative in case the JSON's Name
            // drifted from disk reality (rename via filesystem).
            if (layout is not null) layout.Name = name;
            return layout;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Write the layout to disk, overwriting any existing file
    /// with the same name. The layout's <see cref="SavedLayout.Name"/>
    /// is sanitised to a filesystem-safe basename.</summary>
    public void Save(SavedLayout layout)
    {
        if (string.IsNullOrWhiteSpace(layout.Name))
            throw new ArgumentException("Layout name cannot be empty.", nameof(layout));

        // Refresh the timestamp so the file mtime + the JSON field stay
        // in sync — useful when sorting layouts by recency later.
        layout.SavedAt = DateTimeOffset.Now.ToString("O");

        var path = ResolvePath(layout.Name);
        File.WriteAllText(path, layout.ToJson());
    }

    /// <summary>Delete the named layout's USER file. Shipped built-ins are
    /// never touched — deleting a user save that shadowed one reverts to the
    /// shipped version. Returns false when there was no user file to delete
    /// (including a built-in with no user copy).</summary>
    public bool Delete(string name)
    {
        var path = ResolvePath(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Returns true if a layout with this (sanitised) name already
    /// exists — as a user save or a shipped built-in. Used by Save As to
    /// confirm overwrite and by name resolution.</summary>
    public bool Exists(string name)
        => File.Exists(ResolvePath(name)) || IsBuiltIn(name);

    /// <summary>True when a layout with this name ships with the app
    /// (whether or not a user save currently shadows it).</summary>
    public bool IsBuiltIn(string name)
    {
        var p = ResolveBuiltinPath(name);
        return p is not null && File.Exists(p);
    }

    /// <summary>True when the user has their own saved file for this name
    /// (as opposed to only the shipped built-in).</summary>
    public bool HasUserCopy(string name) => File.Exists(ResolvePath(name));

    private string ResolvePath(string name)
    {
        var safe = Sanitize(name);
        return Path.Combine(_dir, safe + ".json");
    }

    private string? ResolveBuiltinPath(string name)
    {
        if (_builtinDir is null) return null;
        var safe = Sanitize(name);
        return Path.Combine(_builtinDir, safe + ".json");
    }

    /// <summary>Sanitise a user-supplied name to a filesystem-safe
    /// basename. Invalid characters get replaced with underscores;
    /// the result is trimmed. Empty results are NOT defaulted here —
    /// callers should validate before reaching this method.</summary>
    private static string Sanitize(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }
}
