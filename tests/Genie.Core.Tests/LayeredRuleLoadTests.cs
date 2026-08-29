using System;
using System.IO;
using System.Linq;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Highlights;
using Genie.Core.Persistence;
using Genie.Core.Runtime;
using Genie.Core.Triggers;
using Genie.Core.Variables;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// End-to-end pins for the #257 layered load: per-dir effective sets
/// (json, with a coexisting .cfg as that dir's persisted truth), then
/// Character-over-Global layering with scope tags.
/// </summary>
public sealed class LayeredRuleLoadTests : IDisposable
{
    private readonly string _root;
    private readonly string _globalDir;
    private readonly string _profileDir;
    private readonly PersistenceService _p = new();

    public LayeredRuleLoadTests()
    {
        _root       = Path.Combine(Path.GetTempPath(), "g5-257-" + Guid.NewGuid().ToString("N"));
        _globalDir  = Path.Combine(_root, "Config");
        _profileDir = Path.Combine(_root, "Config", "Profiles", "Test-ACCT");
        Directory.CreateDirectory(_globalDir);
        Directory.CreateDirectory(_profileDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static HighlightRule Hi(string pattern, string color, bool enabled = true)
        => new(pattern, color, isEnabled: enabled);

    private void WriteHighlights(string dir, params HighlightRule[] rules)
        => _p.SaveHighlights(Path.Combine(dir, "highlights.json"), rules);

    private (LayeredRuleLoad.EffectiveScope Glob, LayeredRuleLoad.EffectiveScope Prof) Build()
        => (LayeredRuleLoad.BuildEffectiveScope(_globalDir, _p),
            LayeredRuleLoad.BuildEffectiveScope(_profileDir, _p));

    [Fact]
    public void GlobalOnly_AllRulesLoadTaggedGlobal()
    {
        WriteHighlights(_globalDir, Hi("shared one", "Red"), Hi("shared two", "Blue"));
        var (g, c) = Build();

        var target = new HighlightEngine();
        LayeredRuleLoad.ApplyLayered(g, c, highlights: target);

        Assert.Equal(2, target.Rules.Count);
        Assert.All(target.Rules, r => Assert.Equal(RuleScope.Global, r.Scope));
    }

    [Fact]
    public void ProfileRuleShadowsSameKeyGlobal_AndComesFirst()
    {
        WriteHighlights(_globalDir,  Hi("kill shot", "Red"), Hi("global only", "Gray"));
        WriteHighlights(_profileDir, Hi("kill shot", "Gold"));
        var (g, c) = Build();

        var target = new HighlightEngine();
        LayeredRuleLoad.ApplyLayered(g, c, highlights: target);

        Assert.Equal(2, target.Rules.Count);
        Assert.Equal("Gold", target.Rules[0].ForegroundColor);              // profile copy, first
        Assert.Equal(RuleScope.Character, target.Rules[0].Scope);
        Assert.Equal("global only", target.Rules[1].Pattern);
        Assert.Equal(RuleScope.Global, target.Rules[1].Scope);
    }

    [Fact]
    public void DisabledProfileCopy_IsThePerCharacterOptOut()
    {
        WriteHighlights(_globalDir,  Hi("noisy line", "Red"));
        WriteHighlights(_profileDir, Hi("noisy line", "Red", enabled: false));
        var (g, c) = Build();

        var target = new HighlightEngine();
        LayeredRuleLoad.ApplyLayered(g, c, highlights: target);

        var rule = Assert.Single(target.Rules);
        Assert.False(rule.IsEnabled);                                       // globally on, off for this character
        Assert.Equal(RuleScope.Character, rule.Scope);
    }

    [Fact]
    public void CoexistingCfg_IsTheDirsPersistedTruth()
    {
        // Global dir: json says A, cfg says B — the cfg replay clears and
        // rebuilds, so the effective global set is B (exactly the pre-#257
        // single-dir chain's behaviour, now applied per scope).
        WriteHighlights(_globalDir, Hi("json rule", "Red"));
        var cfgSource = new HighlightEngine();
        cfgSource.AddRule("cfg rule", "Lime");
        ConfigPersistence.WriteLines(Path.Combine(_globalDir, "highlights.cfg"),
                                     CfgFormat.HighlightLines(cfgSource.Rules));
        var (g, c) = Build();

        var target = new HighlightEngine();
        LayeredRuleLoad.ApplyLayered(g, c, highlights: target);

        var rule = Assert.Single(target.Rules);
        Assert.Equal("cfg rule", rule.Pattern);
        Assert.Equal(RuleScope.Global, rule.Scope);
    }

    [Fact]
    public void ProfileCfg_CannotWipeLayeredGlobals()
    {
        // The old connect chain's failure mode: a profile highlights.cfg
        // cleared the whole engine at replay, erasing every global rule.
        // Under per-scope effective sets it only defines the PROFILE layer.
        WriteHighlights(_globalDir, Hi("global stays", "Red"));
        var cfgSource = new HighlightEngine();
        cfgSource.AddRule("profile cfg rule", "Gold");
        ConfigPersistence.WriteLines(Path.Combine(_profileDir, "highlights.cfg"),
                                     CfgFormat.HighlightLines(cfgSource.Rules));
        var (g, c) = Build();

        var target = new HighlightEngine();
        LayeredRuleLoad.ApplyLayered(g, c, highlights: target);

        Assert.Equal(2, target.Rules.Count);
        Assert.Equal("profile cfg rule", target.Rules[0].Pattern);
        Assert.Equal("global stays",     target.Rules[1].Pattern);
    }

    [Fact]
    public void Aliases_LayerByName()
    {
        _p.SaveAliases(Path.Combine(_globalDir, "aliases.json"),
            new[] { new AliasRule("hb", "put health"), new AliasRule("gg", "put goodbye") });
        _p.SaveAliases(Path.Combine(_profileDir, "aliases.json"),
            new[] { new AliasRule("hb", "put perceive health") });
        var (g, c) = Build();

        var target = new AliasEngine();
        LayeredRuleLoad.ApplyLayered(g, c, aliases: target);

        Assert.Equal(2, target.Aliases.Count);
        Assert.Equal("put perceive health",
            target.Aliases.First(a => a.Name == "hb").Expansion);
        Assert.Equal(RuleScope.Character, target.Aliases.First(a => a.Name == "hb").Scope);
        Assert.Equal(RuleScope.Global,    target.Aliases.First(a => a.Name == "gg").Scope);
    }

    [Fact]
    public void VariablesAndClasses_ProfileValueOverridesGlobal()
    {
        var gv = new VariableStore(); gv.Set("hunt", "rats"); gv.Set("home", "crossing");
        _p.SaveVariables(Path.Combine(_globalDir, "variables.json"), gv);
        var pv = new VariableStore(); pv.Set("hunt", "goblins");
        _p.SaveVariables(Path.Combine(_profileDir, "variables.json"), pv);

        var gc = new ClassEngine(); gc.Set("combat", true);
        _p.SaveClasses(Path.Combine(_globalDir, "classes.json"), gc);
        var pc = new ClassEngine(); pc.Set("combat", false);
        _p.SaveClasses(Path.Combine(_profileDir, "classes.json"), pc);

        var (g, c) = Build();
        var vars = new VariableStore();
        var cls  = new ClassEngine();
        LayeredRuleLoad.ApplyLayered(g, c, variables: vars, classes: cls);

        Assert.Equal("goblins",  vars.Get("hunt"));       // profile wins
        Assert.Equal("crossing", vars.Get("home"));       // global passes through
        Assert.False(cls.IsActive("combat"));             // profile override
    }

    [Fact]
    public void SingleLayer_NullCharacter_TagsEverythingGlobal()
    {
        WriteHighlights(_globalDir, Hi("only layer", "Red"));
        var g = LayeredRuleLoad.BuildEffectiveScope(_globalDir, _p);

        var target = new HighlightEngine();
        LayeredRuleLoad.ApplyLayered(g, null, highlights: target);

        var rule = Assert.Single(target.Rules);
        Assert.Equal(RuleScope.Global, rule.Scope);
    }
}
