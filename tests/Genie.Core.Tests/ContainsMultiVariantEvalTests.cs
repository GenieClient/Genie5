using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 #145 (reported by umbravi): an <c>if</c> takes the false path when
/// <c>contains()</c> sits inside a multi-variant (multi-operand) parenthesized
/// condition. Per its maintainer's analysis on that issue, Genie 4's
/// <c>Eval.ParseQueue</c> walks a FLAT segment queue and <c>return</c>s when it
/// meets a <c>SectionEndType</c>, so a nested function call's own closing paren
/// terminates the ENCLOSING parenthesized section early and the operands after
/// it are never evaluated.
///
/// Genie 5 cannot reproduce this by construction: <see cref="ScriptExpression"/>
/// is recursive-descent over a precedence ladder (ParseOr -> ParseAnd -> ParseNot
/// -> ParseCmp -> ParseAdd -> ParseMul -> ParseUnary -> ParseAtom), and
/// ParseIdentOrCall consumes a call's argument list through its own nested
/// ParseOr. A function's closing paren is therefore consumed by the call that
/// opened it and can never close the surrounding group.
///
/// These are port-fidelity guards for GenieClient/Genie5#249 — they assert the
/// Genie 4 behavior is NOT reproduced. The false-expectation half of the table
/// is load-bearing: a parser that bailed out early and yielded true would sail
/// through every true case on its own.
/// </summary>
public class ContainsMultiVariantEvalTests
{
    // Runs one `if <cond> then echo HIT` line with %1="put", %2 unset.
    private static bool Eval(string cond)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_g4145_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var echoed = new List<string>();
        var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                      sendCommand: _ => { }, echo: l => echoed.Add(l));
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), $"if {cond} then echo HIT\n");
            Assert.True(engine.TryStart("t", new[] { "put" }));
            for (int i = 0; i < 100; i++) engine.Tick();
            var bad = echoed.FirstOrDefault(l => l.Contains("bad condition", StringComparison.Ordinal));
            Assert.Null(bad);   // a parse failure is a different defect, not a false result
            return echoed.Any(l => l.Contains("HIT", StringComparison.Ordinal));
        }
        finally
        {
            engine.StopAll();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static IEnumerable<object[]> Cases => new[]
    {
        // ---- umbravi's exact seven (GenieClient/Genie4#145) ----
        new object[] { true,  "(true && true)" },
        new object[] { true,  "contains(\"put|Put|get|Get\", \"%1\")" },
        new object[] { true,  "\"%2\" == \"\"" },
        new object[] { true,  "(contains(\"put|Put|get|Get\", \"%1\") || \"%2\" == \"\")" },
        new object[] { true,  "(contains(\"put\", \"%1\") && \"%2\" == \"\")" },
        new object[] { true,  "(!contains(\"inverse\", \"%1\"))" },
        new object[] { true,  "(!contains(\"inverse multi\", \"%1\") && \"%2\" == \"\")" },

        // ---- shapes umbravi did not cover: function on the RIGHT of the operator ----
        new object[] { true,  "(\"%2\" == \"\" && contains(\"put|Put\", \"%1\"))" },
        new object[] { true,  "(\"%2\" == \"x\" || contains(\"put\", \"%1\"))" },

        // ---- two function calls in one parenthesized condition ----
        new object[] { true,  "(contains(\"put\", \"%1\") && contains(\"put|get\", \"%1\"))" },
        new object[] { true,  "(contains(\"nope\", \"%1\") || contains(\"put\", \"%1\"))" },
        new object[] { true,  "(!contains(\"inverse\", \"%1\") && !contains(\"nope\", \"%1\"))" },

        // ---- three or more terms ----
        new object[] { true,  "(contains(\"put\", \"%1\") && \"%2\" == \"\" && contains(\"put\", \"%1\"))" },
        new object[] { true,  "(contains(\"nope\", \"%1\") || contains(\"nope2\", \"%1\") || contains(\"put\", \"%1\"))" },

        // ---- extra nesting around each operand ----
        new object[] { true,  "((contains(\"put\", \"%1\")) && (\"%2\" == \"\"))" },
        new object[] { true,  "((contains(\"put\", \"%1\") && \"%2\" == \"\") || \"%2\" == \"x\")" },

        // ---- a function call as another function's argument ----
        new object[] { true,  "(contains(replace(\"PUT\", \"PUT\", \"put\"), \"%1\") && \"%2\" == \"\")" },
        new object[] { true,  "(len(\"%1\") = 3 && contains(\"put\", \"%1\"))" },

        // ---- other functions in the same multi-variant shape ----
        new object[] { true,  "(startswith(\"putter\", \"%1\") && \"%2\" == \"\")" },
        new object[] { true,  "(endswith(\"input\", \"%1\") && \"%2\" == \"\")" },
        new object[] { true,  "(match(\"put\", \"%1\") && \"%2\" == \"\")" },
        new object[] { true,  "(count(\"a|b|c\", \"|\") = 2 && contains(\"put\", \"%1\"))" },

        // ---- FALSE cases: a parser that early-returns true would pass everything above ----
        new object[] { false, "(contains(\"nope\", \"%1\") && \"%2\" == \"\")" },
        new object[] { false, "(contains(\"put\", \"%1\") && \"%2\" == \"x\")" },
        new object[] { false, "(contains(\"nope\", \"%1\") || contains(\"nope2\", \"%1\"))" },
        new object[] { false, "(!contains(\"put\", \"%1\") && \"%2\" == \"\")" },
        new object[] { false, "(contains(\"put\", \"%1\") && !contains(\"put\", \"%1\"))" },
        new object[] { false, "(\"%2\" == \"\" && contains(\"nope\", \"%1\"))" },
        new object[] { false, "((contains(\"put\", \"%1\")) && (\"%2\" == \"x\"))" },
        new object[] { false, "(startswith(\"putter\", \"%1\") && \"%2\" == \"x\")" },
        new object[] { false, "(contains(\"put\", \"%1\") && \"%2\" == \"\" && contains(\"nope\", \"%1\"))" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Multi_variant_contains_evaluates_every_operand(bool expected, string cond)
        => Assert.Equal(expected, Eval(cond));

    /// <summary>
    /// The same shape has to hold in the other two places a condition is
    /// evaluated, not just <c>if</c>: a <c>while</c> head and a <c>waiteval</c>
    /// re-check (the sibling half of #249).
    /// </summary>
    [Fact]
    public void Multi_variant_contains_terminates_a_while_loop()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_cmv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var echoed = new List<string>();
        var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                      sendCommand: _ => { }, echo: l => echoed.Add(l));
        try
        {
            // Loops while BOTH operands hold; the body falsifies the second one,
            // so an early-terminating parse would spin forever (or never enter).
            File.WriteAllText(Path.Combine(dir, "t.cmd"),
                "var go yes\n" +
                "while (contains(\"put\", \"%1\") && \"%go\" == \"yes\")\n" +
                "{\n" +
                "  echo BODY\n" +
                "  var go no\n" +
                "}\n" +
                "echo DONE\n");
            Assert.True(engine.TryStart("t", new[] { "put" }));
            for (int i = 0; i < 200; i++) engine.Tick();

            Assert.Contains(echoed, l => l.Contains("BODY", StringComparison.Ordinal));
            Assert.Contains(echoed, l => l.Contains("DONE", StringComparison.Ordinal));
            Assert.Equal(1, echoed.Count(l => l.Contains("BODY", StringComparison.Ordinal)));
        }
        finally { engine.StopAll(); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Multi_variant_contains_unblocks_a_waiteval()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_cmv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var echoed = new List<string>();
        var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                      sendCommand: _ => { }, echo: l => echoed.Add(l));
        try
        {
            engine.Globals["roomname"] = "Nowhere";
            File.WriteAllText(Path.Combine(dir, "t.cmd"),
                "waiteval (contains(\"put\", \"%1\") && contains(\"$roomname\", \"Crossing\"))\n" +
                "echo PASSED\n");
            Assert.True(engine.TryStart("t", new[] { "put" }));
            for (int i = 0; i < 50; i++) engine.Tick();
            Assert.DoesNotContain(echoed, l => l.Contains("PASSED", StringComparison.Ordinal));

            engine.Globals["roomname"] = "The Crossing, Town Green";
            for (int i = 0; i < 50; i++) engine.Tick();
            Assert.Contains(echoed, l => l.Contains("PASSED", StringComparison.Ordinal));
        }
        finally { engine.StopAll(); try { Directory.Delete(dir, true); } catch { } }
    }
}
