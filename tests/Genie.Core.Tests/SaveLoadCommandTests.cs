using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Macros;
using Genie.Core.Persistence;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Shunts;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #440: Genie 4's <c>#save &lt;list&gt; …</c> and <c>#load …</c> were
/// unknown commands, so a script's <c>put #save vars</c> wrote nothing. Each
/// word now runs that list's own save or load.
/// </summary>
public class SaveLoadCommandTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public SaveLoadCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_saveload_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieSaveLoadTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private (FakeCommandHost host, CommandEngine engine) NewEngine()
    {
        var host   = new FakeCommandHost();
        var engine = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host);
        engine.Variables = new VariableEngine(engine);
        engine.Aliases   = new AliasEngine(engine);
        engine.Classes   = new ClassEngine();
        return (host, engine);
    }

    private string ProfileFile(string name) => Path.Combine(_config.ConfigProfileDir, name);

    [Theory]
    [InlineData("var")]
    [InlineData("vars")]
    [InlineData("variable")]
    [InlineData("setvar")]
    [InlineData("setvariable")]
    [InlineData("VARS")]
    public void Save_vars_writes_the_variables_file(string word)
    {
        var (host, engine) = NewEngine();
        engine.ProcessInput("#var lootbag backpack");

        engine.ProcessInput($"#save {word}");

        Assert.Contains("Variables Saved", host.Echoes);
        Assert.Contains("lootbag", File.ReadAllText(ProfileFile("variables.cfg")));
        Assert.DoesNotContain(host.Echoes, e => e.Contains("Unknown command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Save_writes_the_same_file_as_the_lists_own_save()
    {
        var (_, engine) = NewEngine();
        engine.ProcessInput("#var lootbag backpack");
        engine.ProcessInput("#var save");
        var own = File.ReadAllText(ProfileFile("variables.cfg"));
        File.Delete(ProfileFile("variables.cfg"));

        engine.ProcessInput("#save vars");

        Assert.Equal(own, File.ReadAllText(ProfileFile("variables.cfg")));
    }

    [Fact]
    public void Several_words_save_several_lists()
    {
        var (host, engine) = NewEngine();

        engine.ProcessInput("#save vars aliases classes");

        Assert.Equal(new[] { "Variables Saved", "Aliases Saved", "Classes Saved" }, host.Echoes);
        Assert.True(File.Exists(ProfileFile("variables.cfg")));
        Assert.True(File.Exists(ProfileFile("aliases.cfg")));
        Assert.True(File.Exists(ProfileFile("classes.cfg")));
    }

    [Fact]
    public void Save_all_saves_every_list_and_the_settings_but_not_layout_or_profile()
    {
        var (host, engine) = NewEngine();

        engine.ProcessInput("#save all");

        Assert.Contains("Variables Saved", host.Echoes);
        Assert.Contains("Aliases Saved", host.Echoes);
        Assert.Contains("Classes Saved", host.Echoes);
        Assert.Equal(new[] { "save" }, host.ConfigCommands);
        Assert.Empty(host.LayoutCommands);
        Assert.DoesNotContain(host.Echoes, e => e.Contains("layout", StringComparison.OrdinalIgnoreCase)
                                             || e.Contains("profile", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("set")]
    [InlineData("setting")]
    [InlineData("settings")]
    public void Save_config_goes_to_the_settings_save(string word)
    {
        var (host, engine) = NewEngine();

        engine.ProcessInput($"#save {word}");

        Assert.Equal(new[] { "save" }, host.ConfigCommands);
    }

    [Fact]
    public void Load_vars_reads_the_file_back()
    {
        var (host, engine) = NewEngine();
        engine.ProcessInput("#var lootbag backpack");
        engine.ProcessInput("#save vars");
        engine.ProcessInput("#unvar lootbag");
        Assert.Null(engine.Variables!.Store.Get("lootbag"));

        engine.ProcessInput("#load vars");

        Assert.Equal("backpack", engine.Variables.Store.Get("lootbag"));
        Assert.DoesNotContain(host.Echoes, e => e.Contains("Unknown command", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_config_goes_to_the_settings_load()
    {
        var (host, engine) = NewEngine();

        engine.ProcessInput("#load config");

        Assert.Equal(new[] { "load" }, host.ConfigCommands);
    }

    [Fact]
    public void An_unknown_word_is_named_and_the_rest_still_run()
    {
        var (host, engine) = NewEngine();

        engine.ProcessInput("#save varz aliases");

        Assert.Contains(host.Echoes, e => e.Contains("unknown list 'varz'"));
        Assert.Contains("Aliases Saved", host.Echoes);
    }

    [Theory]
    [InlineData("#save")]
    [InlineData("#load")]
    public void A_bare_verb_prints_usage(string input)
    {
        var (host, engine) = NewEngine();

        engine.ProcessInput(input);

        Assert.Single(host.Echoes);
        Assert.StartsWith("Usage: " + input, host.Echoes[0]);
    }

    [Theory]
    [InlineData("#save layout", "#layout save")]
    [InlineData("#load layout", "#layout load")]
    [InlineData("#save profile", "Connect dialog")]
    [InlineData("#load profile", "Connect dialog")]
    public void Layout_and_profile_say_where_to_go(string input, string expected)
    {
        var (host, engine) = NewEngine();

        engine.ProcessInput(input);

        Assert.Single(host.Echoes);
        Assert.Contains(expected, host.Echoes[0]);
        Assert.Empty(host.LayoutCommands);
    }

    // ── Public #446: a typed save writes the character's own entries only ────
    // A connected character has its own profile folder beside the shared
    // Config folder. `#save all` wrote every loaded entry into the profile
    // folder, so each shared rule became a private copy that shadowed the
    // shared file from the next login on.

    private const string SharedPattern = "shared pattern";
    private const string OwnPattern    = "own pattern";

    /// <summary>An engine for a connected character (two config layers), with
    /// one shared entry loaded in each of the nine lists.</summary>
    private (FakeCommandHost host, CommandEngine engine) NewCharacterEngineWithSharedEntries()
    {
        _config.ApplyCharacterProfile("Tester", "ACCT");
        Assert.NotEqual(_config.ConfigDir, _config.ConfigProfileDir);

        var (host, engine) = NewEngine();
        engine.Highlights  = new HighlightEngine();
        engine.Triggers    = new TriggerEngineFinal();
        engine.Substitutes = new SubstituteEngine();
        engine.Gags        = new GagEngine();
        engine.Shunts      = new ShuntEngine();
        engine.Macros      = new MacroEngine();

        engine.Highlights.AddRule(SharedPattern, "#FFFFFF", "#800000",
            windows: new[] { "experience" }).Scope = RuleScope.Global;
        engine.Triggers.AddTrigger(SharedPattern, "#echo shared").Scope = RuleScope.Global;
        engine.Substitutes.AddRule(SharedPattern, "replaced").Scope = RuleScope.Global;
        engine.Gags.AddRule(SharedPattern).Scope = RuleScope.Global;
        engine.Shunts.AddRule(SharedPattern, "log").Scope = RuleScope.Global;
        engine.Aliases!.AddAlias("sharedalias", "look").Scope = RuleScope.Global;
        engine.Macros.Add("F9", "look");
        engine.Macros.Get("F9")!.Scope = RuleScope.Global;
        engine.Classes!.Set("sharedclass", true);
        engine.Classes.SetScope("sharedclass", RuleScope.Global);
        engine.Variables!.Store.Set("sharedvar", "1");
        engine.Variables.Store.SetConfigScope("sharedvar", RuleScope.Global);
        return (host, engine);
    }

    private static readonly string[] RuleCfgFiles =
    {
        "variables.cfg", "aliases.cfg", "classes.cfg", "triggers.cfg", "macros.cfg",
        "substitutes.cfg", "gags.cfg", "highlights.cfg", "shunts.cfg",
    };

    [Fact]
    public void Save_all_makes_no_profile_file_for_a_list_holding_only_shared_entries()
    {
        var (host, engine) = NewCharacterEngineWithSharedEntries();

        engine.ProcessInput("#save all");

        foreach (var file in RuleCfgFiles)
            Assert.False(File.Exists(ProfileFile(file)), $"{file} should not have been written");
        Assert.Contains("Highlights Saved", host.Echoes);
        Assert.DoesNotContain(host.Echoes, e => e.StartsWith("Failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_all_writes_the_characters_own_entries_and_leaves_the_shared_ones_out()
    {
        var (_, engine) = NewCharacterEngineWithSharedEntries();
        engine.ProcessInput($"#highlight add {{{OwnPattern}}} {{Yellow}}");
        engine.ProcessInput($"#trigger add {{{OwnPattern}}} {{#echo own}}");
        engine.ProcessInput($"#substitute add {{{OwnPattern}}} {{mine}}");
        engine.ProcessInput($"#gag add {{{OwnPattern}}}");
        engine.ProcessInput($"#shunt add {{{OwnPattern}}} {{log}}");
        engine.ProcessInput("#alias add ownalias look");
        engine.ProcessInput("#macro add F8 look");
        engine.ProcessInput("#class ownclass on");
        engine.ProcessInput("#var ownvar 1");

        engine.ProcessInput("#save all");

        foreach (var file in new[] { "highlights.cfg", "triggers.cfg", "substitutes.cfg", "gags.cfg", "shunts.cfg" })
        {
            var text = File.ReadAllText(ProfileFile(file));
            Assert.Contains(OwnPattern, text);
            Assert.DoesNotContain(SharedPattern, text);
        }
        AssertOwnOnly("aliases.cfg",   "ownalias", "sharedalias");
        AssertOwnOnly("macros.cfg",    "F8",       "F9");
        AssertOwnOnly("classes.cfg",   "ownclass", "sharedclass");
        AssertOwnOnly("variables.cfg", "ownvar",   "sharedvar");
    }

    private void AssertOwnOnly(string file, string own, string shared)
    {
        var text = File.ReadAllText(ProfileFile(file));
        Assert.Contains(own, text);
        Assert.DoesNotContain(shared, text);
    }

    [Fact]
    public void Saving_after_the_last_own_entry_is_removed_empties_the_existing_file()
    {
        var (_, engine) = NewCharacterEngineWithSharedEntries();
        engine.ProcessInput($"#gag add {{{OwnPattern}}}");
        engine.ProcessInput("#save gags");
        Assert.Contains(OwnPattern, File.ReadAllText(ProfileFile("gags.cfg")));

        engine.Gags!.RemoveRule(OwnPattern);
        engine.ProcessInput("#save gags");

        Assert.True(File.Exists(ProfileFile("gags.cfg")));
        Assert.DoesNotContain("#gag", File.ReadAllText(ProfileFile("gags.cfg")));
    }

    [Fact]
    public void Load_keeps_the_shared_entries_the_profile_file_does_not_replace()
    {
        var (_, engine) = NewCharacterEngineWithSharedEntries();
        engine.ProcessInput($"#highlight add {{{OwnPattern}}} {{Yellow}}");
        engine.ProcessInput($"#trigger add {{{OwnPattern}}} {{#echo own}}");
        engine.ProcessInput($"#substitute add {{{OwnPattern}}} {{mine}}");
        engine.ProcessInput($"#gag add {{{OwnPattern}}}");
        engine.ProcessInput($"#shunt add {{{OwnPattern}}} {{log}}");
        engine.ProcessInput("#macro add F8 look");
        engine.ProcessInput("#save all");

        engine.ProcessInput("#load all");

        var shared = Assert.Single(engine.Highlights!.Rules, r => r.Pattern == SharedPattern);
        Assert.Equal(RuleScope.Global, shared.Scope);
        Assert.Equal(new[] { "experience" }, shared.Windows);
        Assert.Equal(RuleScope.Character,
            Assert.Single(engine.Highlights.Rules, r => r.Pattern == OwnPattern).Scope);

        Assert.Equal(RuleScope.Global, Assert.Single(engine.Triggers!.Triggers, r => r.Pattern == SharedPattern).Scope);
        Assert.Single(engine.Triggers.Triggers, r => r.Pattern == OwnPattern);
        Assert.Equal(RuleScope.Global, Assert.Single(engine.Substitutes!.Rules, r => r.Pattern == SharedPattern).Scope);
        Assert.Single(engine.Substitutes.Rules, r => r.Pattern == OwnPattern);
        Assert.Equal(RuleScope.Global, Assert.Single(engine.Gags!.Rules, r => r.Pattern == SharedPattern).Scope);
        Assert.Single(engine.Gags.Rules, r => r.Pattern == OwnPattern);
        Assert.Equal(RuleScope.Global, Assert.Single(engine.Shunts!.Rules, r => r.Pattern == SharedPattern).Scope);
        Assert.Single(engine.Shunts.Rules, r => r.Pattern == OwnPattern);
        Assert.Equal(RuleScope.Global, engine.Macros!.Get("F9")!.Scope);
        Assert.Equal(RuleScope.Character, engine.Macros.Get("F8")!.Scope);
    }

    [Fact]
    public void Load_lets_the_characters_copy_win_over_a_shared_entry_with_the_same_key()
    {
        var (_, engine) = NewCharacterEngineWithSharedEntries();
        File.WriteAllText(ProfileFile("highlights.cfg"),
            $"#highlight add {{{SharedPattern}}} {{Yellow}} {{}} {{String}} {{}} {{}} {{}}\n");

        engine.ProcessInput("#load highlights");

        var rule = Assert.Single(engine.Highlights!.Rules);
        Assert.Equal("Yellow", rule.ForegroundColor);
        Assert.Equal(RuleScope.Character, rule.Scope);
    }

    [Fact]
    public void Without_a_profile_folder_a_save_still_writes_every_entry()
    {
        var (_, engine) = NewEngine();
        engine.Highlights = new HighlightEngine();
        engine.Highlights.AddRule(SharedPattern, "#FFFFFF").Scope = RuleScope.Global;
        Assert.Equal(_config.ConfigDir, _config.ConfigProfileDir);

        engine.ProcessInput("#save highlights");

        Assert.Contains(SharedPattern, File.ReadAllText(ProfileFile("highlights.cfg")));
    }

    private sealed class FakeCommandHost : ICommandHost
    {
        public List<string> Echoes { get; } = new();
        public List<string> ConfigCommands { get; } = new();
        public List<string> LayoutCommands { get; } = new();

        public Dictionary<string, string> Globals { get; } = new();
        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;

        public void Echo(string text) => Echoes.Add(text);
        public void EchoTo(string text, string? window, string? color) { }
        public void EchoMain(string text, string? color, bool mono) { }
        public void EchoLink(string text, string command, string? window) { }
        public void EchoClear(string? window) { }
        public void WindowCommand(string sub, string window) { }
        public void SetStatusBar(string text, int index) { }
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null) { }
        public void RunScript(string text) { }
        public void InjectParsedLine(string line) { }
        public void StopScript(string? name) { }
        public void PauseScript(string? name) { }
        public void ResumeScript(string? name) { }
        public void StopAllScripts() { }
        public void PauseAllScripts() { }
        public void ResumeAllScripts() { }
        public void SetTraceLevelAll(int level) { }
        public IReadOnlyList<string> RunningScripts() => Array.Empty<string>();
        public void SetGlobalVariable(string name, string value) => Globals[name] = value;
        public void RemoveGlobalVariable(string name) => Globals.Remove(name);
        public string SetLiveAudit(Genie.Core.Diagnostics.AuditMode mode) => string.Empty;
        public void EditScript(string name) { }
        public void LayoutCommand(string args) => LayoutCommands.Add(args);
        public void PluginCommand(string args) { }
        public void ConfigCommand(string args) => ConfigCommands.Add(args);
        public void MapperGoto(string args) { }
        public void MapperCommand(string args) { }
        public void MapperReset() { }
        public void PlaySound(string soundName) { }
        public void Speak(string text, bool urgent = false) { }
        public void TtsCommand(string args) { }
        public void FlashWindow() { }
        public void Connect(ConnectRequest request) { }
    }
}
