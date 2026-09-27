using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Classes;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Layout;
using Genie.Core.Persistence;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Shunts;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #248 — <c>#shunt</c>: route game lines matching a pattern to a named
/// window. Covers the engine (matching, first-match-wins, classes), the pure
/// target router (move/copy targets, closed-panel fallback, the IfClosed
/// chain, unknown and non-text windows), the command surface, and persistence
/// (.cfg and .json round-trips, the layered load, the connect-time .cfg replay).
/// The display-pipeline ordering (substitute → gag → shunt) is pinned in
/// Genie.App.Tests/ShuntDisplayTests.
/// </summary>
public class ShuntTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public ShuntTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_shunt_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieShuntTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ── Engine ──────────────────────────────────────────────────────────

    [Fact]
    public void Match_returns_the_first_matching_rule()
    {
        var e = new ShuntEngine();
        e.AddRule("kitten", "Pets");
        e.AddRule("kitten pounces", "Other");

        Assert.Equal("Pets", e.Match("A kitten pounces on a ball of yarn.")!.Window);
        Assert.Null(e.Match("You feel fully rested."));
    }

    [Fact]
    public void Match_is_case_insensitive_unless_asked()
    {
        var e = new ShuntEngine();
        e.AddRule("KITTEN", "Pets");
        e.AddRule("Puppy", "Pets", caseSensitive: true);

        Assert.NotNull(e.Match("a kitten"));
        Assert.Null(e.Match("a puppy"));
        Assert.NotNull(e.Match("a Puppy"));
    }

    [Fact]
    public void Disabled_rules_inactive_classes_and_the_master_switch_never_match()
    {
        var classes = new ClassEngine();
        var e = new ShuntEngine { Classes = classes };
        var off = e.AddRule("kitten", "Pets");
        off.IsEnabled = false;
        e.AddRule("puppy", "Pets", className: "dogs");
        classes.Set("dogs", false);
        e.AddRule("parrot", "Pets");

        Assert.Null(e.Match("a kitten"));
        Assert.Null(e.Match("a puppy"));
        Assert.NotNull(e.Match("a parrot"));

        e.Enabled = false;
        Assert.Null(e.Match("a parrot"));
    }

    [Fact]
    public void A_bad_regex_never_matches_and_never_throws()
    {
        var e = new ShuntEngine();
        e.AddRule("(unclosed", "Pets");
        Assert.Null(e.Match("(unclosed"));
    }

    // ── Router ──────────────────────────────────────────────────────────

    private static readonly HashSet<string> Streams =
        new(StringComparer.OrdinalIgnoreCase) { "talk", "log", "atmospherics", "ooc", "combat" };

    private static ShuntDecision Route(string window, ISet<string> open, WindowSettingsStore? store = null) =>
        ShuntRouter.Resolve(window,
            isStream:   Streams.Contains,
            isReserved: n => n.Equals("mapper", StringComparison.OrdinalIgnoreCase) || Streams.Contains(n),
            isOpen:     open.Contains,
            store:      store);

    private static WindowSettingsStore StoreWith(params string[] ids)
    {
        var s = new WindowSettingsStore();
        foreach (var id in ids) s.Register(id, id);
        return s;
    }

    [Fact]
    public void An_open_stream_target_is_delivered_to_its_buffer()
    {
        var d = Route("Atmospherics", new HashSet<string> { "atmospherics" });
        Assert.Equal(new ShuntDecision(ShuntSinkKind.Stream, "atmospherics"), d);
    }

    [Fact]
    public void A_closed_stream_with_the_default_IfClosed_goes_to_main()
    {
        var d = Route("Atmospherics", new HashSet<string>(), StoreWith("atmospherics"));
        Assert.Equal(ShuntSinkKind.Main, d.Kind);
    }

    [Fact]
    public void A_closed_stream_follows_its_IfClosed_chain()
    {
        var store = StoreWith("combat", "log");
        store.Get("combat").IfClosed = "log";

        Assert.Equal(new ShuntDecision(ShuntSinkKind.Stream, "log"),
                     Route("combat", new HashSet<string> { "log" }, store));
        // Target of the redirect closed too, with its own default → Main.
        Assert.Equal(ShuntSinkKind.Main, Route("combat", new HashSet<string>(), store).Kind);
    }

    [Fact]
    public void An_IfClosed_that_drops_resolves_to_main_for_a_shunt()
    {
        var store = StoreWith("ooc");
        store.Get("ooc").IfClosed = "";            // disabled / drop
        Assert.Equal(ShuntSinkKind.Main, Route("ooc", new HashSet<string>(), store).Kind);
    }

    [Fact]
    public void A_closed_stream_with_no_store_goes_to_main()
    {
        Assert.Equal(ShuntSinkKind.Main, Route("talk", new HashSet<string>()).Kind);
    }

    [Fact]
    public void Main_and_non_text_panels_resolve_to_main()
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main", "game", "mapper" };
        Assert.Equal(ShuntSinkKind.Main, Route("main", all).Kind);
        Assert.Equal(ShuntSinkKind.Main, Route("Game", all).Kind);
        Assert.Equal(ShuntSinkKind.Main, Route("game-text", all).Kind);
        Assert.Equal(ShuntSinkKind.Main, Route("Mapper", all).Kind);
        Assert.Equal(ShuntSinkKind.Main, Route("  ", all).Kind);
    }

    [Fact]
    public void A_named_window_is_delivered_when_open_or_not_yet_created()
    {
        // The host reports a never-created window as "open" (first use creates
        // and shows it, like #echo >Name) — the router just trusts isOpen.
        var d = Route("Kittens", new HashSet<string> { "Kittens" });
        Assert.Equal(new ShuntDecision(ShuntSinkKind.Window, "Kittens"), d);
    }

    [Fact]
    public void A_named_window_the_user_closed_keeps_the_line_in_main()
    {
        Assert.Equal(ShuntSinkKind.Main, Route("Kittens", new HashSet<string>()).Kind);
    }

    // ── Commands ────────────────────────────────────────────────────────

    private (FakeCommandHost host, CommandEngine cmd, ShuntEngine shunts) Make()
    {
        var host   = new FakeCommandHost();
        var cmd    = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host);
        var shunts = new ShuntEngine();
        cmd.Shunts = shunts;
        return (host, cmd, shunts);
    }

    [Fact]
    public void Shunt_pattern_window_adds_a_move_rule()
    {
        var (host, cmd, shunts) = Make();
        cmd.ProcessInput("#shunt {^A kitten} {Atmospherics}");

        var r = Assert.Single(shunts.Rules);
        Assert.Equal("^A kitten", r.Pattern);
        Assert.Equal("Atmospherics", r.Window);
        Assert.False(r.Copy);
        Assert.Contains(host.Echoes, e => e.Contains("Shunt added"));
    }

    [Fact]
    public void Trailing_copy_keyword_makes_a_copy_rule_and_does_not_shift_the_class()
    {
        var (_, cmd, shunts) = Make();
        cmd.ProcessInput("#shunt {kitten} {Pets} copy");
        cmd.ProcessInput("#shunt add {puppy} {Pets} {animals} copy");
        cmd.ProcessInput("#shunt {parrot} {Pets} {birds}");

        var kitten = shunts.Rules.Single(r => r.Pattern == "kitten");
        Assert.True(kitten.Copy);
        Assert.Equal("", kitten.ClassName);
        var puppy = shunts.Rules.Single(r => r.Pattern == "puppy");
        Assert.True(puppy.Copy);
        Assert.Equal("animals", puppy.ClassName);
        var parrot = shunts.Rules.Single(r => r.Pattern == "parrot");
        Assert.False(parrot.Copy);
        Assert.Equal("birds", parrot.ClassName);
    }

    [Fact]
    public void Re_adding_a_pattern_replaces_it()
    {
        var (_, cmd, shunts) = Make();
        cmd.ProcessInput("#shunt {kitten} {Pets}");
        cmd.ProcessInput("#shunt {kitten} {Log} copy");

        var r = Assert.Single(shunts.Rules);
        Assert.Equal("Log", r.Window);
        Assert.True(r.Copy);
    }

    [Fact]
    public void Echo_style_window_prefix_is_tolerated()
    {
        var (_, cmd, shunts) = Make();
        cmd.ProcessInput("#shunt {kitten} {>Pets}");
        Assert.Equal("Pets", Assert.Single(shunts.Rules).Window);
    }

    [Fact]
    public void Main_target_and_bad_patterns_are_refused()
    {
        var (host, cmd, shunts) = Make();
        cmd.ProcessInput("#shunt {kitten} {main}");
        cmd.ProcessInput("#shunt {(unclosed} {Pets}");
        cmd.ProcessInput("#shunt add {kitten}");

        Assert.Empty(shunts.Rules);
        Assert.Contains(host.Echoes, e => e.Contains("main window"));
        Assert.Contains(host.Echoes, e => e.Contains("invalid pattern"));
        Assert.Contains(host.Echoes, e => e.StartsWith("Usage: #shunt"));
    }

    [Fact]
    public void List_shows_each_rule_with_its_target_mode_and_class()
    {
        var (host, cmd, _) = Make();
        cmd.ProcessInput("#shunt {kitten} {Pets} {animals} copy");
        cmd.ProcessInput("#shunt {orc} {Combat}");
        host.Echoes.Clear();

        cmd.ProcessInput("#shunt list");
        Assert.Contains("kitten → Pets (copy) [animals]", host.Echoes);
        Assert.Contains("orc → Combat", host.Echoes);

        host.Echoes.Clear();
        cmd.ProcessInput("#shunt combat");        // one arg = filter (pattern or window)
        Assert.Contains("orc → Combat", host.Echoes);
        Assert.DoesNotContain("kitten → Pets (copy) [animals]", host.Echoes);
    }

    [Fact]
    public void Unshunt_and_remove_drop_the_rule()
    {
        var (host, cmd, shunts) = Make();
        cmd.ProcessInput("#shunt {kitten} {Pets}");
        cmd.ProcessInput("#shunt {orc} {Combat}");

        cmd.ProcessInput("#unshunt {kitten}");
        cmd.ProcessInput("#shunt remove {orc}");
        cmd.ProcessInput("#unshunt {never}");

        Assert.Empty(shunts.Rules);
        Assert.Contains("Shunt removed: kitten", host.Echoes);
        Assert.Contains("Shunt removed: orc", host.Echoes);
        Assert.Contains(host.Echoes, e => e.Contains("No shunt with pattern: never"));
    }

    [Fact]
    public void Save_then_load_round_trips_through_shunts_cfg()
    {
        var (_, cmd, shunts) = Make();
        cmd.ProcessInput("#shunt {^A kitten} {Atmospherics} {pets} copy");
        cmd.ProcessInput("#shunt {orc} {Combat}");
        cmd.ProcessInput("#shunt save");

        var path = Path.Combine(_config.ConfigProfileDir, "shunts.cfg");
        Assert.True(File.Exists(path));

        shunts.Clear();
        cmd.ProcessInput("#shunt load");

        Assert.Equal(2, shunts.Rules.Count);
        var kitten = shunts.Rules.Single(r => r.Pattern == "^A kitten");
        Assert.Equal(("Atmospherics", true, "pets"), (kitten.Window, kitten.Copy, kitten.ClassName));
        var orc = shunts.Rules.Single(r => r.Pattern == "orc");
        Assert.Equal(("Combat", false, ""), (orc.Window, orc.Copy, orc.ClassName));
    }

    // ── Persistence ─────────────────────────────────────────────────────

    [Fact]
    public void Json_round_trip_keeps_every_field()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "shunts.json");
        var src  = new ShuntEngine();
        src.AddRule("kitten", "Pets", copy: true, caseSensitive: true, isEnabled: false, className: "animals");

        var p = new PersistenceService();
        p.SaveShunts(path, src.Rules);
        var m = Assert.Single(p.LoadShunts(path));

        Assert.Equal(("kitten", "Pets", true, true, false, "animals"),
                     (m.Pattern, m.Window, m.Copy, m.CaseSensitive, m.IsEnabled, m.ClassName));
    }

    [Fact]
    public void Cfg_format_writes_the_class_slot_so_copy_cannot_be_read_as_a_class()
    {
        var e = new ShuntEngine();
        e.AddRule("kitten", "Pets", copy: true);
        Assert.Equal("#shunt add {kitten} {Pets} {} copy", Assert.Single(CfgFormat.ShuntLines(e.Rules)));
    }

    [Fact]
    public void Layered_load_puts_character_rules_over_global_ones()
    {
        var global  = Path.Combine(_root, "Config");
        var profile = Path.Combine(_root, "Profiles", "Renucci-ACCT");
        Directory.CreateDirectory(global);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(global, "shunts.json"),
            """[{"Pattern":"kitten","Window":"Global"},{"Pattern":"orc","Window":"Combat"}]""");
        File.WriteAllText(Path.Combine(profile, "shunts.json"),
            """[{"Pattern":"kitten","Window":"Mine","Copy":true}]""");

        var p      = new PersistenceService();
        var target = new ShuntEngine();
        LayeredRuleLoad.ApplyLayered(LayeredRuleLoad.BuildEffectiveScope(global, p),
                                     LayeredRuleLoad.BuildEffectiveScope(profile, p),
                                     shunts: target);

        Assert.Equal(2, target.Rules.Count);
        var kitten = target.Rules.Single(r => r.Pattern == "kitten");
        Assert.Equal(("Mine", true, RuleScope.Character), (kitten.Window, kitten.Copy, kitten.Scope));
        Assert.Equal(RuleScope.Global, target.Rules.Single(r => r.Pattern == "orc").Scope);
    }

    [Fact]
    public void A_coexisting_shunts_cfg_is_the_persisted_truth_for_its_dir()
    {
        var dir = Path.Combine(_root, "Config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "shunts.json"), """[{"Pattern":"stale","Window":"Old"}]""");
        File.WriteAllText(Path.Combine(dir, "shunts.cfg"), "#shunt add {fresh} {New} {} copy");

        var scope = LayeredRuleLoad.BuildEffectiveScope(dir, new PersistenceService());

        var r = Assert.Single(scope.Shunts.Rules);
        Assert.Equal(("fresh", "New", true), (r.Pattern, r.Window, r.Copy));
    }

    [Fact]
    public void Live_reload_applies_an_external_edit_and_rewrites_the_cfg_twin()
    {
        var global  = Path.Combine(_root, "Config");
        var profile = Path.Combine(_root, "Profiles", "Renucci-ACCT");
        Directory.CreateDirectory(global);
        Directory.CreateDirectory(profile);
        var shunts = new ShuntEngine();
        shunts.AddRule("old", "Somewhere");
        File.WriteAllText(Path.Combine(profile, "shunts.cfg"), "#shunt add {old} {Somewhere} {}");
        File.WriteAllText(Path.Combine(profile, "shunts.json"),
            """[{"Pattern":"kitten","Window":"Pets","Copy":true}]""");

        var n = RuleFileLiveReload.Reload("shunts.json", profile, global, shunts: shunts);

        Assert.Equal(1, n);
        var r = Assert.Single(shunts.Rules);
        Assert.Equal(("kitten", "Pets", true), (r.Pattern, r.Window, r.Copy));
        var cfg = File.ReadAllText(Path.Combine(profile, "shunts.cfg"));
        Assert.Contains("kitten", cfg);
        Assert.DoesNotContain("old", cfg);
    }

    [Fact]
    public void Live_reload_of_a_corrupt_file_keeps_the_current_rules()
    {
        var dir = Path.Combine(_root, "Config");
        Directory.CreateDirectory(dir);
        var shunts = new ShuntEngine();
        shunts.AddRule("keep", "Pets");
        File.WriteAllText(Path.Combine(dir, "shunts.json"), "[ not json");

        Assert.ThrowsAny<Exception>(() => RuleFileLiveReload.Reload("shunts.json", dir, dir, shunts: shunts));
        Assert.Equal("keep", Assert.Single(shunts.Rules).Pattern);
    }

    [Fact]
    public void Shunts_json_is_a_watched_rule_file()
    {
        Assert.Contains("shunts.json", RuleFileWatcher.WatchedFiles);
    }

    [Fact]
    public void Headless_connect_replay_loads_shunts_cfg()
    {
        var dir = Path.Combine(_root, "Config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "shunts.cfg"), "#shunt add {kitten} {Pets} {animals}");

        var shunts = new ShuntEngine();
        CfgReplay.LoadInto(dir, shunts: shunts);

        var r = Assert.Single(shunts.Rules);
        Assert.Equal(("kitten", "Pets", "animals"), (r.Pattern, r.Window, r.ClassName));
    }

    /// <summary>Records every echo and game dispatch.</summary>
    private sealed class FakeCommandHost : ICommandHost
    {
        public List<string> SentToGame { get; } = new();
        public List<string> Echoes     { get; } = new();
        private readonly Dictionary<string, string> _globals = new();

        public IReadOnlyDictionary<string, string> GetGlobalVariables() => _globals;
        public string ExpandVariables(string text) => text;
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null)
            => SentToGame.Add(text);
        public void Echo(string text) => Echoes.Add(text);
        public void EchoTo(string text, string? window, string? color) { }
        public void EchoMain(string text, string? color, bool mono) { }
        public void EchoLink(string text, string command, string? window) { }
        public void EchoClear(string? window) { }
        public void WindowCommand(string sub, string window) { }
        public void SetStatusBar(string text, int index) { }
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
        public void SetGlobalVariable(string name, string value) => _globals[name] = value;
        public void RemoveGlobalVariable(string name) => _globals.Remove(name);
        public string SetLiveAudit(Genie.Core.Diagnostics.AuditMode mode) => string.Empty;
        public void EditScript(string name) { }
        public void LayoutCommand(string args) { }
        public void PluginCommand(string args) { }
        public void ConfigCommand(string args) { }
        public void MapperGoto(string args) { }
        public void MapperReset() { }
        public void MapperCommand(string args) { }
        public void PlaySound(string soundName) { }
        public void Speak(string text, bool urgent = false) { }
        public void TtsCommand(string args) { }
        public void FlashWindow() { }
        public void Connect(ConnectRequest request) { }
    }
}
