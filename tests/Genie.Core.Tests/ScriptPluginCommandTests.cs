using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Config;
using Genie.Core.Extensions;
using Genie.Core.Runtime;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #325 — a slash-prefixed command issued by a script (<c>send</c>,
/// <c>put</c>, a bare line, <c>move</c>, or JS <c>put()</c>) must be offered to
/// the external plugins before it reaches the game, exactly as typed input is.
///
/// <para>Reported against Barnacus' Genie 4 <c>Tracker.dll</c>: legacy scripts
/// are full of <c>send /timers start BLESS</c>, and Genie 5 put those straight
/// on the wire, so DragonRealms answered "Please rephrase that command." A
/// transparent plugin port was impossible without rewriting every dependent
/// script.</para>
///
/// <para>Built-in extensions already got this offer (commit be23134); these
/// tests cover the missing plugin leg, plus the two send paths that had no
/// client-side offer at all — <c>move</c> and the JS interop bridge.</para>
/// </summary>
public class ScriptPluginCommandTests
{
    /// <summary>Extension that claims <c>/probe…</c>, for the ordering test.</summary>
    private sealed class ProbeExtension : IGameExtension
    {
        public string Name        => "Probe";
        public string Version     => "1.0";
        public string Description => "test probe";
        public bool   Enabled     { get; set; } = true;
        public List<string> Claimed { get; } = new();

        public void Initialize(IExtensionHost host) { }
        public void OnGameLine(string line) { }
        public void OnCommandSent(string command) { }
        public void OnPrompt() { }
        public void Shutdown() { }

        public bool OnSlashCommand(string input)
        {
            if (!input.StartsWith("/probe", StringComparison.OrdinalIgnoreCase)) return false;
            Claimed.Add(input);
            return true;
        }
    }

    private sealed record Run(List<string> Seen, List<string> Sent, List<string> Echoed,
                              ProbeExtension Probe);

    /// <summary>Run a .cmd body against a fresh engine whose PluginInput records
    /// every offer and applies <paramref name="transform"/> (default: pass through
    /// unchanged — the "no plugin claims it" case). Returns what the plugin saw,
    /// what actually reached the send delegate, and what was echoed.
    /// <paramref name="configure"/> mutates the engine's live
    /// <see cref="GenieConfig"/> before the script runs (used for
    /// <c>mycommandchar</c>); leave it null to exercise the default <c>'/'</c>.
    /// </summary>
    private static Run RunFixture(string body, Func<string, string?>? transform = null,
                                  int ticks = 400, bool registerProbe = false,
                                  Action<GenieConfig>? configure = null)
    {
        var seen   = new List<string>();
        var sent   = new List<string>();
        var echoed = new List<string>();
        var dir    = Path.Combine(Path.GetTempPath(), "gc_plugincmd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: c => sent.Add(c), echo: e => echoed.Add(e));
            if (configure is not null)
            {
                var lds = new LocalDirectoryService("GenieScriptPluginCmdTest", dir);
                lds.UseExplicitRoot(dir);
                var cfg = new GenieConfig(lds);
                configure(cfg);
                engine.Config = cfg;
            }
            var probe = new ProbeExtension();
            if (registerProbe) engine.Extensions.Register(probe);
            engine.PluginInput = cmd =>
            {
                seen.Add(cmd);
                return transform is null ? cmd : transform(cmd);
            };
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < ticks; i++) engine.Tick();
            return new Run(seen, sent, echoed, probe);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ── the reported repro: swallowed commands must not reach the game ────────

    [Fact]
    public void Send_slash_swallowed_by_a_plugin_never_reaches_the_game()
    {
        var r = RunFixture("send /owned\n", _ => null);

        Assert.Contains("/owned", r.Seen);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Put_slash_swallowed_by_a_plugin_never_reaches_the_game()
    {
        var r = RunFixture("put /owned\n", _ => null);

        Assert.Contains("/owned", r.Seen);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Bare_slash_line_swallowed_by_a_plugin_never_reaches_the_game()
    {
        var r = RunFixture("/owned\n", _ => null);

        Assert.Contains("/owned", r.Seen);
        Assert.Empty(r.Sent);
    }

    /// <summary>The issue's own DragonRealms repro.</summary>
    [Fact]
    public void Tracker_style_timers_command_is_claimed_by_the_plugin()
    {
        var r = RunFixture("send /timers start CompatibilityTest\nexit\n",
                           cmd => cmd.StartsWith("/timers") ? null : cmd);

        Assert.Contains("/timers start CompatibilityTest", r.Seen);
        Assert.Empty(r.Sent);
    }

    // ── transform + fall-through ─────────────────────────────────────────────

    [Fact]
    public void Plugin_can_rewrite_a_script_slash_command()
    {
        // The rewrite lands, and the gate then runs on the REWRITTEN line: '/new'
        // is still a mycommandchar line, so it stays client-side. A plugin that
        // wants its command to reach DragonRealms rewrites it to a game verb —
        // see A_plugin_rewrite_to_a_game_verb_still_reaches_the_game. The rewrite
        // is not re-offered to the plugins (Genie 4 did not re-enter ParseInput).
        var r = RunFixture("put /old\n", cmd => cmd == "/old" ? "/new" : cmd);

        Assert.Contains("/old", r.Seen);
        Assert.Contains("/new", r.Echoed);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Unhandled_slash_command_is_kept_local_and_never_reaches_the_game()
    {
        // Typed-input parity, correctly stated this time. The superseded version
        // of this test asserted the opposite and justified it as "typed-input
        // parity: a '/…' nothing owns is ordinary game text" — but typed input
        // has never behaved that way: GenieCore.SendToGame gates the socket write
        // on Config.MyCommandChar, so a typed '/unknown thing' is echoed and
        // dropped. Scripts bypass that sink, so they alone leaked the line to
        // DragonRealms, which answered "Please rephrase that command."
        var r = RunFixture("put /unknown thing\n");

        Assert.Contains("/unknown thing", r.Seen);   // plugins still get their offer
        Assert.Empty(r.Sent);                        // …but nothing reaches the game
    }

    [Fact]
    public void Unclaimed_slash_command_explains_itself_once()
    {
        // Silence was the field failure: a restart routine's `/restart …` looked
        // like it ran, and the `quit` on the next line logged the character out.
        var r = RunFixture("put /restart CharProfile .uber\nput /restart CharProfile .uber\n");

        var warnings = r.Echoed.FindAll(e => e.StartsWith("[genie] script sent"));
        Assert.Single(warnings);                                     // once per distinct command
        Assert.Contains("/restart CharProfile .uber", warnings[0]);
        Assert.Contains("did NOT reach the game",     warnings[0]);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void A_custom_mycommandchar_is_held_back_without_the_plugin_warning()
    {
        // `#config mycommandchar ~` is the deliberate trigger-feed idiom, not a
        // missing plugin — hold the line back, but stay quiet about it. It never
        // enters the '/' offer path, so the plugins are not consulted either.
        var r = RunFixture("put ~500\n", configure: c => c.MyCommandChar = '~');

        Assert.Empty(r.Seen);
        Assert.Empty(r.Sent);
        Assert.DoesNotContain(r.Echoed, e => e.StartsWith("[genie] script sent"));
        Assert.Contains("~500", r.Echoed);           // still echoed, Genie 4 parity
    }

    [Fact]
    public void A_slash_line_stays_game_bound_when_mycommandchar_is_not_slash()
    {
        // Mirror of SendToGame: the gate keys on MyCommandChar, so with '~'
        // configured an unclaimed '/…' is ordinary game text again.
        var r = RunFixture("put /unknown thing\n", configure: c => c.MyCommandChar = '~');

        Assert.Contains("/unknown thing", r.Seen);   // '/' still drives the plugin offer
        Assert.Contains("/unknown thing", r.Sent);
    }

    [Fact]
    public void A_plugin_rewrite_to_a_game_verb_still_reaches_the_game()
    {
        // The gate runs on the REWRITTEN line, so a plugin that turns its own
        // command into a real verb is unaffected by the hold-back.
        var r = RunFixture("put /bless\n", cmd => cmd == "/bless" ? "prep bless" : cmd);

        Assert.Contains("prep bless", r.Sent);
    }

    [Fact]
    public void A_held_back_slash_does_not_consume_the_typeahead_budget()
    {
        // Same load-bearing constraint as a swallowed command: _inFlight is
        // released only by a game prompt, so holding the line back must not bump
        // it or the following send waits forever.
        var r = RunFixture("put /unknown thing\nput look\n");

        Assert.Contains("look", r.Sent);
        Assert.DoesNotContain("/unknown thing", r.Sent);
    }

    // The narrow-scope guarantee: the bare-verb corpus is untouched, so the
    // community script body can't regress through a plugin's OnInput. One
    // game-bound send per fixture — a second would sit behind the type-ahead
    // gate forever, since no prompt arrives to release _inFlight.
    [Theory]
    [InlineData("put bob\n",       "bob")]
    [InlineData("look\n",          "look")]
    [InlineData("move north\n",    "north")]
    [InlineData("send prep mb\n",  "prep mb")]
    public void Ordinary_game_verbs_never_enter_plugin_input_dispatch(string body, string expected)
    {
        var r = RunFixture(body);

        Assert.Empty(r.Seen);
        Assert.Contains(expected, r.Sent);
    }

    // ── ordering + the type-ahead budget ─────────────────────────────────────

    [Fact]
    public void Builtin_extensions_get_first_refusal_before_plugins()
    {
        // Matches the typed-input ordering in GenieCore.ProcessInputCore:
        // extensions claim first, so the plugin chain is never even consulted.
        var r = RunFixture("put /probe weapon\n", registerProbe: true);

        Assert.Contains("/probe weapon", r.Probe.Claimed);
        Assert.Empty(r.Seen);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Swallowed_slash_does_not_consume_the_typeahead_budget()
    {
        // The load-bearing constraint: _inFlight is released only by a game
        // prompt, so a swallowed command must never bump it — otherwise the
        // next game-bound send waits forever for a prompt that never comes.
        // This is why the offer sits before the increment rather than inside
        // the sendCommand delegate.
        var r = RunFixture("send /owned\nput look\n", cmd => cmd == "/owned" ? null : cmd);

        Assert.Contains("/owned", r.Seen);
        Assert.Contains("look",   r.Sent);
        Assert.DoesNotContain("/owned", r.Sent);
    }

    // ── the other send paths ─────────────────────────────────────────────────

    [Fact]
    public void Semicolon_tail_slash_is_offered_to_plugins()
    {
        // The tail drains through PendingSends, a separate send site.
        var r = RunFixture("put /owned first;/owned second\n", _ => null);

        Assert.Contains("/owned first",  r.Seen);
        Assert.Contains("/owned second", r.Seen);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Delayed_send_slash_is_offered_to_plugins()
    {
        // `send <delay> /cmd` fires from the PendingSends drain once the delay
        // elapses — the same site, reached the other way.
        var seen = new List<string>();
        var sent = new List<string>();
        var dir  = Path.Combine(Path.GetTempPath(), "gc_plugincmd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), "send 0.05 /owned delayed\n");
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: c => sent.Add(c), echo: _ => { });
            engine.PluginInput = cmd => { seen.Add(cmd); return null; };
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 60 && seen.Count == 0; i++)
            {
                engine.Tick();
                System.Threading.Thread.Sleep(5);   // let the 0.05s send gate elapse
            }

            Assert.Contains("/owned delayed", seen);
            Assert.Empty(sent);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Move_slash_command_is_offered_to_plugins()
    {
        // `move` was the one send site with no client-side offer at all — not
        // even to the built-in extensions (public #325 review finding).
        var r = RunFixture("move /owned\n", _ => null);

        Assert.Contains("/owned", r.Seen);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Move_with_an_ordinary_direction_still_sends()
    {
        var r = RunFixture("move go bridge\n");

        Assert.Empty(r.Seen);
        Assert.Contains("go bridge", r.Sent);
    }

    [Fact]
    public void Claimed_move_does_not_hang_the_script_waiting_for_a_room_change()
    {
        // `move` normally parks the script on PauseUntil.MaxValue until a room
        // change arrives. A claimed command sends nothing, so no room change is
        // ever coming — the script must continue instead of hanging forever.
        var r = RunFixture("move /owned\nput look\n", _ => null);

        Assert.Contains("/owned", r.Seen);
        Assert.Contains("look",   r.Sent);   // the line after `move` ran
        Assert.DoesNotContain("/owned", r.Sent);
    }

    [Fact]
    public void Js_put_slash_command_is_offered_to_plugins()
    {
        // The JS interop bridge (genie.put / inline <% put() %>) went straight
        // to the socket — it bypassed even the built-in extension dispatch that
        // be23134 added, an unnoticed hole in that fix.
        var r = RunFixture("<%\nput(\"/owned\");\n%>\n", _ => null);

        Assert.Contains("/owned", r.Seen);
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Js_put_ordinary_command_still_sends()
    {
        var r = RunFixture("<%\nput(\"look\");\n%>\n");

        Assert.Empty(r.Seen);
        Assert.Contains("look", r.Sent);
    }

    [Fact]
    public void Standalone_js_array_script_gets_the_same_offer_and_hold_back()
    {
        // The inline `<% … %>` bridge above runs through JsLibraryContext, which
        // already routed to ResolveOutboundCommand. A standalone .js array script
        // runs on JsScriptRuntime, whose send delegate was wired straight to the
        // raw sink — the last sink still bypassing both the client-side offer and
        // the mycommandchar gate.
        var seen = new List<string>();
        var sent = new List<string>();
        var dir  = Path.Combine(Path.GetTempPath(), "gc_plugincmd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.js"),
                              "genie.put('/owned');\ngenie.put('look');\n");
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: c => sent.Add(c), echo: _ => { });
            engine.PluginInput = cmd => { seen.Add(cmd); return cmd; };
            Assert.True(engine.TryStart("t", new List<string>()));

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !sent.Contains("look"))
            {
                engine.Tick();
                System.Threading.Thread.Sleep(10);
            }

            Assert.Contains("/owned", seen);        // plugins got their offer
            Assert.DoesNotContain("/owned", sent);  // …and the line stayed local
            Assert.Contains("look", sent);          // ordinary verbs unaffected
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ── host-not-wired safety ────────────────────────────────────────────────

    [Fact]
    public void Engine_without_a_plugin_host_still_holds_slashes_back()
    {
        // PluginInput is null in the TestHarness and headless tests. Nothing can
        // claim the line there, which makes it MORE certain to be local-only, not
        // less — the mycommandchar gate does not depend on a plugin host being
        // wired, exactly as GenieCore.SendToGame's does not for typed input.
        var sent = new List<string>();
        var dir  = Path.Combine(Path.GetTempPath(), "gc_plugincmd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), "put /owned\n");
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: c => sent.Add(c), echo: _ => { });
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 200; i++) engine.Tick();

            Assert.Empty(sent);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
