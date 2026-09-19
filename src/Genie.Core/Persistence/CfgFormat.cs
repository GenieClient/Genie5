using Genie.Core.Aliases;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Macros;
using Genie.Core.Presets;
using Genie.Core.Runtime;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;

namespace Genie.Core.Persistence;

/// <summary>
/// The single authority for Genie 4's "one command per line" .cfg rule-file
/// format: each line is the #command you would type at the bar to recreate
/// the rule. <c>CommandEngine</c>'s #save handlers and the Genie 4 Import
/// dialog both emit through here, so a file named <c>*.cfg</c> always
/// contains cfg-format lines. (The import briefly serialized JSON into
/// .cfg-named files instead; the connect-time loader then replayed the JSON
/// line-by-line through the command pipeline — and every non-#command line
/// falls through to send-to-game. See the guarded loaders in CommandEngine.)
/// </summary>
public static class CfgFormat
{
    // Empty names are skipped — a nameless class entry emits "#class on",
    // which the loader reads as a list command, not an add.
    public static IEnumerable<string> ClassLines(IReadOnlyDictionary<string, bool> classes) =>
        classes
            .Where(kvp => kvp.Key.Length > 0 &&
                          !kvp.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            .Select(kvp => $"#class {kvp.Key} {(kvp.Value ? "on" : "off")}");

    public static IEnumerable<string> AliasLines(IEnumerable<AliasRule> aliases) =>
        aliases.Select(a =>
            $"#alias add {ConfigPersistence.FormatArg(a.Name)} {ConfigPersistence.FormatArg(a.Expansion)}");

    public static IEnumerable<string> VariableLines(VariableStore store) =>
        store.GetAll()
            .Where(kvp => kvp.Value.Scope == VariableScope.User)
            .Select(kvp => $"#var {ConfigPersistence.FormatArg(kvp.Key)} {ConfigPersistence.FormatArg(kvp.Value.Value)}");

    public static IEnumerable<string> HighlightLines(IEnumerable<HighlightRule> rules) =>
        rules.Select(r =>
            $"#highlight add {ConfigPersistence.FormatArg(r.Pattern)} {ConfigPersistence.FormatArg(r.ForegroundColor)} {ConfigPersistence.FormatArg(r.BackgroundColor)} {ConfigPersistence.FormatArg(r.MatchType.ToString())} {ConfigPersistence.FormatArg(r.ClassName)} {ConfigPersistence.FormatArg(r.SoundFile)} {ConfigPersistence.FormatArg(r.Speak)}");

    public static IEnumerable<string> TriggerLines(IEnumerable<TriggerRule> triggers) =>
        triggers.Select(t =>
            $"#trigger add {ConfigPersistence.FormatArg(t.Pattern)} {ConfigPersistence.FormatArg(t.Action)} {ConfigPersistence.FormatArg(t.ClassName)} {ConfigPersistence.FormatArg(t.SoundFile)} {ConfigPersistence.FormatArg(t.Speak)}{(t.Eval ? " eval" : "")}{(t.MatchAll ? " matchall" : "")}");

    public static IEnumerable<string> SubstituteLines(IEnumerable<SubstituteRule> rules) =>
        rules.Select(r =>
            $"#substitute add {ConfigPersistence.FormatArg(r.Pattern)} {ConfigPersistence.FormatArg(r.Replacement)} {ConfigPersistence.FormatArg(r.ClassName)}");

    public static IEnumerable<string> GagLines(IEnumerable<GagRule> rules) =>
        rules.Select(r =>
            $"#gag add {ConfigPersistence.FormatArg(r.Pattern)} {ConfigPersistence.FormatArg(r.ClassName)}");

    public static IEnumerable<string> MacroLines(IEnumerable<MacroRule> rules) =>
        rules.Select(m =>
            $"#macro add {ConfigPersistence.FormatArg(m.Key)} {ConfigPersistence.FormatArg(m.Action)}");

    /// <summary>
    /// Name-highlight rules as <c>#names add {name} {fg} {bg}</c> — the
    /// argument order <c>CommandEngine.HandleNames</c> actually parses
    /// (name first; Genie 4's own names.cfg puts the colours first).
    /// </summary>
    public static IEnumerable<string> NameLines(IEnumerable<NameRule> rules) =>
        rules.Where(n => n.Name.Length > 0)
             .Select(n =>
                 $"#names add {ConfigPersistence.FormatArg(n.Name)} {ConfigPersistence.FormatArg(n.ForegroundColor)} {ConfigPersistence.FormatArg(n.BackgroundColor)}");

    /// <summary>
    /// Preset colours as <c>#preset {id} {fg} {bg}</c>.
    /// </summary>
    /// <remarks>
    /// The whole palette is written, not just entries that differ from the
    /// defaults, so the file states the full intent rather than depending on
    /// what the built-in defaults happen to be in a later version.
    /// <para>
    /// ⚠ <c>HighlightLine</c> does not survive this round-trip: the
    /// <c>#preset</c> handler preserves the existing flag and has no argument
    /// to set it, so a preset imported with <c>{highlightline}</c> loses that
    /// bit when re-read from cfg. Fixing it needs a handler change, not a
    /// writer change.
    /// </para>
    /// </remarks>
    public static IEnumerable<string> PresetLines(IReadOnlyDictionary<string, PresetRule> presets) =>
        presets.Values
            .Where(p => p.Id.Length > 0)
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
                $"#preset {ConfigPersistence.FormatArg(p.Id)} {ConfigPersistence.FormatArg(p.ForegroundColor)} {ConfigPersistence.FormatArg(p.BackgroundColor)}");
}
