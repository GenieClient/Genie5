using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 parity (Script.cs ParseVariable): <c>%name(...)</c> is array
/// indexing only when the parenthesised index is an INTEGER. Otherwise the
/// parentheses are literal text that follows the variable — e.g. a regex
/// capture group right after a prefix variable. Genie 5 used to swallow the
/// group, which made uber.cmd's weapon-on-floor check
/// (<c>\b%IgnoreAdjectives(%MYWEAPONS)\b</c>) match everything and run
/// <c>gosub GET $1</c> with an empty $1.
/// </summary>
public class VarParenGroupAfterNameTests
{
    private static List<string> Run(string body)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_vparen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 200; i++) engine.Tick();
            return echoed;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Group_after_prefix_variable_survives_and_captures()
    {
        var o = Run(
            "var IA (?<!x )\n" +
            "var MW a|gladius\n" +
            "if matchre(\"a gladius\", \"\\b%IA(%MW)\\b\") then echo CAP=[$1]\n" +
            "exit\n");
        Assert.Contains("CAP=[a]", o);   // group kept: first alternative of (a|gladius)
    }

    [Fact]
    public void Non_numeric_group_text_is_kept_literally()
    {
        var o = Run(
            "var IA zz\n" +
            "echo [%IA(gladius)]\n" +
            "exit\n");
        Assert.Contains("[zz(gladius)]", o);
    }

    [Fact]
    public void Numeric_index_still_selects_the_array_element()
    {
        var o = Run(
            "var Bags pack|sack|belt\n" +
            "echo [%Bags(0)][%Bags(2)][%Bags(9)]\n" +
            "exit\n");
        Assert.Contains("[pack][belt][]", o);
    }
}
