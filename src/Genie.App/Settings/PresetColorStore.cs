using System.IO;
using Genie.App.Highlighting;
using Genie.Core.Persistence;
using Genie.Core.Presets;

namespace Genie.App.Settings;

/// <summary>
/// Reads and writes individual preset colours for editors outside the Presets
/// tab — Display Settings and the theme editor (public #406). The
/// preset stays the ONE source of the colour: these editors change the preset
/// itself rather than keeping a second setting.
///
/// <para><b>Which layer.</b> Connected, an edit goes to the layer the preset is
/// in effect from — the character's <c>presets.json</c> when it carries an
/// override, else the shared one — like an edit on the Presets tab. Offline,
/// it reads and writes the shared (All characters) layer.</para>
///
/// <para>Only the edited entry is changed in the file; everything else in it is
/// kept as found.</para>
/// </summary>
public sealed class PresetColorStore
{
    private readonly PresetEngine        _engine;
    private readonly string              _globalDir;
    private readonly Func<string?>       _connectedProfileDir;
    private readonly PersistenceService  _persistence = new();

    /// <param name="engine">The live preset engine the renderer reads.</param>
    /// <param name="globalDir">The shared config dir (<c>Config/</c>).</param>
    /// <param name="connectedProfileDir">The connected character's config dir,
    /// or null when offline. Equal to <paramref name="globalDir"/> for a
    /// single-layer (profile-less) connection.</param>
    public PresetColorStore(PresetEngine engine, string globalDir, Func<string?> connectedProfileDir)
    {
        _engine              = engine;
        _globalDir           = globalDir;
        _connectedProfileDir = connectedProfileDir;
    }

    private bool Connected => _connectedProfileDir() is not null;

    /// <summary>The preset's effective foreground: the live engine's when
    /// connected; offline, the shared file's value or the stock default.</summary>
    public string GetForeground(string id)
    {
        if (Connected) return _engine.GetForeground(id);
        return DiskShared(id)?.ForegroundColor ?? new PresetEngine().GetForeground(id);
    }

    /// <summary>The preset's effective background ("" = none), read like
    /// <see cref="GetForeground"/>.</summary>
    public string GetBackground(string id)
    {
        if (Connected) return _engine.GetBackground(id);
        return DiskShared(id)?.BackgroundColor ?? new PresetEngine().GetBackground(id);
    }

    /// <summary>Foreground and background lookups for reading many presets at
    /// once (the map's palette), read like <see cref="GetForeground"/> /
    /// <see cref="GetBackground"/> but with the shared file loaded only once.</summary>
    public (Func<string, string> Foreground, Func<string, string> Background) Reader()
    {
        if (Connected) return (_engine.GetForeground, _engine.GetBackground);
        var shared = Load(GlobalPath)
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var stock = new PresetEngine();
        return (id => shared.TryGetValue(id, out var m) ? m.ForegroundColor : stock.GetForeground(id),
                id => shared.TryGetValue(id, out var m) ? m.BackgroundColor : stock.GetBackground(id));
    }

    private PresetPersistenceModel? DiskShared(string id) =>
        Load(GlobalPath).FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Show <paramref name="foreground"/> live without saving — for a
    /// dialog's preview. Undo by previewing the original value.</summary>
    public void Preview(string id, string foreground) => Preview(id, foreground, null);

    /// <summary>Show colours live without saving. A null colour is left as it is;
    /// an empty background means none.</summary>
    public void Preview(string id, string? foreground, string? background)
    {
        var rule = _engine.Get(id);
        _engine.Apply(new PresetRule
        {
            Id              = rule?.Id ?? id,
            ForegroundColor = foreground ?? rule?.ForegroundColor ?? "Default",
            BackgroundColor = background ?? rule?.BackgroundColor ?? string.Empty,
            HighlightLine   = rule?.HighlightLine ?? false,
            Scope           = rule?.Scope ?? RuleScope.Global,
        });
        UserHighlights.NotifyRulesChanged();   // repaint visible lines and bars
    }

    /// <summary>Set the preset's foreground live and save it to its layer's file.</summary>
    public void SetForeground(string id, string foreground) => Set(id, foreground, null);

    /// <summary>Set the preset's colours live and save them to its layer's file.
    /// A null colour is left as it is (live and on disk); an empty background
    /// means none.</summary>
    public void Set(string id, string? foreground, string? background)
    {
        var rule  = _engine.Get(id);
        var scope = Connected ? rule?.Scope ?? RuleScope.Global : RuleScope.Global;
        var profileDir = _connectedProfileDir();
        var path  = scope == RuleScope.Character && profileDir is not null
            ? Path.Combine(profileDir, "presets.json")
            : GlobalPath;

        // Offline, the live engine may still hold a character override from the
        // last session; only touch it when the shared value is what it shows.
        if (Connected || rule is null || rule.Scope == RuleScope.Global)
            Preview(id, foreground, background);

        var models = Load(path);
        var model  = models.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            model = new PresetPersistenceModel
            {
                Id              = rule?.Id ?? id,
                ForegroundColor = rule?.ForegroundColor ?? new PresetEngine().GetForeground(id),
                BackgroundColor = rule?.BackgroundColor ?? string.Empty,
                HighlightLine   = rule?.HighlightLine ?? false,
            };
            models.Add(model);
        }
        if (foreground is not null) model.ForegroundColor = foreground;
        if (background is not null) model.BackgroundColor = background;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RuleFileWatcher.MarkAppWrite(path);
            _persistence.SavePresets(path, models.Select(m => new PresetRule
            {
                Id = m.Id, ForegroundColor = m.ForegroundColor,
                BackgroundColor = m.BackgroundColor, HighlightLine = m.HighlightLine,
            }));
        }
        catch { /* best-effort, like the Presets tab's save */ }
    }

    private string GlobalPath => Path.Combine(_globalDir, "presets.json");

    private List<PresetPersistenceModel> Load(string path)
    {
        try { return File.Exists(path) ? _persistence.LoadPresets(path) : new(); }
        catch { return new(); }
    }
}
