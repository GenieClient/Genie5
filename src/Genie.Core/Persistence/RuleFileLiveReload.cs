using System.Text.Json;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Runtime;
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
        ClassEngine?        classes     = null)
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
                highlights.Clear();
                var layered = ScopedRuleLoader.Layer(character, global, x => x.Pattern);
                foreach (var (m, scope) in layered)
                    highlights.AddRule(
                        m.Pattern, m.ForegroundColor, m.BackgroundColor,
                        Enum.TryParse<HighlightMatchType>(m.MatchType, out var mt) ? mt : HighlightMatchType.String,
                        m.CaseSensitive, m.IsEnabled, m.ClassName, m.SoundFile, m.Speak, m.Windows).Scope = scope;
                SyncScopedCfg(single, profileDir, globalDir, "highlights.cfg",
                    sc => CfgFormat.HighlightLines(highlights.Rules.Where(r => sc is null || r.Scope == sc)));
                return layered.Count;
            }
            case "triggers.json":
            {
                if (triggers is null) return 0;
                var character = Character<TriggerPersistenceModel>();
                var global    = Global<TriggerPersistenceModel>();
                triggers.Clear();
                var layered = ScopedRuleLoader.Layer(character, global, x => x.Pattern);
                foreach (var (m, scope) in layered)
                    triggers.AddTrigger(m.Pattern, m.Action, m.CaseSensitive, m.IsEnabled, m.ClassName,
                                        m.SoundFile, m.Speak, m.Eval, m.MatchAll).Scope = scope;
                SyncScopedCfg(single, profileDir, globalDir, "triggers.cfg",
                    sc => CfgFormat.TriggerLines(triggers.Triggers.Where(r => sc is null || r.Scope == sc)));
                return layered.Count;
            }
            case "substitutes.json":
            {
                if (substitutes is null) return 0;
                var character = Character<SubstitutePersistenceModel>();
                var global    = Global<SubstitutePersistenceModel>();
                substitutes.Clear();
                var layered = ScopedRuleLoader.Layer(character, global, x => x.Pattern);
                foreach (var (m, scope) in layered)
                    substitutes.AddRule(m.Pattern, m.Replacement, m.CaseSensitive, m.IsEnabled, m.ClassName).Scope = scope;
                SyncScopedCfg(single, profileDir, globalDir, "substitutes.cfg",
                    sc => CfgFormat.SubstituteLines(substitutes.Rules.Where(r => sc is null || r.Scope == sc)));
                return layered.Count;
            }
            case "gags.json":
            {
                if (gags is null) return 0;
                var character = Character<GagPersistenceModel>();
                var global    = Global<GagPersistenceModel>();
                gags.Clear();
                var layered = ScopedRuleLoader.Layer(character, global, x => x.Pattern);
                foreach (var (m, scope) in layered)
                    gags.AddRule(m.Pattern, m.CaseSensitive, m.IsEnabled, m.ClassName).Scope = scope;
                SyncScopedCfg(single, profileDir, globalDir, "gags.cfg",
                    sc => CfgFormat.GagLines(gags.Rules.Where(r => sc is null || r.Scope == sc)));
                return layered.Count;
            }
            case "aliases.json":
            {
                if (aliases is null) return 0;
                var character = Character<AliasPersistenceModel>();
                var global    = Global<AliasPersistenceModel>();
                aliases.Clear();
                var layered = ScopedRuleLoader.Layer(character, global, x => x.Name);
                foreach (var (m, scope) in layered)
                    aliases.AddAlias(m.Name, m.Expansion, m.IsEnabled).Scope = scope;
                SyncScopedCfg(single, profileDir, globalDir, "aliases.cfg",
                    sc => CfgFormat.AliasLines(aliases.Aliases.Where(r => sc is null || r.Scope == sc)));
                return layered.Count;
            }
            case "variables.json":
            {
                if (variables is null) return 0;
                var character = Character<VariablePersistenceModel>();
                var global    = Global<VariablePersistenceModel>();
                variables.ClearUserVariables();   // system/reserved globals persist, as at connect
                foreach (var m in global)    variables.Set(m.Name, m.Value);   // upsert store:
                foreach (var m in character) variables.Set(m.Name, m.Value);   // profile value wins
                SyncCfg(profileDir, "variables.cfg", () => CfgFormat.VariableLines(variables));
                return global.Count + character.Count;
            }
            case "classes.json":
            {
                if (classes is null) return 0;
                var character = Character<ClassPersistenceModel>();
                var global    = Global<ClassPersistenceModel>();
                classes.Clear();
                foreach (var m in global)    classes.Set(m.Name, m.IsActive);
                foreach (var m in character) classes.Set(m.Name, m.IsActive);
                SyncCfg(profileDir, "classes.cfg", () => CfgFormat.ClassLines(classes.GetAll()));
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
            : JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? new List<T>();

    /// <summary>Rewrite each dir's coexisting Genie 4-style .cfg from its own
    /// scope's subset (null scope = single-layer, whole set). Only rewrites a
    /// file that already exists — json-only dirs never get a .cfg forked for
    /// them (same rule as the panels' SyncCfg).</summary>
    private static void SyncScopedCfg(bool single, string profileDir, string globalDir,
                                      string fileName, Func<RuleScope?, IEnumerable<string>> linesFor)
    {
        if (single)
        {
            SyncCfg(profileDir, fileName, () => linesFor(null));
            return;
        }
        SyncCfg(profileDir, fileName, () => linesFor(RuleScope.Character));
        SyncCfg(globalDir,  fileName, () => linesFor(RuleScope.Global));
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
