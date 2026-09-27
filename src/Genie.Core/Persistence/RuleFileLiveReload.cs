using System.Text.Json;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Runtime;
using Genie.Core.Shunts;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;

namespace Genie.Core.Persistence;

/// <summary>
/// Applies an externally edited rule <c>.json</c> file (one of
/// <see cref="RuleFileWatcher.WatchedFiles"/>) to the live engines — the
/// reload half of the live-reload feature; <see cref="RuleFileWatcher"/> is
/// the detection half. Kept in Core (engine-parameterized, like
/// <see cref="CfgReplay"/>) so the whole watch→edit→reload path is testable
/// without the Avalonia host.
///
/// <para>Semantics (#257 two-layer): the PAIR of files on disk becomes the
/// truth for its rule type — the profile copy layered over the global copy,
/// Character-first with cross-layer key shadowing, exactly as the connect
/// load builds the set. Whichever copy changed, BOTH are re-read (so the
/// watcher never needs to say which dir fired). Parse FIRST, both layers — a
/// torn or corrupt file throws before anything is cleared, so the current
/// rules survive a bad save. On success the engine is cleared and rebuilt
/// layered (so deletions apply; deleting the profile copy falls back to the
/// global set, deleting both clears), and each dir's coexisting <c>.cfg</c>
/// is rewritten from ITS OWN scope's subset: the connect sequence treats a
/// dir's .cfg as its persisted truth, so a stale or cross-scope .cfg would
/// silently revert the edit at the next connect (the same split dual-write
/// rule the Configuration panels follow). Deliberately NOT via
/// <see cref="PersistenceService"/>'s loaders: three of those swallow parse
/// errors into an empty list, which here would wipe an engine over a
/// half-written file.</para>
/// </summary>
public static class RuleFileLiveReload
{
    /// <summary>
    /// Reload <paramref name="fileName"/> into its engine from BOTH config
    /// layers (<paramref name="profileDir"/> over <paramref name="globalDir"/>;
    /// the same dir twice = one layer). Engines left null are skipped.
    /// Returns the number of entries applied. Throws
    /// <see cref="JsonException"/> (or an IO exception) on an unreadable file
    /// — nothing is cleared in that case — and <see cref="ArgumentException"/>
    /// for a file name that isn't a watched rule file.
    /// </summary>
    public static int Reload(
        string              fileName,
        string              profileDir,
        string              globalDir,
        HighlightEngine?    highlights  = null,
        TriggerEngineFinal? triggers    = null,
        SubstituteEngine?   substitutes = null,
        GagEngine?          gags        = null,
        AliasEngine?        aliases     = null,
        VariableStore?      variables   = null,
        ClassEngine?        classes     = null,
        ShuntEngine?        shunts      = null)
    {
        var single = ScopedRuleLoader.SameDirectory(profileDir, globalDir);
        var (profilePath, globalPath) = ScopedRuleLoader.Paths(profileDir, globalDir, fileName);

        List<T> Character<T>() => single ? new List<T>() : Parse<T>(profilePath);
        List<T> Global<T>()    => Parse<T>(globalPath);

        switch (fileName.ToLowerInvariant())
        {
            case "highlights.json":
            {
                if (highlights is null) return 0;
                var character = Character<HighlightPersistenceModel>();
                var global    = Global<HighlightPersistenceModel>();
                static HighlightRule Add(HighlightEngine e, HighlightPersistenceModel m) =>
                    e.AddRule(m.Pattern, m.ForegroundColor, m.BackgroundColor,
                        Enum.TryParse<HighlightMatchType>(m.MatchType, out var mt) ? mt : HighlightMatchType.String,
                        m.CaseSensitive, m.IsEnabled, m.ClassName, m.SoundFile, m.Speak, m.Windows);
                highlights.Clear();
                var layered = ScopedRuleLoader.Layer(character, global, x => x.Pattern);
                foreach (var (m, scope) in layered) Add(highlights, m).Scope = scope;
                SyncScopedCfg(single, profileDir, globalDir, "highlights.cfg",
                    () => CfgFormat.HighlightLines(highlights.Rules),
                    () => CfgFormat.HighlightLines(highlights.Rules.Where(r => r.Scope == RuleScope.Character)),
                    () => { var g = new HighlightEngine(); foreach (var m in global) Add(g, m); return CfgFormat.HighlightLines(g.Rules); });
                return layered.Count;
            }
            case "triggers.json":
            {
                if (triggers is null) return 0;
                var character = Character<TriggerPersistenceModel>();
                var global    = Global<TriggerPersistenceModel>();
                static TriggerRule Add(TriggerEngineFinal e, TriggerPersistenceModel m) =>
                    e.AddTrigger(m.Pattern, m.Action, m.CaseSensitive, m.IsEnabled, m.ClassName,
                                 m.SoundFile, m.Speak, m.Eval, m.MatchAll);
                triggers.Clear();
                var layered = ScopedRuleLoader.Layer(character, global, x => x.Pattern);
                foreach (var (m, scope) in layered) Add(triggers, m).Scope = scope;
                SyncScopedCfg(single, profileDir, globalDir, "triggers.cfg",
                    () => CfgFormat.TriggerLines(triggers.Triggers),
                    () => CfgFormat.TriggerLines(triggers.Triggers.Where(r => r.Scope == RuleScope.Character)),
                    () => { var g = new TriggerEngineFinal(); foreach (var m in global) Add(g, m); return CfgFormat.TriggerLines(g.Triggers); });
                return layered.Count;
            }
            case "substitutes.json":
            {
                if (substitutes is null) return 0;
                var character = Character<SubstitutePersistenceModel>();
                var global    = Global<SubstitutePersistenceModel>();
                static SubstituteRule Add(SubstituteEngine e, SubstitutePersistenceModel m) =>
                    e.AddRule(m.Pattern, m.Replacement, m.CaseSensitive, m.IsEnabled, m.ClassName, m.WholeWord);
                substitutes.Clear();
                var layered = ScopedRuleLoader.Layer(character, global, x => x.Pattern);
                foreach (var (m, scope) in layered) Add(substitutes, m).Scope = scope;
                SyncScopedCfg(single, profileDir, globalDir, "substitutes.cfg",
                    () => CfgFormat.SubstituteLines(substitutes.Rules),
                    () => CfgFormat.SubstituteLines(substitutes.Rules.Where(r => r.Scope == RuleScope.Character)),
                    () => { var g = new SubstituteEngine(); foreach (var m in global) Add(g, m); return CfgFormat.SubstituteLines(g.Rules); });
                return layered.Count;
            }
            case "gags.json":
            {
                if (gags is null) return 0;
                var character = Character<GagPersistenceModel>();
                var global    = Global<GagPersistenceModel>();
                static GagRule Add(GagEngine e, GagPersistenceModel m) =>
                    e.AddRule(m.Pattern, m.CaseSensitive, m.IsEnabled, m.ClassName);
                gags.Clear();
                var layered = ScopedRuleLoader.Layer(character, global, x => x.Pattern);
                foreach (var (m, scope) in layered) Add(gags, m).Scope = scope;
                SyncScopedCfg(single, profileDir, globalDir, "gags.cfg",
                    () => CfgFormat.GagLines(gags.Rules),
                    () => CfgFormat.GagLines(gags.Rules.Where(r => r.Scope == RuleScope.Character)),
                    () => { var g = new GagEngine(); foreach (var m in global) Add(g, m); return CfgFormat.GagLines(g.Rules); });
                return layered.Count;
            }
            case "shunts.json":
            {
                if (shunts is null) return 0;
                var character = Character<ShuntPersistenceModel>();
                var global    = Global<ShuntPersistenceModel>();
                shunts.Clear();
                var layered = ScopedRuleLoader.Layer(character, global, x => x.Pattern);
                foreach (var (m, scope) in layered)
                    shunts.AddRule(m.Pattern, m.Window, m.Copy, m.CaseSensitive, m.IsEnabled, m.ClassName).Scope = scope;
                SyncScopedCfg(single, profileDir, globalDir, "shunts.cfg",
                    sc => CfgFormat.ShuntLines(shunts.Rules.Where(r => sc is null || r.Scope == sc)));
                return layered.Count;
            }
            case "aliases.json":
            {
                if (aliases is null) return 0;
                var character = Character<AliasPersistenceModel>();
                var global    = Global<AliasPersistenceModel>();
                static AliasRule Add(AliasEngine e, AliasPersistenceModel m) =>
                    e.AddAlias(m.Name, m.Expansion, m.IsEnabled);
                aliases.Clear();
                var layered = ScopedRuleLoader.Layer(character, global, x => x.Name);
                foreach (var (m, scope) in layered) Add(aliases, m).Scope = scope;
                SyncScopedCfg(single, profileDir, globalDir, "aliases.cfg",
                    () => CfgFormat.AliasLines(aliases.Aliases),
                    () => CfgFormat.AliasLines(aliases.Aliases.Where(r => r.Scope == RuleScope.Character)),
                    () => { var g = new AliasEngine(); foreach (var m in global) Add(g, m); return CfgFormat.AliasLines(g.Aliases); });
                return layered.Count;
            }
            case "variables.json":
            {
                if (variables is null) return 0;
                var character = Character<VariablePersistenceModel>();
                var global    = Global<VariablePersistenceModel>();
                variables.ClearUserVariables();   // system/reserved globals persist, as at connect
                // Upsert store: the profile value wins, and each key is tagged
                // with the layer that wrote it (#315) so saves split correctly.
                foreach (var m in global)
                    if (variables.Set(m.Name, m.Value)) variables.SetConfigScope(m.Name, RuleScope.Global);
                foreach (var m in character)
                    if (variables.Set(m.Name, m.Value)) variables.SetConfigScope(m.Name, RuleScope.Character);
                SyncScopedCfg(single, profileDir, globalDir, "variables.cfg",
                    () => CfgFormat.VariableLines(variables),
                    () => CfgFormat.VariableLines(variables.GetAll().Values.Where(v => v.ConfigScope == RuleScope.Character)),
                    () => { var g = new VariableStore(); foreach (var m in global) g.Set(m.Name, m.Value); return CfgFormat.VariableLines(g); });
                return global.Count + character.Count;
            }
            case "classes.json":
            {
                if (classes is null) return 0;
                var character = Character<ClassPersistenceModel>();
                var global    = Global<ClassPersistenceModel>();
                classes.Clear();
                foreach (var m in global)    { classes.Set(m.Name, m.IsActive); classes.SetScope(m.Name, RuleScope.Global); }
                foreach (var m in character) { classes.Set(m.Name, m.IsActive); classes.SetScope(m.Name, RuleScope.Character); }
                SyncScopedCfg(single, profileDir, globalDir, "classes.cfg",
                    () => CfgFormat.ClassLines(classes.GetAll()),
                    () => CfgFormat.ClassLines(classes.GetAll().Where(kv => classes.ScopeOf(kv.Key) == RuleScope.Character)),
                    () => CfgFormat.ClassLines(global.Select(m => new KeyValuePair<string, bool>(m.Name, m.IsActive))));
                return global.Count + character.Count;
            }
            default:
                throw new ArgumentException($"Not a live-reload rule file: {fileName}", nameof(fileName));
        }
    }

    /// <summary>Strict parse: a missing file (deleted, or never created) is an
    /// empty layer; a corrupt file throws before the caller clears anything.</summary>
    private static List<T> Parse<T>(string path) =>
        !File.Exists(path)
            ? new List<T>()
            : JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), PersistenceJsonContext.Read) ?? new List<T>();

    /// <summary>Rewrite each dir's coexisting Genie 4-style .cfg from its own
    /// layer: single-layer = the whole set; otherwise the profile .cfg gets
    /// the engine's Character subset and the global .cfg the GLOBAL FILE's
    /// full content. Not the engine's Global subset: a character override
    /// shadows its global twin out of the engine, so deriving the global .cfg
    /// from the engine dropped every shadowed twin — and a dir's .cfg is its
    /// persisted truth at the next connect (#315). Only rewrites a file that
    /// already exists — json-only dirs never get a .cfg forked for them (same
    /// rule as the panels' SyncCfg).</summary>
    private static void SyncScopedCfg(bool single, string profileDir, string globalDir, string fileName,
                                      Func<IEnumerable<string>> all,
                                      Func<IEnumerable<string>> character,
                                      Func<IEnumerable<string>> globalFile)
    {
        if (single)
        {
            SyncCfg(profileDir, fileName, all);
            return;
        }
        SyncCfg(profileDir, fileName, character);
        SyncCfg(globalDir,  fileName, globalFile);
    }

    private static void SyncCfg(string dir, string fileName, Func<IEnumerable<string>> lines)
    {
        try
        {
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path)) ConfigPersistence.WriteLines(path, lines());
        }
        catch { /* best-effort — worst case the .cfg stays stale, as before */ }
    }
}
