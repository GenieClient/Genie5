using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Extensions;
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

    private sealed record Run(List<string> Seen, List<string> Sent, ProbeExtension Probe);

    /// <summary>Run a .cmd body against a fresh engine whose PluginInput records
    /// every offer and applies <paramref name="transform"/> (default: pass through
    /// unchanged — the "no plugin claims it" case). Returns what the plugin saw
    /// and what actually reached the send delegate.</summary>
    private static Run RunFixture(string body, Func<string, string?>? transform = null,
                                  int ticks = 400, bool registerProbe = false)
    {
        var seen = new List<string>();
        var sent = new List<string>();
        var dir  = Path.Combine(Path.GetTempPath(), "gc_plugincmd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: c => sent.Add(c), echo: _ => { });
            var probe = new ProbeExtension();
            if (registerProbe) engine.Extensions.Register(probe);
            engine.PluginInput = cmd =>
            {
                seen.Add(cmd);
                return transform is null ? cmd : transform(cmd);
            };
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < ticks; i++) engine.Tick();
            return new Run(seen, sent, probe);
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
        var r = RunFixture("put /old\n", cmd => cmd == "/old" ? "/new" : cmd);

        Assert.Contains("/new", r.Sent);
        Assert.DoesNotContain("/old", r.Sent);
    }

    [Fact]
    public void Unhandled_slash_command_keeps_the_existing_game_fallback()
    {
        // Typed-input parity: a '/…' nothing owns is ordinary game text.
        var r = RunFixture("put /unknown thing\n");

        Assert.Contains("/unknown thing", r.Seen);
        Assert.Contains("/unknown thing", r.Sent);
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

    // ── host-not-wired safety ────────────────────────────────────────────────

    [Fact]
    public void Engine_without_a_plugin_host_sends_slashes_verbatim()
    {
        // PluginInput is null in the TestHarness and headless tests; the
        // pre-#325 fall-through must survive.
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

            Assert.Contains("/owned", sent);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
