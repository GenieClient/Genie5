using System;
using System.IO;
using System.Linq;
using Genie.Core.Aliases;
using Genie.Core.Macros;
using Genie.Core.Persistence;
using Genie.Core.Variables;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Pins <see cref="CfgReplay"/> — the offline .cfg replay behind the
/// Configuration dialog's draft engines and the panel dual-write fix
/// ("custom changes not holding"): at connect the .cfg files replay AFTER
/// the .json load and each loader clears first, so a .cfg on disk is the
/// effective persisted truth for its rule type. Drafts must therefore
/// overlay the .cfg, and what the panels write back to the .cfg
/// (<see cref="CfgFormat"/>) must round-trip through the loaders unchanged.
/// </summary>
public class CfgReplayTests : IDisposable
{
    private readonly string _dir;

    public CfgReplayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_cfgreplay_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private void WriteCfg(string fileName, params string[] lines)
        => File.WriteAllLines(Path.Combine(_dir, fileName), lines);

    [Fact]
    public void Replays_saved_cfg_rules_into_offline_engines()
    {
        WriteCfg("macros.cfg",  "#macro add {num8} \"#if {$hidden = 1}{#put sneak north}{#put north}\"");
        WriteCfg("aliases.cfg", "#alias add {atk} {attack target}");

        var macros  = new MacroEngine();
        var aliases = new AliasEngine();
        CfgReplay.LoadInto(_dir, aliases: aliases, macros: macros);

        // The conditional-movement action (nested braces → FormatArg
        // quote-wraps it) must survive verbatim — this is the exact macro
        // shape the "changes not holding" report was about.
        Assert.Equal("#if {$hidden = 1}{#put sneak north}{#put north}", macros.Get("num8")?.Action);
        Assert.Equal("attack target", aliases.Aliases.Single(a => a.Name == "atk").Expansion);
    }

    [Fact]
    public void Cfg_replay_clears_preloaded_rules_matching_connect_semantics()
    {
        // Connect parity: the engine holds .json-loaded rules, then the .cfg
        // loader clears and replays — the .cfg wins wholesale. This is the
        // clobber the dual-write fix keys off, so pin it.
        WriteCfg("macros.cfg", "#macro add {num2} {s}");

        var macros = new MacroEngine();
        macros.Add("F1", "json only");
        CfgReplay.LoadInto(_dir, macros: macros);

        Assert.Null(macros.Get("F1"));
        Assert.Equal("s", macros.Get("num2")?.Action);
    }

    [Fact]
    public void Variables_merge_into_the_target_store_without_clearing()
    {
        // #var load merges (never clears) — the overlay must do the same.
        WriteCfg("variables.cfg", "#var {hidden} {1}");

        var store = new VariableStore();
        store.Set("keep", "kept");
        CfgReplay.LoadInto(_dir, variables: store);

        var all = store.GetAll();
        Assert.Equal("kept", all["keep"].Value);
        Assert.Equal("1", all["hidden"].Value);
    }

    [Fact]
    public void Missing_directory_and_missing_files_are_noops()
    {
        var macros = new MacroEngine();
        macros.Add("F1", "stays");

        CfgReplay.LoadInto(Path.Combine(_dir, "does-not-exist"), macros: macros);
        CfgReplay.LoadInto(_dir, macros: macros);   // dir exists, no macros.cfg

        Assert.Equal("stays", macros.Get("F1")?.Action);
    }

    [Fact]
    public void Panel_dualwrite_output_roundtrips_through_the_loader()
    {
        // Exactly what ConfigurationViewModel.SyncCfg writes: CfgFormat lines
        // via ConfigPersistence. A fresh replay must reproduce the rule set.
        var source = new MacroEngine();
        source.Add("num8",   "#if {$hidden = 1}{#put sneak north}{#put north}");
        source.Add("num*",   "health");
        source.Add("Ctrl+D", "look");
        Genie.Core.Runtime.ConfigPersistence.WriteLines(
            Path.Combine(_dir, "macros.cfg"), CfgFormat.MacroLines(source.Rules));

        var reloaded = new MacroEngine();
        CfgReplay.LoadInto(_dir, macros: reloaded);

        Assert.Equal(source.Rules.Count, reloaded.Rules.Count);
        foreach (var rule in source.Rules)
            Assert.Equal(rule.Action, reloaded.Get(rule.Key)?.Action);
    }
}
