using System;
using System.IO;
using System.Linq;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Highlights;
using Genie.Core.Layout;
using Genie.Core.Persistence;
using Genie.Core.Runtime;
using Genie.Core.Variables;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #315 — the follow-ups to the two-layer rule config. Classes,
/// variables and window settings now carry a config layer like the eight
/// rule types, so their saves split instead of writing the merged set to the
/// profile; a deleted per-character override can put its shared twin back
/// into the engine at once; and the live-reload .cfg sync no longer drops
/// shadowed shared rules from the global .cfg.
/// </summary>
public sealed class RuleScopeFollowupTests : IDisposable
{
    private readonly string _root;
    private readonly string _globalDir;
    private readonly string _profileDir;
    private readonly PersistenceService _p = new();

    public RuleScopeFollowupTests()
    {
        _root       = Path.Combine(Path.GetTempPath(), "g5-315-" + Guid.NewGuid().ToString("N"));
        _globalDir  = Path.Combine(_root, "Config");
        _profileDir = Path.Combine(_root, "Config", "Profiles", "Test-ACCT");
        Directory.CreateDirectory(_profileDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (LayeredRuleLoad.EffectiveScope Glob, LayeredRuleLoad.EffectiveScope Prof) Build()
        => (LayeredRuleLoad.BuildEffectiveScope(_globalDir, _p),
            LayeredRuleLoad.BuildEffectiveScope(_profileDir, _p));

    private void SaveVars(string dir, params (string Name, string Value)[] vars)
    {
        var s = new VariableStore();
        foreach (var (n, v) in vars) s.Set(n, v);
        _p.SaveVariables(Path.Combine(dir, "variables.json"), s);
    }

    private void SaveClasses(string dir, params (string Name, bool On)[] classes)
    {
        var e = new ClassEngine();
        foreach (var (n, on) in classes) e.Set(n, on);
        _p.SaveClasses(Path.Combine(dir, "classes.json"), e);
    }

    // ── Layer tags at load ─────────────────────────────────────────────────

    [Fact]
    public void LayeredLoad_TagsVariablesAndClassesWithTheLayerThatWroteThem()
    {
        SaveVars(_globalDir,  ("hunt", "rats"), ("home", "crossing"));
        SaveVars(_profileDir, ("hunt", "goblins"));
        SaveClasses(_globalDir,  ("combat", true), ("shared", true));
        SaveClasses(_profileDir, ("combat", false));
        var (g, c) = Build();

        var vars = new VariableStore();
        var cls  = new ClassEngine();
        LayeredRuleLoad.ApplyLayered(g, c, variables: vars, classes: cls);

        Assert.Equal(RuleScope.Character, vars.GetAll()["hunt"].ConfigScope);
        Assert.Equal(RuleScope.Global,    vars.GetAll()["home"].ConfigScope);
        Assert.Equal(RuleScope.Character, cls.ScopeOf("combat"));
        Assert.Equal(RuleScope.Global,    cls.ScopeOf("shared"));
    }

    // ── Runtime changes to shared values ───────────────────────────────────

    [Fact]
    public void ChangingASharedVariable_MakesItThisCharactersValue_ButAnUnchangedSetDoesNot()
    {
        var vars = new VariableStore();
        vars.Set("home", "crossing");
        vars.SetConfigScope("home", RuleScope.Global);

        vars.Set("home", "crossing");                                        // same value
        Assert.Equal(RuleScope.Global, vars.GetAll()["home"].ConfigScope);

        vars.Set("home", "riverhaven");                                      // #var / a script
        Assert.Equal(RuleScope.Character, vars.GetAll()["home"].ConfigScope);
    }

    [Fact]
    public void TogglingASharedClass_MakesItThisCharactersState()
    {
        var cls = new ClassEngine();
        cls.Set("combat", true);
        cls.SetScope("combat", RuleScope.Global);
        cls.Set("loot", true);
        cls.SetScope("loot", RuleScope.Global);

        cls.Set("combat", true);                                             // unchanged
        Assert.Equal(RuleScope.Global, cls.ScopeOf("combat"));
        cls.Set("combat", false);                                            // #class combat off
        Assert.Equal(RuleScope.Character, cls.ScopeOf("combat"));

        cls.DeactivateAll();                                                 // loot flips too
        Assert.Equal(RuleScope.Character, cls.ScopeOf("loot"));

        cls.Remove("loot");
        cls.Set("loot", true);                                               // re-added = new
        Assert.Equal(RuleScope.Character, cls.ScopeOf("loot"));
        cls.SetScope("default", RuleScope.Global);                           // never saved
        Assert.Equal(RuleScope.Character, cls.ScopeOf("default"));
    }

    // ── Twin resurrection ──────────────────────────────────────────────────

    [Fact]
    public void RestoreGlobalTwin_PutsTheShadowedSharedRuleBack()
    {
        _p.SaveAliases(Path.Combine(_globalDir,  "aliases.json"), new[] { new AliasRule("hb", "put health") });
        _p.SaveAliases(Path.Combine(_profileDir, "aliases.json"), new[] { new AliasRule("hb", "put perceive health") });
        var (g, c) = Build();
        var live = new AliasEngine();
        LayeredRuleLoad.ApplyLayered(g, c, aliases: live);
        Assert.Equal("put perceive health", Assert.Single(live.Aliases).Expansion);

        live.RemoveAlias("hb");                                              // delete the override
        Assert.True(LayeredRuleLoad.RestoreGlobalTwin(g, "aliases.json", "HB", aliases: live));

        var back = Assert.Single(live.Aliases);
        Assert.Equal("put health", back.Expansion);
        Assert.Equal(RuleScope.Global, back.Scope);
    }

    [Fact]
    public void RestoreGlobalTwin_NoTwinOrKeyStillPresent_ReturnsFalse()
    {
        var hi = new HighlightEngine();
        hi.AddRule("kill shot", "Red").Scope = RuleScope.Global;
        var g = new LayeredRuleLoad.EffectiveScope(hi, new(), new(), new(), new(), new(), new(), new());

        var live = new HighlightEngine();
        Assert.False(LayeredRuleLoad.RestoreGlobalTwin(g, "highlights.json", "no such rule", highlights: live));
        live.AddRule("kill shot", "Gold");
        Assert.False(LayeredRuleLoad.RestoreGlobalTwin(g, "highlights.json", "kill shot", highlights: live));
        Assert.Equal("Gold", Assert.Single(live.Rules).ForegroundColor);   // untouched
    }

    [Fact]
    public void RestoreGlobalTwin_VariablesAndClasses()
    {
        SaveVars(_globalDir,  ("hunt", "rats"));
        SaveVars(_profileDir, ("hunt", "goblins"));
        SaveClasses(_globalDir,  ("combat", true));
        SaveClasses(_profileDir, ("combat", false));
        var (g, c) = Build();
        var vars = new VariableStore();
        var cls  = new ClassEngine();
        LayeredRuleLoad.ApplyLayered(g, c, variables: vars, classes: cls);

        vars.Remove("hunt");
        cls.Remove("combat");
        Assert.True(LayeredRuleLoad.RestoreGlobalTwin(g, "variables.json", "hunt", variables: vars));
        Assert.True(LayeredRuleLoad.RestoreGlobalTwin(g, "classes.json",   "combat", classes: cls));

        Assert.Equal("rats", vars.Get("hunt"));
        Assert.Equal(RuleScope.Global, vars.GetAll()["hunt"].ConfigScope);
        Assert.True(cls.IsActive("combat"));
        Assert.Equal(RuleScope.Global, cls.ScopeOf("combat"));
    }

    // ── windows.json split ─────────────────────────────────────────────────

    private static WindowSettingsPersistenceModel Row(string id, double size) =>
        new() { Id = id, DisplayTitle = id, FontSize = size, Foreground = "Default", HasIfClosed = true };

    [Fact]
    public void WindowSettingsSplit_RoundTrip_KeepsEachRowInItsFile_AndTheShadowedSharedRow()
    {
        _p.SaveWindowSettings(Path.Combine(_globalDir,  "windows.json"), new[] { Row("talk", 14), Row("combat", 15) });
        _p.SaveWindowSettings(Path.Combine(_profileDir, "windows.json"), new[] { Row("talk", 20) });

        var store = new WindowSettingsStore();
        store.Register("talk", "Talk");
        store.Register("combat", "Combat");
        store.Register("thoughts", "Thoughts");                              // never saved anywhere
        foreach (var m in _p.LoadWindowSettings(Path.Combine(_globalDir, "windows.json")))  store.Apply(m, RuleScope.Global);
        foreach (var m in _p.LoadWindowSettings(Path.Combine(_profileDir, "windows.json"))) store.Apply(m, RuleScope.Character);

        Assert.Equal(20, store.Get("talk").FontSize);
        Assert.Equal(RuleScope.Character, store.Get("talk").Scope);
        Assert.Equal(RuleScope.Global,    store.Get("combat").Scope);
        Assert.Equal(RuleScope.Character, store.Get("thoughts").Scope);

        store.Get("combat").FontSize = 16;                                   // edit a shared window
        _p.SaveWindowSettingsSplit(store, _profileDir, _globalDir);

        var prof = _p.LoadWindowSettings(Path.Combine(_profileDir, "windows.json"));
        var glob = _p.LoadWindowSettings(Path.Combine(_globalDir,  "windows.json"));
        Assert.Equal(new[] { "talk", "thoughts" }, prof.Select(r => r.Id).OrderBy(x => x));
        Assert.Equal(20, prof.Single(r => r.Id == "talk").FontSize);
        Assert.Equal(new[] { "combat", "talk" }, glob.Select(r => r.Id).OrderBy(x => x));
        Assert.Equal(16, glob.Single(r => r.Id == "combat").FontSize);       // the edit went to the shared file
        Assert.Equal(14, glob.Single(r => r.Id == "talk").FontSize);         // shadowed twin kept
    }

    [Fact]
    public void WindowSettingsSplit_SingleLayer_WritesEverythingToTheOneFile()
    {
        var store = new WindowSettingsStore();
        store.Register("talk", "Talk");
        _p.SaveWindowSettingsSplit(store, _globalDir, _globalDir);
        Assert.Equal("talk", Assert.Single(_p.LoadWindowSettings(Path.Combine(_globalDir, "windows.json"))).Id);
    }

    [Fact]
    public void HeldRow_KeepsItsLayerUntilTheWindowRegisters()
    {
        var store = new WindowSettingsStore();
        store.Apply(Row("dialog-x", 18), RuleScope.Global);                  // server dialog not open yet
        Assert.Equal(RuleScope.Global, store.ScopedRows().Single().Scope);

        var s = store.Register("dialog-x", "Dialog X");
        Assert.Equal(18, s.FontSize);
        Assert.Equal(RuleScope.Global, s.Scope);
    }

    [Fact]
    public void DefaultsFor_DoesNotReplaceTheLiveInstance()
    {
        var store = new WindowSettingsStore();
        var live  = store.Register("room", "Room", "Segoe UI", 11);
        live.FontSize = 20;

        var defaults = store.DefaultsFor("room", "Room");

        Assert.Same(live, store.Get("room"));                                // open window keeps its instance
        Assert.Equal(11, defaults.FontSize);                                 // the registered font, not mono 13
        Assert.Equal("Segoe UI", defaults.FontFamily);
    }

    // ── Live reload ────────────────────────────────────────────────────────

    [Fact]
    public void LiveReload_TagsVariablesAndClasses_AndSplitsTheirCfg()
    {
        SaveVars(_globalDir,  ("hunt", "rats"), ("home", "crossing"));
        SaveVars(_profileDir, ("hunt", "goblins"));
        ConfigPersistence.WriteLines(Path.Combine(_profileDir, "variables.cfg"), new[] { "#var stale 1" });
        ConfigPersistence.WriteLines(Path.Combine(_globalDir,  "variables.cfg"), new[] { "#var stale 1" });

        var vars = new VariableStore();
        RuleFileLiveReload.Reload("variables.json", _profileDir, _globalDir, variables: vars);

        Assert.Equal(RuleScope.Character, vars.GetAll()["hunt"].ConfigScope);
        Assert.Equal(RuleScope.Global,    vars.GetAll()["home"].ConfigScope);
        var profCfg = File.ReadAllText(Path.Combine(_profileDir, "variables.cfg"));
        var globCfg = File.ReadAllText(Path.Combine(_globalDir,  "variables.cfg"));
        Assert.Contains("goblins", profCfg);
        Assert.DoesNotContain("crossing", profCfg);                          // no merged set in the profile
        Assert.Contains("crossing", globCfg);
        Assert.Contains("rats", globCfg);                                    // shadowed shared value kept
        Assert.DoesNotContain("goblins", globCfg);
    }

    [Fact]
    public void LiveReload_GlobalCfgKeepsASharedRuleThatACharacterOverrides()
    {
        // Before public #315, the reload wrote the global .cfg from the engine's Global
        // subset — which never holds a shadowed twin — so a profile override
        // silently deleted the shared rule from the global .cfg, the dir's
        // persisted truth at the next connect.
        _p.SaveHighlights(Path.Combine(_globalDir,  "highlights.json"),
            new[] { new HighlightRule("kill shot", "Red"), new HighlightRule("other", "Blue") });
        _p.SaveHighlights(Path.Combine(_profileDir, "highlights.json"),
            new[] { new HighlightRule("kill shot", "Gold") });
        ConfigPersistence.WriteLines(Path.Combine(_globalDir, "highlights.cfg"), Array.Empty<string>());

        var hi = new HighlightEngine();
        RuleFileLiveReload.Reload("highlights.json", _profileDir, _globalDir, highlights: hi);

        var globCfg = File.ReadAllText(Path.Combine(_globalDir, "highlights.cfg"));
        Assert.Contains("kill shot", globCfg);
        Assert.Contains("Red", globCfg);
        Assert.DoesNotContain("Gold", globCfg);
        Assert.Contains("other", globCfg);
    }
}
