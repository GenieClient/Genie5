using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Macros;
using Genie.Core.Shunts;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;

namespace Genie.Core.Persistence;

/// <summary>
/// The shared #257 load machinery for the nine cfg-capable rule types
/// (shunts joined the set with public #248).
/// <see cref="BuildEffectiveScope"/> resolves ONE directory's effective set —
/// its <c>.json</c> content with any coexisting <c>.cfg</c> replayed on top
/// through the real loaders (the .cfg stays the persisted truth for its dir,
/// exactly as the pre-#257 single-dir chain behaved). <see cref="ApplyLayered"/>
/// then merges a Global scope and an optional Character scope into target
/// engines with Character-over-Global precedence, tagging every applied rule
/// with its <see cref="RuleScope"/> so saves can split back to the right file.
/// Used by the App's connect-time load AND the Configuration dialog's draft
/// engines, so both always agree.
/// </summary>
public static class LayeredRuleLoad
{
    /// <summary>One directory's effective rule engines (json + cfg-over-json).</summary>
    public sealed record EffectiveScope(
        HighlightEngine     Highlights,
        TriggerEngineFinal  Triggers,
        SubstituteEngine    Substitutes,
        GagEngine           Gags,
        AliasEngine         Aliases,
        MacroEngine         Macros,
        ClassEngine         Classes,
        VariableStore       Variables,
        ShuntEngine         Shunts);

    /// <summary>
    /// Load <paramref name="dir"/>'s effective set into fresh scratch engines:
    /// tolerant .json parses first (corrupt JSON yields that type empty rather
    /// than failing the whole load), then <see cref="CfgReplay.LoadInto"/>,
    /// which only touches a type whose <c>.cfg</c> exists in the dir and whose
    /// loaders clear-then-replay — so a dir carrying a .cfg yields that .cfg's
    /// content, else the .json's.
    /// </summary>
    public static EffectiveScope BuildEffectiveScope(string dir, PersistenceService p)
    {
        var s = new EffectiveScope(
            new HighlightEngine(), new TriggerEngineFinal(), new SubstituteEngine(),
            new GagEngine(), new AliasEngine(), new MacroEngine(),
            new ClassEngine(), new VariableStore(), new ShuntEngine());

        try { foreach (var m in p.LoadClasses(Path.Combine(dir, "classes.json"))) s.Classes.Set(m.Name, m.IsActive); } catch { }
        try
        {
            foreach (var m in p.LoadHighlights(Path.Combine(dir, "highlights.json")))
            {
                s.Highlights.RemoveRule(m.Pattern);
                s.Highlights.AddRule(m.Pattern, m.ForegroundColor, m.BackgroundColor,
                    Enum.TryParse<HighlightMatchType>(m.MatchType, out var mt) ? mt : HighlightMatchType.String,
                    m.CaseSensitive, m.IsEnabled, m.ClassName, m.SoundFile, m.Speak, m.Windows);
            }
        }
        catch { }
        try
        {
            foreach (var m in p.LoadTriggers(Path.Combine(dir, "triggers.json")))
            {
                s.Triggers.RemoveTrigger(m.Pattern);
                s.Triggers.AddTrigger(m.Pattern, m.Action, m.CaseSensitive, m.IsEnabled, m.ClassName,
                                      m.SoundFile, m.Speak, m.Eval, m.MatchAll);
            }
        }
        catch { }
        try
        {
            foreach (var m in p.LoadSubstitutes(Path.Combine(dir, "substitutes.json")))
            {
                s.Substitutes.RemoveRule(m.Pattern);
                s.Substitutes.AddRule(m.Pattern, m.Replacement, m.CaseSensitive, m.IsEnabled, m.ClassName, m.WholeWord);
            }
        }
        catch { }
        try
        {
            foreach (var m in p.LoadGags(Path.Combine(dir, "gags.json")))
            {
                s.Gags.RemoveRule(m.Pattern);
                s.Gags.AddRule(m.Pattern, m.CaseSensitive, m.IsEnabled, m.ClassName);
            }
        }
        catch { }
        try
        {
            foreach (var m in p.LoadAliases(Path.Combine(dir, "aliases.json")))
            {
                s.Aliases.RemoveAlias(m.Name);
                s.Aliases.AddAlias(m.Name, m.Expansion, m.IsEnabled);
            }
        }
        catch { }
        try
        {
            foreach (var m in p.LoadShunts(Path.Combine(dir, "shunts.json")))
            {
                s.Shunts.RemoveRule(m.Pattern);
                s.Shunts.AddRule(m.Pattern, m.Window, m.Copy, m.CaseSensitive, m.IsEnabled, m.ClassName);
            }
        }
        catch { }
        try { foreach (var m in p.LoadMacros(Path.Combine(dir, "macros.json"))) s.Macros.Add(m.Key, m.Action); } catch { }
        try { foreach (var m in p.LoadVariables(Path.Combine(dir, "variables.json"))) s.Variables.Set(m.Name, m.Value); } catch { }
        try
        {
            CfgReplay.LoadInto(dir, classes: s.Classes, aliases: s.Aliases, variables: s.Variables,
                               highlights: s.Highlights, triggers: s.Triggers,
                               substitutes: s.Substitutes, gags: s.Gags, macros: s.Macros,
                               shunts: s.Shunts);
        }
        catch { /* a corrupt .cfg leaves the json view standing */ }
        return s;
    }

    /// <summary>
    /// Merge scopes into the target engines (null targets are skipped —
    /// callers pull only the types they need). <paramref name="character"/>
    /// null = single-layer (profile-less): the global set applies tagged
    /// <see cref="RuleScope.Global"/>. Pattern/alias/macro engines layer by
    /// key with Character first (first-match-wins order is load-bearing);
    /// classes and variables are upsert stores — global first, character
    /// values override. Targets are ADDED TO, not cleared: the connect path
    /// clears on a character switch itself, and the first offline→connect
    /// load must land on top of a logon script's runtime setup (issue #88).
    /// </summary>
    public static void ApplyLayered(
        EffectiveScope      global,
        EffectiveScope?     character,
        HighlightEngine?    highlights  = null,
        TriggerEngineFinal? triggers    = null,
        SubstituteEngine?   substitutes = null,
        GagEngine?          gags        = null,
        AliasEngine?        aliases     = null,
        MacroEngine?        macros      = null,
        ClassEngine?        classes     = null,
        VariableStore?      variables   = null,
        ShuntEngine?        shunts      = null)
    {
        // Upsert stores tag each key with the layer that last wrote it (public #315),
        // so a profile value overriding a global one saves back to the profile.
        if (classes is not null)
        {
            foreach (var kv in global.Classes.GetAll())
            {
                classes.Set(kv.Key, kv.Value);
                classes.SetScope(kv.Key, RuleScope.Global);
            }
            if (character is not null)
                foreach (var kv in character.Classes.GetAll())
                {
                    classes.Set(kv.Key, kv.Value);
                    classes.SetScope(kv.Key, RuleScope.Character);
                }
        }

        if (highlights is not null)
            foreach (var (r, scope) in ScopedRuleLoader.Layer(
                (IEnumerable<HighlightRule>?)character?.Highlights.Rules ?? Array.Empty<HighlightRule>(),
                global.Highlights.Rules, x => x.Pattern))
            {
                highlights.RemoveRule(r.Pattern);
                highlights.AddRule(r.Pattern, r.ForegroundColor, r.BackgroundColor, r.MatchType,
                                   r.CaseSensitive, r.IsEnabled, r.ClassName, r.SoundFile, r.Speak,
                                   r.Windows).Scope = scope;
            }

        if (triggers is not null)
            foreach (var (r, scope) in ScopedRuleLoader.Layer(
                (IEnumerable<TriggerRule>?)character?.Triggers.Triggers ?? Array.Empty<TriggerRule>(),
                global.Triggers.Triggers, x => x.Pattern))
            {
                triggers.RemoveTrigger(r.Pattern);
                triggers.AddTrigger(r.Pattern, r.Action, r.CaseSensitive, r.IsEnabled, r.ClassName,
                                    r.SoundFile, r.Speak, r.Eval, r.MatchAll).Scope = scope;
            }

        if (substitutes is not null)
            foreach (var (r, scope) in ScopedRuleLoader.Layer(
                (IEnumerable<SubstituteRule>?)character?.Substitutes.Rules ?? Array.Empty<SubstituteRule>(),
                global.Substitutes.Rules, x => x.Pattern))
            {
                substitutes.RemoveRule(r.Pattern);
                substitutes.AddRule(r.Pattern, r.Replacement, r.CaseSensitive, r.IsEnabled, r.ClassName, r.WholeWord).Scope = scope;
            }

        if (gags is not null)
            foreach (var (r, scope) in ScopedRuleLoader.Layer(
                (IEnumerable<GagRule>?)character?.Gags.Rules ?? Array.Empty<GagRule>(),
                global.Gags.Rules, x => x.Pattern))
            {
                gags.RemoveRule(r.Pattern);
                gags.AddRule(r.Pattern, r.CaseSensitive, r.IsEnabled, r.ClassName).Scope = scope;
            }

        if (shunts is not null)
            foreach (var (r, scope) in ScopedRuleLoader.Layer(
                (IEnumerable<ShuntRule>?)character?.Shunts.Rules ?? Array.Empty<ShuntRule>(),
                global.Shunts.Rules, x => x.Pattern))
            {
                shunts.RemoveRule(r.Pattern);
                shunts.AddRule(r.Pattern, r.Window, r.Copy, r.CaseSensitive, r.IsEnabled, r.ClassName).Scope = scope;
            }

        if (aliases is not null)
            foreach (var (r, scope) in ScopedRuleLoader.Layer(
                (IEnumerable<AliasRule>?)character?.Aliases.Aliases ?? Array.Empty<AliasRule>(),
                global.Aliases.Aliases, x => x.Name))
            {
                aliases.RemoveAlias(r.Name);
                aliases.AddAlias(r.Name, r.Expansion, r.IsEnabled, r.ClassName).Scope = scope;
            }

        if (macros is not null)
            foreach (var (r, scope) in ScopedRuleLoader.Layer(
                (IEnumerable<MacroRule>?)character?.Macros.Rules ?? Array.Empty<MacroRule>(),
                global.Macros.Rules, x => x.Key))
            {
                macros.Add(r.Key, r.Action, r.ClassName);
                var added = macros.Rules.FirstOrDefault(
                    x => x.Key.Equals(r.Key, StringComparison.OrdinalIgnoreCase));
                if (added is not null) added.Scope = scope;
            }

        if (variables is not null)
        {
            foreach (var kv in global.Variables.GetAll())
                if (variables.Set(kv.Key, kv.Value.Value)) variables.SetConfigScope(kv.Key, RuleScope.Global);
            if (character is not null)
                foreach (var kv in character.Variables.GetAll())
                    if (variables.Set(kv.Key, kv.Value.Value)) variables.SetConfigScope(kv.Key, RuleScope.Character);
        }
    }

    /// <summary>
    /// Put a shared rule back into a live (or draft) engine after its
    /// this-character override was deleted (public #315). The override had
    /// shadowed the twin OUT of the engine, so without this the shared rule
    /// only reappeared at the next connect. The twin comes from the on-disk
    /// <paramref name="global"/> scope and is tagged Global; it is appended
    /// (the pattern engines keep Character rules first, so it lands among the
    /// shared ones — the exact file order returns at the next connect).
    /// Returns false when there is no twin, or the engine already carries
    /// <paramref name="key"/> (nothing to restore). Only the target engine
    /// for <paramref name="fileName"/> is consulted; null targets are skipped.
    /// </summary>
    public static bool RestoreGlobalTwin(
        EffectiveScope      global,
        string              fileName,
        string              key,
        HighlightEngine?    highlights  = null,
        TriggerEngineFinal? triggers    = null,
        SubstituteEngine?   substitutes = null,
        GagEngine?          gags        = null,
        AliasEngine?        aliases     = null,
        MacroEngine?        macros      = null,
        ClassEngine?        classes     = null,
        VariableStore?      variables   = null)
    {
        bool Same(string? a) => string.Equals(a, key, StringComparison.OrdinalIgnoreCase);

        switch (fileName.ToLowerInvariant())
        {
            case "highlights.json":
            {
                if (highlights is null || highlights.Rules.Any(r => Same(r.Pattern))) return false;
                var r = global.Highlights.Rules.FirstOrDefault(x => Same(x.Pattern));
                if (r is null) return false;
                highlights.AddRule(r.Pattern, r.ForegroundColor, r.BackgroundColor, r.MatchType,
                                   r.CaseSensitive, r.IsEnabled, r.ClassName, r.SoundFile, r.Speak,
                                   r.Windows).Scope = RuleScope.Global;
                return true;
            }
            case "triggers.json":
            {
                if (triggers is null || triggers.Triggers.Any(r => Same(r.Pattern))) return false;
                var r = global.Triggers.Triggers.FirstOrDefault(x => Same(x.Pattern));
                if (r is null) return false;
                triggers.AddTrigger(r.Pattern, r.Action, r.CaseSensitive, r.IsEnabled, r.ClassName,
                                    r.SoundFile, r.Speak, r.Eval, r.MatchAll).Scope = RuleScope.Global;
                return true;
            }
            case "substitutes.json":
            {
                if (substitutes is null || substitutes.Rules.Any(r => Same(r.Pattern))) return false;
                var r = global.Substitutes.Rules.FirstOrDefault(x => Same(x.Pattern));
                if (r is null) return false;
                substitutes.AddRule(r.Pattern, r.Replacement, r.CaseSensitive, r.IsEnabled, r.ClassName,
                                    r.WholeWord).Scope = RuleScope.Global;
                return true;
            }
            case "gags.json":
            {
                if (gags is null || gags.Rules.Any(r => Same(r.Pattern))) return false;
                var r = global.Gags.Rules.FirstOrDefault(x => Same(x.Pattern));
                if (r is null) return false;
                gags.AddRule(r.Pattern, r.CaseSensitive, r.IsEnabled, r.ClassName).Scope = RuleScope.Global;
                return true;
            }
            case "aliases.json":
            {
                if (aliases is null || aliases.Aliases.Any(r => Same(r.Name))) return false;
                var r = global.Aliases.Aliases.FirstOrDefault(x => Same(x.Name));
                if (r is null) return false;
                aliases.AddAlias(r.Name, r.Expansion, r.IsEnabled, r.ClassName).Scope = RuleScope.Global;
                return true;
            }
            case "macros.json":
            {
                if (macros is null || macros.Rules.Any(r => Same(r.Key))) return false;
                var r = global.Macros.Rules.FirstOrDefault(x => Same(x.Key));
                if (r is null) return false;
                macros.Add(r.Key, r.Action, r.ClassName);
                if (macros.Rules.FirstOrDefault(x => Same(x.Key)) is { } added) added.Scope = RuleScope.Global;
                return true;
            }
            case "classes.json":
            {
                if (classes is null || classes.GetAll().ContainsKey(key)) return false;
                if (!global.Classes.GetAll().TryGetValue(key, out var active)
                    || key.Equals("default", StringComparison.OrdinalIgnoreCase)) return false;
                classes.Set(key, active);
                classes.SetScope(key, RuleScope.Global);
                return true;
            }
            case "variables.json":
            {
                if (variables is null || variables.Get(key) is not null) return false;
                if (global.Variables.Get(key) is not { } value) return false;
                if (!variables.Set(key, value)) return false;
                variables.SetConfigScope(key, RuleScope.Global);
                return true;
            }
            default:
                return false;
        }
    }
}
