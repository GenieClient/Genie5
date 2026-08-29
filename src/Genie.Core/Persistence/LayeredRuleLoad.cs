using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Macros;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;

namespace Genie.Core.Persistence;

/// <summary>
/// The shared #257 load machinery for the eight cfg-capable rule types.
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
        VariableStore       Variables);

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
            new ClassEngine(), new VariableStore());

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
                s.Substitutes.AddRule(m.Pattern, m.Replacement, m.CaseSensitive, m.IsEnabled, m.ClassName);
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
        try { foreach (var m in p.LoadMacros(Path.Combine(dir, "macros.json"))) s.Macros.Add(m.Key, m.Action); } catch { }
        try { foreach (var m in p.LoadVariables(Path.Combine(dir, "variables.json"))) s.Variables.Set(m.Name, m.Value); } catch { }
        try
        {
            CfgReplay.LoadInto(dir, classes: s.Classes, aliases: s.Aliases, variables: s.Variables,
                               highlights: s.Highlights, triggers: s.Triggers,
                               substitutes: s.Substitutes, gags: s.Gags, macros: s.Macros);
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
        VariableStore?      variables   = null)
    {
        if (classes is not null)
        {
            foreach (var kv in global.Classes.GetAll()) classes.Set(kv.Key, kv.Value);
            if (character is not null)
                foreach (var kv in character.Classes.GetAll()) classes.Set(kv.Key, kv.Value);
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
                substitutes.AddRule(r.Pattern, r.Replacement, r.CaseSensitive, r.IsEnabled, r.ClassName).Scope = scope;
            }

        if (gags is not null)
            foreach (var (r, scope) in ScopedRuleLoader.Layer(
                (IEnumerable<GagRule>?)character?.Gags.Rules ?? Array.Empty<GagRule>(),
                global.Gags.Rules, x => x.Pattern))
            {
                gags.RemoveRule(r.Pattern);
                gags.AddRule(r.Pattern, r.CaseSensitive, r.IsEnabled, r.ClassName).Scope = scope;
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
            foreach (var kv in global.Variables.GetAll()) variables.Set(kv.Key, kv.Value.Value);
            if (character is not null)
                foreach (var kv in character.Variables.GetAll()) variables.Set(kv.Key, kv.Value.Value);
        }
    }
}
