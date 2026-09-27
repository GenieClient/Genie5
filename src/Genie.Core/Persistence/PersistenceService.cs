using System.Text.Json;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Layout;
using Genie.Core.Macros;
using Genie.Core.Presets;
using Genie.Core.Shunts;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;

namespace Genie.Core.Persistence;

public sealed class PersistenceService
{
    // Writes: indented, regex metacharacters left literal (see
    // PersistenceJsonContext.Write). Source-generated metadata, #287.
    private readonly JsonSerializerOptions _options = PersistenceJsonContext.Write;

    public void SaveAliases(string path, IEnumerable<AliasRule> aliases)
    {
        var data = aliases.Select(a => new AliasPersistenceModel
        {
            Name = a.Name,
            Expansion = a.Expansion,
            IsEnabled = a.IsEnabled
        });

        File.WriteAllText(path, JsonSerializer.Serialize(data, _options));
    }

    public List<AliasPersistenceModel> LoadAliases(string path)
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<List<AliasPersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new();
    }

    public void SaveTriggers(string path, IEnumerable<TriggerRule> triggers)
    {
        var data = triggers.Select(t => new TriggerPersistenceModel
        {
            Pattern = t.Pattern,
            Action = t.Action,
            CaseSensitive = t.CaseSensitive,
            IsEnabled = t.IsEnabled,
            ClassName = t.ClassName,
            SoundFile = t.SoundFile,
            Speak = t.Speak,
            Eval = t.Eval,
            MatchAll = t.MatchAll,
        });

        File.WriteAllText(path, JsonSerializer.Serialize(data, _options));
    }

    public List<TriggerPersistenceModel> LoadTriggers(string path)
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<List<TriggerPersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new();
    }

    public void SaveVariables(string path, VariableStore store)
        => SaveVariables(path, store.GetAll().Values);

    /// <summary>Write a subset of a store's variables — the #315 split save
    /// sends each config layer's rows to its own file.</summary>
    public void SaveVariables(string path, IEnumerable<VariableValue> variables)
    {
        var data = variables.Select(v => new VariablePersistenceModel
        {
            Name = v.Name,
            Value = v.Value,
            Scope = v.Scope.ToString()
        });

        File.WriteAllText(path, JsonSerializer.Serialize(data, _options));
    }

    public List<VariablePersistenceModel> LoadVariables(string path)
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<List<VariablePersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new();
    }

    public void SaveHighlights(string path, IEnumerable<HighlightRule> rules)
    {
        var data = rules.Select(r => new HighlightPersistenceModel
        {
            Pattern = r.Pattern,
            ForegroundColor = r.ForegroundColor,
            BackgroundColor = r.BackgroundColor,
            MatchType = r.MatchType.ToString(),
            CaseSensitive = r.CaseSensitive,
            IsEnabled = r.IsEnabled,
            ClassName = r.ClassName,
            SoundFile = r.SoundFile,
            Speak = r.Speak,
            Windows = r.Windows.ToList(),
        });
        File.WriteAllText(path, JsonSerializer.Serialize(data, _options));
    }

    public void SaveClasses(string path, ClassEngine engine) => SaveClasses(path, engine.GetAll());

    /// <summary>Write a subset of class states (the #315 split save). The
    /// built-in <c>default</c> class is never written.</summary>
    public void SaveClasses(string path, IEnumerable<KeyValuePair<string, bool>> classes)
    {
        var data = classes
            .Where(kv => !kv.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            .Select(kv => new ClassPersistenceModel { Name = kv.Key, IsActive = kv.Value });
        File.WriteAllText(path, JsonSerializer.Serialize(data, _options));
    }

    public List<ClassPersistenceModel> LoadClasses(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<ClassPersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new(); }
        catch { return new(); }
    }

    public List<HighlightPersistenceModel> LoadHighlights(string path)
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<List<HighlightPersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new();
    }

    public void SaveNames(string path, IEnumerable<NameRule> rules)
    {
        var data = rules.Select(r => new NamePersistenceModel
        {
            Name            = r.Name,
            ForegroundColor = r.ForegroundColor,
            BackgroundColor = r.BackgroundColor,
        });
        File.WriteAllText(path, JsonSerializer.Serialize(data, _options));
    }

    public List<NamePersistenceModel> LoadNames(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<NamePersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new(); }
        catch { return new(); }
    }

    public void SaveSubstitutes(string path, IEnumerable<SubstituteRule> rules)
    {
        var data = rules.Select(r => new SubstitutePersistenceModel
        {
            Pattern       = r.Pattern,
            Replacement   = r.Replacement,
            CaseSensitive = r.CaseSensitive,
            IsEnabled     = r.IsEnabled,
            ClassName     = r.ClassName,
            WholeWord     = r.WholeWord,
        });
        File.WriteAllText(path, JsonSerializer.Serialize(data, _options));
    }

    public List<SubstitutePersistenceModel> LoadSubstitutes(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<SubstitutePersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new(); }
        catch { return new(); }
    }

    public void SaveGags(string path, IEnumerable<GagRule> rules)
    {
        var data = rules.Select(r => new GagPersistenceModel
        {
            Pattern       = r.Pattern,
            CaseSensitive = r.CaseSensitive,
            IsEnabled     = r.IsEnabled,
            ClassName     = r.ClassName,
        });
        File.WriteAllText(path, JsonSerializer.Serialize(data, _options));
    }

    public List<GagPersistenceModel> LoadGags(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<GagPersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new(); }
        catch { return new(); }
    }

    public void SaveShunts(string path, IEnumerable<ShuntRule> rules)
    {
        var data = rules.Select(r => new ShuntPersistenceModel
        {
            Pattern       = r.Pattern,
            Window        = r.Window,
            Copy          = r.Copy,
            CaseSensitive = r.CaseSensitive,
            IsEnabled     = r.IsEnabled,
            ClassName     = r.ClassName,
        });
        File.WriteAllText(path, JsonSerializer.Serialize(data, _options));
    }

    public List<ShuntPersistenceModel> LoadShunts(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<ShuntPersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new(); }
        catch { return new(); }
    }

    public void SaveMacros(string path, IEnumerable<MacroRule> rules)
    {
        var data = rules.Select(r => new MacroPersistenceModel
        {
            Key    = r.Key,
            Action = r.Action,
        });
        File.WriteAllText(path, JsonSerializer.Serialize(data, _options));
    }

    public List<MacroPersistenceModel> LoadMacros(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<MacroPersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new(); }
        catch { return new(); }
    }

    public void SavePresets(string path, PresetEngine engine)
        => SavePresets(path, engine.Presets.Values);

    /// <summary>Subset overload for the #257 split-save: writes exactly the
    /// given rules (one scope's share of the engine).</summary>
    public void SavePresets(string path, IEnumerable<PresetRule> rules)
    {
        var data = rules.Select(r => new PresetPersistenceModel
        {
            Id              = r.Id,
            ForegroundColor = r.ForegroundColor,
            BackgroundColor = r.BackgroundColor,
            HighlightLine   = r.HighlightLine,
        });
        File.WriteAllText(path, JsonSerializer.Serialize(data, _options));
    }

    public List<PresetPersistenceModel> LoadPresets(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<PresetPersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new(); }
        catch { return new(); }
    }

    public void SaveLayout(string path, LayoutState state)
        => File.WriteAllText(path, JsonSerializer.Serialize(state, _options));

    public LayoutState? LoadLayout(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<LayoutState>(File.ReadAllText(path), PersistenceJsonContext.Read); }
        catch { return null; }
    }

    public void SaveWindowSettings(string path, WindowSettingsStore store)
        // Rows for dynamic windows that have not opened this session (#156)
        // ride along verbatim (ScopedRows includes them) so a save cannot drop them.
        => SaveWindowSettings(path, store.ScopedRows().Select(r => r.Row));

    public void SaveWindowSettings(string path, IEnumerable<WindowSettingsPersistenceModel> rows)
        => File.WriteAllText(path, JsonSerializer.Serialize(rows, _options));

    /// <summary>
    /// The #315 split save for <c>windows.json</c>: Character rows to the
    /// profile file, Global rows to the shared one. The global side is the
    /// store's Global rows merged with every on-disk global row the store no
    /// longer carries at Global scope — a per-character override replaces its
    /// global twin in the store, so writing the store alone would drop the
    /// shared row. A scope's file is only created when it has rows (an
    /// existing file always rewrites). <paramref name="profileDir"/> equal to
    /// <paramref name="globalDir"/> = one layer, everything to the one file.
    /// </summary>
    public void SaveWindowSettingsSplit(WindowSettingsStore store, string profileDir, string globalDir)
    {
        var rows = store.ScopedRows().ToList();
        if (ScopedRuleLoader.SameDirectory(profileDir, globalDir))
        {
            SaveWindowSettings(Path.Combine(globalDir, "windows.json"), rows.Select(r => r.Row));
            return;
        }
        var profilePath = Path.Combine(profileDir, "windows.json");
        var globalPath  = Path.Combine(globalDir,  "windows.json");
        var character   = rows.Where(r => r.Scope == RuleScope.Character).Select(r => r.Row).ToList();
        var global      = ScopedRuleLoader.MergeGlobalForSave(
            rows.Where(r => r.Scope == RuleScope.Global).Select(r => r.Row),
            LoadWindowSettings(globalPath), r => r.Id, Array.Empty<string>());
        // A character row shadows (not deletes) its global twin: keep the twin.
        if (character.Count > 0 || File.Exists(profilePath)) SaveWindowSettings(profilePath, character);
        if (global.Count    > 0 || File.Exists(globalPath))  SaveWindowSettings(globalPath,  global);
    }

    public List<WindowSettingsPersistenceModel> LoadWindowSettings(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<List<WindowSettingsPersistenceModel>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new(); }
        catch { return new(); }
    }

    // ── Client state ────────────────────────────────────────────────────────

    public void SaveClientState(string path, ClientState state)
        => File.WriteAllText(path, JsonSerializer.Serialize(state, _options));

    public ClientState LoadClientState(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<ClientState>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new(); }
        catch { return new(); }
    }
}
