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
/// <para>Semantics: the file on disk becomes the truth for its rule type.
/// Parse FIRST — a torn or corrupt file throws before anything is cleared, so
/// the current rules survive a bad save. On success the engine is cleared and
/// rebuilt from the file (so deletions apply), and a coexisting profile
/// <c>.cfg</c> is rewritten from the engine: the connect sequence replays
/// <c>.cfg</c> AFTER the <c>.json</c> load and its loaders clear first, so
/// without the rewrite a stale <c>.cfg</c> would silently revert the edit at
/// the next connect (the same dual-write rule the Configuration panels
/// follow). Deliberately NOT via <see cref="PersistenceService"/>'s loaders:
/// three of those swallow parse errors into an empty list, which here would
/// wipe an engine over a half-written file.</para>
/// </summary>
public static class RuleFileLiveReload
{
    /// <summary>
    /// Reload <paramref name="fileName"/> into its engine, resolving the file
    /// with profile-over-global precedence (<paramref name="profileDir"/>'s
    /// copy wins; neither existing — e.g. the file was deleted — clears the
    /// engine). Engines left null are skipped. Returns the number of entries
    /// loaded. Throws <see cref="JsonException"/> (or an IO exception) on an
    /// unreadable file — nothing is cleared in that case — and
    /// <see cref="ArgumentException"/> for a file name that isn't a watched
    /// rule file.
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
        var profilePath = Path.Combine(profileDir, fileName);
        var globalPath  = Path.Combine(globalDir, fileName);
        var path        = File.Exists(profilePath) ? profilePath
                        : File.Exists(globalPath)  ? globalPath : null;

        switch (fileName.ToLowerInvariant())
        {
            case "highlights.json":
            {
                if (highlights is null) return 0;
                var models = Parse<HighlightPersistenceModel>(path);
                highlights.Clear();
                foreach (var m in models)
                    highlights.AddRule(
                        m.Pattern, m.ForegroundColor, m.BackgroundColor,
                        Enum.TryParse<HighlightMatchType>(m.MatchType, out var mt) ? mt : HighlightMatchType.String,
                        m.CaseSensitive, m.IsEnabled, m.ClassName, m.SoundFile, m.Speak, m.Windows);
                SyncCfg(profileDir, "highlights.cfg", () => CfgFormat.HighlightLines(highlights.Rules));
                return models.Count;
            }
            case "triggers.json":
            {
                if (triggers is null) return 0;
                var models = Parse<TriggerPersistenceModel>(path);
                triggers.Clear();
                foreach (var m in models)
                    triggers.AddTrigger(m.Pattern, m.Action, m.CaseSensitive, m.IsEnabled, m.ClassName,
                                        m.SoundFile, m.Speak, m.Eval, m.MatchAll);
                SyncCfg(profileDir, "triggers.cfg", () => CfgFormat.TriggerLines(triggers.Triggers));
                return models.Count;
            }
            case "substitutes.json":
            {
                if (substitutes is null) return 0;
                var models = Parse<SubstitutePersistenceModel>(path);
                substitutes.Clear();
                foreach (var m in models)
                    substitutes.AddRule(m.Pattern, m.Replacement, m.CaseSensitive, m.IsEnabled, m.ClassName);
                SyncCfg(profileDir, "substitutes.cfg", () => CfgFormat.SubstituteLines(substitutes.Rules));
                return models.Count;
            }
            case "gags.json":
            {
                if (gags is null) return 0;
                var models = Parse<GagPersistenceModel>(path);
                gags.Clear();
                foreach (var m in models)
                    gags.AddRule(m.Pattern, m.CaseSensitive, m.IsEnabled, m.ClassName);
                SyncCfg(profileDir, "gags.cfg", () => CfgFormat.GagLines(gags.Rules));
                return models.Count;
            }
            case "aliases.json":
            {
                if (aliases is null) return 0;
                var models = Parse<AliasPersistenceModel>(path);
                aliases.Clear();
                foreach (var m in models)
                    aliases.AddAlias(m.Name, m.Expansion, m.IsEnabled);
                SyncCfg(profileDir, "aliases.cfg", () => CfgFormat.AliasLines(aliases.Aliases));
                return models.Count;
            }
            case "variables.json":
            {
                if (variables is null) return 0;
                var models = Parse<VariablePersistenceModel>(path);
                variables.ClearUserVariables();   // system/reserved globals persist, as at connect
                foreach (var m in models)
                    variables.Set(m.Name, m.Value);
                SyncCfg(profileDir, "variables.cfg", () => CfgFormat.VariableLines(variables));
                return models.Count;
            }
            case "classes.json":
            {
                if (classes is null) return 0;
                var models = Parse<ClassPersistenceModel>(path);
                classes.Clear();
                foreach (var m in models)
                    classes.Set(m.Name, m.IsActive);
                SyncCfg(profileDir, "classes.cfg", () => CfgFormat.ClassLines(classes.GetAll()));
                return models.Count;
            }
            default:
                throw new ArgumentException($"Not a live-reload rule file: {fileName}", nameof(fileName));
        }
    }

    /// <summary>Strict parse: a null path (file deleted) is an empty set; a
    /// corrupt file throws before the caller clears anything.</summary>
    private static List<T> Parse<T>(string? path) =>
        path is null
            ? new List<T>()
            : JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? new List<T>();

    /// <summary>Rewrite a coexisting Genie 4-style .cfg from the just-reloaded
    /// engine. Only rewrites a file that already exists — json-only profiles
    /// never get a .cfg forked for them (same rule as the panels' SyncCfg).</summary>
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
