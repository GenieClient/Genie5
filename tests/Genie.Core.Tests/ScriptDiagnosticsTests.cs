using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Script-start and match-registration diagnostics. Both come from community
/// debugging sessions: "my fix didn't take" is usually a second copy of the
/// script running from another directory (so the start line names the resolved
/// file), and a <c>match</c> aimed at a label the script never defines can
/// never fire (the matchwait scanner skips unknown labels), turning every hit
/// into a silent timeout — warn at registration, where the typo is.
/// </summary>
public class ScriptDiagnosticsTests
{
    private static (List<string> echoed, string dir) RunFixture(string body)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_diag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            engine.TryStart("t", new List<string>());
            // Interleave short sleeps so the 0.01s matchwait deadlines used
            // below actually elapse between ticks.
            for (int i = 0; i < 200; i++)
            {
                engine.Tick();
                if (i % 20 == 0) System.Threading.Thread.Sleep(5);
            }
            return (echoed, dir);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Action_remove_miss_is_silent_at_default_debug_level()
    {
        // Genie 4 parity (Script.cs:3121): `action remove <pat>` with no
        // matching registration is a silent no-op. Community scripts
        // remove-then-re-register defensively, so uber startups printed
        // "[script] action: no trigger matched" repeatedly and it read as an
        // error (smoke 2026-08-03 finding #10). The note survives only at
        // debuglevel ≥3.
        var (o, _) = RunFixture(
            "action remove Guild\\: (\\S+)\n" +
            "echo after\n");

        Assert.Contains("after", o);
        Assert.DoesNotContain(o, l => l.Contains("no trigger matched"));
    }

    [Fact]
    public void Action_remove_miss_still_traces_at_debug_three()
    {
        var (o, _) = RunFixture(
            "debug 3\n" +
            "action remove Guild\\: (\\S+)\n" +
            "echo after\n");

        Assert.Contains("after", o);
        Assert.Contains(o, l => l.Contains("no trigger matched"));
    }

    [Fact]
    public void Start_line_names_the_resolved_file()
    {
        var (o, dir) = RunFixture("echo hi\n");

        Assert.Contains(o, l => l.StartsWith("[script] t started (")
                             && l.Contains(Path.Combine(dir, "t.cmd")));
    }

    [Fact]
    public void Match_to_unknown_label_warns_at_registration()
    {
        var (o, _) = RunFixture(
            "matchre LOCATION.unload ^You should unload\n" +
            "matchwait 0.01\n" +
            "echo done\n");

        Assert.Contains("done", o);
        Assert.Contains(o, l => l.Contains("match label 'LOCATION.unload' not found"));
    }

    [Fact]
    public void Unknown_match_label_warns_once_per_label()
    {
        var (o, _) = RunFixture(
            "var i 0\n" +
            "top:\n" +
            "math i add 1\n" +
            "matchre NOPE ^never\n" +
            "matchwait 0.01\n" +
            "if (%i < 3) then goto top\n" +
            "echo done\n");

        Assert.Contains("done", o);
        Assert.Equal(1, o.Count(l => l.Contains("match label 'NOPE' not found")));
    }

    [Fact]
    public void Match_to_defined_label_stays_silent()
    {
        var (o, _) = RunFixture(
            "matchre FOUND ^never\n" +
            "matchwait 0.01\n" +
            "FOUND:\n" +
            "echo done\n");

        Assert.Contains("done", o);
        Assert.DoesNotContain(o, l => l.Contains("not found"));
    }
}
