using System;
using System.IO;
using System.Linq;
using Genie.App.ViewModels;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Layout;
using Genie.Core.Persistence;
using Genie.Core.Profiles;
using Genie.Core.Runtime;
using Genie.Core.Variables;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #315 through the Configuration dialog's VM (draft engines, so no
/// live core is needed): Variables, Classes and Layout → Windows saves split
/// by layer like the other rule types, keep the shared twin a character value
/// shadows, rewrite each layer's coexisting .cfg from its own subset, and a
/// deleted per-character override brings its shared twin back at once.
/// </summary>
public sealed class ConfigurationScopeSplitTests : IDisposable
{
    private readonly string       _root;
    private readonly string       _configDir;
    private readonly string       _profileDir;
    private readonly ProfileStore _store = new();
    private readonly PersistenceService _p = new();

    public ConfigurationScopeSplitTests()
    {
        _root       = Path.Combine(Path.GetTempPath(), "genie_cfg315_" + Guid.NewGuid().ToString("N"));
        _configDir  = Path.Combine(_root, "Config");
        _profileDir = Path.Combine(_root, "Profiles", "Renucci-ACCT");
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(_profileDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ConfigurationViewModel Build(WindowSettingsStore? windows = null)
    {
        _store.Add("Renucci", "host", 11024, "ACCT", "", characterName: "Renucci");
        return new ConfigurationViewModel(
            core:               null,
            configRoot:         _configDir,
            profiles:           _store,
            connectedProfile:   null,                  // offline → draft engines
            windowSettings:     windows ?? new WindowSettingsStore(),
            profileDirResolver: p => p is null ? _configDir : _profileDir);
    }

    private string G(string f) => Path.Combine(_configDir, f);
    private string P(string f) => Path.Combine(_profileDir, f);

    private void SaveVars(string path, params (string Name, string Value)[] vars)
    {
        var s = new VariableStore();
        foreach (var (n, v) in vars) s.Set(n, v);
        _p.SaveVariables(path, s);
    }

    // ── Variables ──────────────────────────────────────────────────────────

    [Fact]
    public void Variables_SaveSplitsByLayer_AndKeepsTheShadowedSharedValue()
    {
        SaveVars(G("variables.json"), ("hunt", "rats"), ("home", "crossing"));
        SaveVars(P("variables.json"), ("hunt", "goblins"));
        var vm    = Build();
        var store = vm.VariableStore!;

        Assert.Equal("goblins", store.Get("hunt"));
        Assert.Equal(RuleScope.Character, store.GetAll()["hunt"].ConfigScope);
        Assert.Equal(RuleScope.Global,    store.GetAll()["home"].ConfigScope);

        store.Set("weapon", "sword");                                        // a new this-character variable
        vm.OnVariablesChanged();

        var prof = _p.LoadVariables(P("variables.json")).ToDictionary(v => v.Name, v => v.Value);
        var glob = _p.LoadVariables(G("variables.json")).ToDictionary(v => v.Name, v => v.Value);
        Assert.Equal(new[] { "hunt", "weapon" }, prof.Keys.OrderBy(k => k));   // no merged set
        Assert.Equal("goblins", prof["hunt"]);
        Assert.Equal("rats",     glob["hunt"]);                             // twin kept
        Assert.Equal("crossing", glob["home"]);
    }

    [Fact]
    public void Variables_ReScopedToAllCharacters_MovesToTheSharedFile()
    {
        SaveVars(P("variables.json"), ("weapon", "sword"));
        var vm = Build();
        vm.VariableStore!.SetConfigScope("weapon", RuleScope.Global);         // the panel's Scope field
        vm.OnVariablesChanged();

        Assert.Empty(_p.LoadVariables(P("variables.json")));
        Assert.Equal("sword", Assert.Single(_p.LoadVariables(G("variables.json"))).Value);
    }

    [Fact]
    public void Variables_CoexistingCfgIsRewrittenPerLayer()
    {
        SaveVars(G("variables.json"), ("home", "crossing"));
        SaveVars(P("variables.json"), ("hunt", "goblins"));
        ConfigPersistence.WriteLines(G("variables.cfg"), new[] { "#var home crossing" });
        ConfigPersistence.WriteLines(P("variables.cfg"), new[] { "#var hunt goblins" });
        var vm = Build();

        vm.VariableStore!.Set("hunt", "trolls");
        vm.OnVariablesChanged();

        var profCfg = File.ReadAllText(P("variables.cfg"));
        var globCfg = File.ReadAllText(G("variables.cfg"));
        Assert.Contains("trolls", profCfg);
        Assert.DoesNotContain("crossing", profCfg);                          // the dual-write can't re-fork
        Assert.Contains("crossing", globCfg);
        Assert.DoesNotContain("trolls", globCfg);
    }

    // ── Classes ────────────────────────────────────────────────────────────

    [Fact]
    public void Classes_SaveSplitsByLayer_ToJsonAndEachCfg()
    {
        var gc = new ClassEngine(); gc.Set("combat", true); gc.Set("loot", true);
        _p.SaveClasses(G("classes.json"), gc);
        var pc = new ClassEngine(); pc.Set("combat", false);
        _p.SaveClasses(P("classes.json"), pc);
        ConfigPersistence.WriteLines(G("classes.cfg"), new[] { "#class combat on", "#class loot on" });
        ConfigPersistence.WriteLines(P("classes.cfg"), new[] { "#class combat off" });
        var vm      = Build();
        var classes = vm.ClassEngine!;

        Assert.False(classes.IsActive("combat"));
        classes.Set("hunting", true);                                        // new → this character
        vm.OnClassesChanged();

        var prof = _p.LoadClasses(P("classes.json")).ToDictionary(c => c.Name, c => c.IsActive);
        var glob = _p.LoadClasses(G("classes.json")).ToDictionary(c => c.Name, c => c.IsActive);
        Assert.Equal(new[] { "combat", "hunting" }, prof.Keys.OrderBy(k => k));
        Assert.False(prof["combat"]);
        Assert.True(glob["combat"]);                                         // shared state kept
        Assert.True(glob["loot"]);
        Assert.DoesNotContain("default", prof.Keys);

        Assert.DoesNotContain("loot", File.ReadAllText(P("classes.cfg")));
        Assert.Contains("#class combat on", File.ReadAllText(G("classes.cfg")));
        Assert.DoesNotContain("hunting", File.ReadAllText(G("classes.cfg")));
    }

    [Fact]
    public void Classes_JsonOnlyProfile_PanelEditIsPersisted()
    {
        // Before public #315 the panel only rewrote an existing classes.cfg, so on a
        // json-only profile a class edit lasted the session.
        var vm = Build();
        vm.ClassEngine!.Set("hunting", false);
        vm.OnClassesChanged();

        var saved = Assert.Single(_p.LoadClasses(P("classes.json")));
        Assert.Equal("hunting", saved.Name);
        Assert.False(saved.IsActive);
        Assert.False(File.Exists(G("classes.json")));                        // no shared file forked
    }

    // ── Twin resurrection ──────────────────────────────────────────────────

    [Fact]
    public void DeletingAnOverride_RestoresTheSharedTwin_AndTheSaveLeavesTheSharedFileAlone()
    {
        _p.SaveAliases(G("aliases.json"), new[] { new AliasRule("hb", "put health") });
        _p.SaveAliases(P("aliases.json"), new[] { new AliasRule("hb", "put perceive health") });
        var vm      = Build();
        var aliases = vm.AliasEngine!;
        Assert.Equal("put perceive health", Assert.Single(aliases.Aliases).Expansion);

        aliases.RemoveAlias("hb");                                           // the panel's delete
        Assert.True(vm.RestoreGlobalTwin("aliases.json", "hb"));
        vm.OnAliasesChanged();

        var back = Assert.Single(aliases.Aliases);
        Assert.Equal("put health", back.Expansion);
        Assert.Equal(RuleScope.Global, back.Scope);
        Assert.Empty(_p.LoadAliases(P("aliases.json")));                     // override gone
        Assert.Equal("put health", Assert.Single(_p.LoadAliases(G("aliases.json"))).Expansion);
    }

    [Fact]
    public void RestoreGlobalTwin_RespectsAnExplicitRemoveForAll()
    {
        _p.SaveAliases(G("aliases.json"), new[] { new AliasRule("hb", "put health") });
        var vm = Build();
        vm.AliasEngine!.RemoveAlias("hb");
        vm.NoteGlobalDelete("aliases.json", "hb");                           // "Remove for all characters"

        Assert.False(vm.RestoreGlobalTwin("aliases.json", "hb"));
        Assert.Empty(vm.AliasEngine!.Aliases);
    }

    [Fact]
    public void RestoreGlobalTwin_Variables()
    {
        SaveVars(G("variables.json"), ("hunt", "rats"));
        SaveVars(P("variables.json"), ("hunt", "goblins"));
        var vm = Build();

        vm.VariableStore!.Remove("hunt");
        Assert.True(vm.RestoreGlobalTwin("variables.json", "hunt"));
        vm.OnVariablesChanged();

        Assert.Equal("rats", vm.VariableStore!.Get("hunt"));
        Assert.Empty(_p.LoadVariables(P("variables.json")));
        Assert.Equal("rats", Assert.Single(_p.LoadVariables(G("variables.json"))).Value);
    }

    [Fact]
    public void RestoreGlobalTwin_WindowResetGoesBackToTheSharedRow()
    {
        _p.SaveWindowSettings(G("windows.json"), new[]
        {
            new WindowSettingsPersistenceModel { Id = "talk", DisplayTitle = "Talk", FontSize = 14, HasIfClosed = true },
        });
        var windows = new WindowSettingsStore();
        var talk    = windows.Register("talk", "Talk");
        talk.FontSize = 22;                                                  // this character's override
        talk.Scope    = RuleScope.Character;
        var vm = Build(windows);

        var changed = 0;
        talk.Changed += () => changed++;
        Assert.True(vm.RestoreGlobalTwin("windows.json", "talk"));

        Assert.Equal(14, talk.FontSize);
        Assert.Equal(RuleScope.Global, talk.Scope);
        Assert.Equal(1, changed);                                            // open window repaints
    }

    [Fact]
    public void RestoreGlobalTwin_NothingInSingleLayerEditing()
    {
        _p.SaveAliases(G("aliases.json"), new[] { new AliasRule("hb", "put health") });
        var vm = new ConfigurationViewModel(null, _configDir, new ProfileStore(), null,
                                            new WindowSettingsStore(),
                                            profileDirResolver: _ => _configDir);
        vm.AliasEngine!.RemoveAlias("hb");
        Assert.False(vm.RestoreGlobalTwin("aliases.json", "hb"));
    }

    // ── Windows ────────────────────────────────────────────────────────────

    [Fact]
    public void WindowSettings_LayoutSaveSplitsByLayer()
    {
        var windows = new WindowSettingsStore();
        windows.Register("talk", "Talk").Scope = RuleScope.Global;
        windows.Register("combat", "Combat");                                // Character by default
        var vm = Build(windows);

        vm.OnWindowSettingsChanged();

        Assert.Equal("combat", Assert.Single(_p.LoadWindowSettings(P("windows.json"))).Id);
        Assert.Equal("talk",   Assert.Single(_p.LoadWindowSettings(G("windows.json"))).Id);
    }
}
