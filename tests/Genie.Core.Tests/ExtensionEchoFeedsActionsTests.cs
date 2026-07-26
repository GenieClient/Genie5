using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Extensions;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 parity: text a built-in extension writes to the game window (via
/// <see cref="IExtensionHost.Echo"/>) is matchable by script <c>action</c>/
/// <c>match</c>/<c>waitfor</c> — not display-only. This is the root cause of the
/// uber-combat "ALL WEAPON SKILLS CAPPED" bug: CircleCalc's <c>/sort</c> emits
/// the sorted skills via Echo, and the script's <c>action … when ^(%list)…</c>
/// is meant to capture them. Before the fix, Echo bypassed the action engine.
/// </summary>
public class ExtensionEchoFeedsActionsTests
{
    // Minimal extension: when it sees the line "DOSORT", it writes a "SORTED …"
    // line to the game window through the host — exactly CircleCalc's shape.
    private sealed class EmittingExtension : IGameExtension
    {
        private IExtensionHost? _host;
        public string Name => "emitter";
        public string Version => "1.0";
        public string Description => "test";
        public bool Enabled { get; set; } = true;
        public void Initialize(IExtensionHost host) => _host = host;
        public void OnGameLine(string line)
        {
            if (line.Trim() == "DOSORT")
                _host?.Echo("SORTED Small Edged - 727");
        }
        public void OnCommandSent(string command) { }
        public void OnPrompt() { }
        public void Shutdown() { }
    }

    private static (ScriptEngine engine, List<string> echoed) NewEngine(string dir)
    {
        var echoed = new List<string>();
        ScriptEngine? e = null;
        // injectGameLine mirrors GenieCore.InjectParsedLine: routes the fed line
        // back into the script game-line path (where actions/matches run).
        e = new ScriptEngine(dir, new TypeAheadSession(),
                             sendCommand: _ => { },
                             echo: l => echoed.Add(l),
                             injectGameLine: l => e!.OnGameLine(l));
        return (e, echoed);
    }

    [Fact]
    public void Extension_echo_reaches_a_script_action()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_extecho_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Action fires an unmistakable marker so we can see it in the echo log.
            File.WriteAllText(Path.Combine(dir, "t.cmd"),
                "action echo ACTION_FIRED_$1 when ^SORTED (.+) -\n" +
                "pause 100\n");

            var (engine, echoed) = NewEngine(dir);
            engine.Extensions.Register(new EmittingExtension());
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 5; i++) engine.Tick();     // let the action register

            // A game line the extension reacts to → it Echoes the SORTED line →
            // that echo must reach the registered action.
            engine.OnGameLine("DOSORT");
            for (int i = 0; i < 5; i++) engine.Tick();

            // The extension's line displayed exactly once…
            Assert.Equal(1, echoed.Count(l => l == "SORTED Small Edged - 727"));
            // …and the action captured it (proving Echo fed the action engine).
            Assert.Contains(echoed, l => l == "ACTION_FIRED_Small Edged");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Extension_echo_is_not_re_dispatched_to_extensions()
    {
        // Re-entrancy guard: the fed-back echo must NOT be handed to extensions'
        // OnGameLine again (that would loop / double-count). We assert the
        // emitter fires exactly once for one "DOSORT".
        var dir = Path.Combine(Path.GetTempPath(), "gc_extecho2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), "pause 100\n");
            var (engine, echoed) = NewEngine(dir);
            engine.Extensions.Register(new EmittingExtension());
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 3; i++) engine.Tick();

            engine.OnGameLine("DOSORT");
            for (int i = 0; i < 3; i++) engine.Tick();

            // Exactly one emission — no runaway loop from re-dispatch.
            Assert.Equal(1, echoed.Count(l => l == "SORTED Small Edged - 727"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
