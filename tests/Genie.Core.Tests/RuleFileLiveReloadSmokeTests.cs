using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Persistence;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Smoke test for the rule-file live-reload feature end to end:
/// <see cref="RuleFileWatcher"/> (detection) feeding
/// <see cref="RuleFileLiveReload"/> (application) — the exact pair the app
/// wires together in MainWindowViewModel. Hand-edit a rule .json while the
/// engines are live and the edit must apply: adds, replacements, deletions,
/// profile-over-global fallback, .cfg lockstep, and corrupt-file safety.
/// </summary>
public class RuleFileLiveReloadSmokeTests : IDisposable
{
    private readonly string _profileDir;
    private readonly string _globalDir;

    public RuleFileLiveReloadSmokeTests()
    {
        var root    = Path.Combine(Path.GetTempPath(), "genie_livereload_smoke_" + Guid.NewGuid().ToString("N"));
        _profileDir = Path.Combine(root, "Profiles", "Renucci-ACCT");
        _globalDir  = Path.Combine(root, "Config");
        Directory.CreateDirectory(_profileDir);
        Directory.CreateDirectory(_globalDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_globalDir)!, recursive: true); } catch { }
    }

    [Fact]
    public void SmokeTest_ExternalEditsFlowThroughWatcherIntoAllSevenEngines()
    {
        // Live engines, as GenieCore holds them.
        var highlights  = new HighlightEngine();
        var triggers    = new TriggerEngineFinal();
        var substitutes = new SubstituteEngine();
        var gags        = new GagEngine();
        var aliases     = new AliasEngine();
        var variables   = new VariableStore();
        var classes     = new ClassEngine();

        int Reload(string fileName) => RuleFileLiveReload.Reload(
            fileName, _profileDir, _globalDir,
            highlights: highlights, triggers: triggers, substitutes: substitutes,
            gags: gags, aliases: aliases, variables: variables, classes: classes);

        using var watcher  = new RuleFileWatcher(debounceMs: 100);
        var pending        = new ConcurrentQueue<string>();
        using var anyEvent = new SemaphoreSlim(0);
        watcher.RuleFileChanged += name => { pending.Enqueue(name); anyEvent.Release(); };
        watcher.Rescope(_profileDir, _globalDir);

        HashSet<string> AwaitEvents(params string[] names)
        {
            var want = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (seen.Count < want.Count && DateTime.UtcNow < deadline)
                if (anyEvent.Wait(TimeSpan.FromMilliseconds(250)) &&
                    pending.TryDequeue(out var n) && want.Contains(n))
                    seen.Add(n);
            return seen;
        }

        // ── 1. Drop all seven files externally (the on-disk PascalCase format
        //       the app writes; a user's hand edit looks exactly like this). ──
        File.WriteAllText(Path.Combine(_profileDir, "highlights.json"),
            """[{"Pattern":"Renucci","ForegroundColor":"Red","MatchType":"String"}]""");
        File.WriteAllText(Path.Combine(_profileDir, "triggers.json"),
            """[{"Pattern":"You are stunned","Action":"#echo ouch"}]""");
        File.WriteAllText(Path.Combine(_profileDir, "substitutes.json"),
            """[{"Pattern":"gobbo","Replacement":"goblin"}]""");
        File.WriteAllText(Path.Combine(_profileDir, "gags.json"),
            """[{"Pattern":"The silvery light"}]""");
        File.WriteAllText(Path.Combine(_profileDir, "aliases.json"),
            """[{"Name":"hp","Expansion":"health"}]""");
        File.WriteAllText(Path.Combine(_profileDir, "variables.json"),
            """[{"Name":"hunt_room","Value":"117"}]""");
        // Classes in the GLOBAL dir — also smokes the profile-over-global fallback.
        File.WriteAllText(Path.Combine(_globalDir, "classes.json"),
            """[{"Name":"combat","IsActive":false}]""");

        var seen = AwaitEvents("highlights.json", "triggers.json", "substitutes.json",
                               "gags.json", "aliases.json", "variables.json", "classes.json");
        Assert.Equal(7, seen.Count);

        // ── 2. Apply each reported file — the same call the app makes. ──
        foreach (var name in seen) Reload(name);

        Assert.Contains(highlights.Rules,  r => r.Pattern == "Renucci" && r.ForegroundColor == "Red");
        Assert.Contains(triggers.Triggers, t => t.Pattern == "You are stunned" && t.Action == "#echo ouch");
        Assert.Contains(substitutes.Rules, r => r.Pattern == "gobbo" && r.Replacement == "goblin");
        Assert.Contains(gags.Rules,        r => r.Pattern == "The silvery light");
        Assert.Contains(aliases.Aliases,   a => a.Name == "hp" && a.Expansion == "health");
        Assert.Contains(variables.GetAll().Values, v => v.Name == "hunt_room" && v.Value == "117");
        Assert.Contains(classes.GetAll(),  kv => kv.Key.Equals("combat", StringComparison.OrdinalIgnoreCase) && !kv.Value);

        // ── 3. Second edit cycle: a replacement and a deletion must both apply. ──
        File.WriteAllText(Path.Combine(_profileDir, "aliases.json"),
            """[{"Name":"mana","Expansion":"harness"}]""");
        File.Delete(Path.Combine(_profileDir, "gags.json"));

        var seen2 = AwaitEvents("aliases.json", "gags.json");
        Assert.Equal(2, seen2.Count);
        foreach (var name in seen2) Reload(name);

        Assert.DoesNotContain(aliases.Aliases, a => a.Name == "hp");        // removed by the edit
        Assert.Contains(aliases.Aliases, a => a.Name == "mana");            // added by the edit
        Assert.Empty(gags.Rules);                                           // file deleted → engine cleared
    }

    [Fact]
    public void CorruptFileThrowsAndLeavesEngineUntouched()
    {
        // SubstituteEngine on purpose: PersistenceService.LoadSubstitutes swallows
        // parse errors into an empty list, which via clear-then-load would wipe
        // the engine over a half-written file. The live-reload path must parse
        // strictly instead — throw, keep the rules.
        var substitutes = new SubstituteEngine();
        substitutes.AddRule("keep", "kept", false, true, "");

        File.WriteAllText(Path.Combine(_profileDir, "substitutes.json"), "[ this is not json");

        Assert.ThrowsAny<Exception>(() =>
            RuleFileLiveReload.Reload("substitutes.json", _profileDir, _globalDir, substitutes: substitutes));
        Assert.Contains(substitutes.Rules, r => r.Pattern == "keep" && r.Replacement == "kept");
    }

    [Fact]
    public void CoexistingCfgIsRewrittenSoReconnectKeepsTheEdit()
    {
        // The connect sequence replays .cfg AFTER .json and its loaders clear
        // first — a stale .cfg would revert the external edit at next connect.
        var triggers = new TriggerEngineFinal();
        var cfgPath  = Path.Combine(_profileDir, "triggers.cfg");
        File.WriteAllText(cfgPath, "#trigger {stale} {reverted}");
        File.WriteAllText(Path.Combine(_profileDir, "triggers.json"),
            """[{"Pattern":"fresh","Action":"#echo new"}]""");

        RuleFileLiveReload.Reload("triggers.json", _profileDir, _globalDir, triggers: triggers);

        var cfg = File.ReadAllText(cfgPath);
        Assert.Contains("fresh", cfg);
        Assert.DoesNotContain("stale", cfg);
    }

    [Fact]
    public void CfgIsNotForkedForJsonOnlyProfiles()
    {
        var triggers = new TriggerEngineFinal();
        File.WriteAllText(Path.Combine(_profileDir, "triggers.json"),
            """[{"Pattern":"fresh","Action":"#echo new"}]""");

        RuleFileLiveReload.Reload("triggers.json", _profileDir, _globalDir, triggers: triggers);

        Assert.False(File.Exists(Path.Combine(_profileDir, "triggers.cfg")));
    }

    [Fact]
    public void UnknownFileNameThrows()
    {
        Assert.Throws<ArgumentException>(() =>
            RuleFileLiveReload.Reload("display.json", _profileDir, _globalDir));
    }
}
