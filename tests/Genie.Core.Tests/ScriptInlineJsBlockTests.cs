using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #322 — Genie 4's inline &lt;% … %&gt; JavaScript blocks in a .cmd script.
/// Before this, &lt;% fell through as an ordinary script line and was SENT TO THE
/// GAME ("Please rephrase that command."), and the JS body was interpreted as
/// script lines.
///
/// The fixtures are the two real scripts the gap broke: the community
/// TEXT_TO_NUMBER helper (word-form number to integer) and skilldata.cmd's
/// skill-list sort.
/// </summary>
public class ScriptInlineJsBlockTests
{
    private sealed record Run(List<string> Echoed, List<string> Sent);

    private static Run RunFixture(string body, int ticks = 400)
    {
        var echoed = new List<string>();
        var sent   = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_jsblock_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: c => sent.Add(c),
                                          echo: l => echoed.Add(l));
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < ticks; i++) engine.Tick();
            return new Run(echoed, sent);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    private static List<string> Results(Run r) => r.Echoed.FindAll(l => l.StartsWith("T:"));

    // ── the reported repro ───────────────────────────────────────────────────

    /// <summary>The issue's own script: a block reads a local with getVar, computes,
    /// and writes back with setVar; the NEXT .cmd line must see the value — i.e. the
    /// block runs synchronously and in source order.</summary>
    [Fact]
    public void TextToNumber_BlockSetsVariableVisibleToNextLine()
    {
        var r = RunFixture(@"var number_as_string seventy eight
<%
   var map = { 'seventy': 70, 'eight': 8 };
   var parts = String(getVar('number_as_string')).toLowerCase().split(' ');
   var n = 0;
   for (var i = 0; i < parts.length; i++) {
      var m = map[parts[i]];
      if (m !== undefined) n += m;
   }
   setVar('text_to_number', n);
%>
echo T:%text_to_number
");
        Assert.Equal(new[] { "T:78" }, Results(r));
    }

    /// <summary>The regression that started the report: no part of the block —
    /// least of all the marker — may reach the game.</summary>
    [Fact]
    public void BlockIsNeverSentToTheGame()
    {
        var r = RunFixture(@"<%
   setVar('x', 'ok');
%>
echo T:%x
");
        Assert.Equal(new[] { "T:ok" }, Results(r));
        Assert.Empty(r.Sent);
    }

    // ── Genie 4 surface parity ───────────────────────────────────────────────

    /// <summary>Genie 4's JsGetVariable returns the STRING "undefined" for an unset
    /// local, and block bodies are written against that. Faithful port.</summary>
    [Fact]
    public void GetVar_OnUnsetLocal_ReturnsUndefinedString_InsideBlock()
    {
        var r = RunFixture(@"<%
   setVar('probe', getVar('no_such_variable'));
%>
echo T:%probe
");
        Assert.Equal(new[] { "T:undefined" }, Results(r));
    }

    /// <summary>...but js/jscall keep the shipped #104 contract of "" on a miss, so
    /// existing .js libraries are unaffected by the block semantics above.</summary>
    [Fact]
    public void GetVar_OnUnsetLocal_StillReturnsEmpty_ForJsCall()
    {
        var r = RunFixture(@"jscall probe getVar('no_such_variable')
echo T:[%probe]
");
        Assert.Equal(new[] { "T:[]" }, Results(r));
    }

    /// <summary>One engine per script: a function defined in one block stays callable
    /// from a later block (Genie 4 reuses the script's engine across blocks).</summary>
    [Fact]
    public void EngineStatePersistsAcrossBlocks()
    {
        var r = RunFixture(@"<%
   function twice(n) { return n * 2; }
%>
<%
   setVar('out', twice(21));
%>
echo T:%out
");
        Assert.Equal(new[] { "T:42" }, Results(r));
    }

    /// <summary>Globals bridge: getGlobal/setGlobal reach $vars.</summary>
    [Fact]
    public void GlobalsBridgeWorksFromBlock()
    {
        var r = RunFixture(@"<%
   setGlobal('blocktest', 'seen');
%>
echo T:$blocktest
");
        Assert.Equal(new[] { "T:seen" }, Results(r));
    }

    // ── parser hazards these blocks would otherwise trip ─────────────────────

    /// <summary>A JS body containing a bare default: must NOT register as a .cmd
    /// label — the label scanner takes any unspaced word ending in a colon. If it
    /// did, a goto elsewhere could jump into the middle of JavaScript.</summary>
    [Fact]
    public void JsLabelLookalikeDoesNotBecomeAScriptLabel()
    {
        var r = RunFixture(@"var hit no
<%
   var k = 2, out = '';
   switch (k) {
      case 1: out = 'one'; break;
      default: out = 'other'; break;
   }
   setVar('out', out);
%>
echo T:%out
");
        Assert.Equal(new[] { "T:other" }, Results(r));
    }

    /// <summary>A then inside a JS string literal must not be rewritten by the
    /// inline-conditional normaliser.</summary>
    [Fact]
    public void ThenInsideJsStringIsNotNormalised()
    {
        var r = RunFixture(@"<%
   if (1 < 2) { setVar('out', 'if x then y'); }
%>
echo T:%out
");
        Assert.Equal(new[] { "T:if x then y" }, Results(r));
    }

    // ── block delimiter forms (Genie 4 Script.cs:3686) ───────────────────────

    /// <summary>Text trailing the opening marker on that line is JS, and a block may
    /// open and close on one line.</summary>
    [Fact]
    public void SingleLineBlockForm()
    {
        var r = RunFixture(@"<% setVar('out', 'inline'); %>
echo T:%out
");
        Assert.Equal(new[] { "T:inline" }, Results(r));
    }

    /// <summary>An indented block is recognised, and the marker keeps the opening
    /// line's indent so surrounding block structure is unaffected.</summary>
    [Fact]
    public void IndentedBlockInsideIfBlockRuns()
    {
        var r = RunFixture(@"var go 1
if %go = 1 then
{
   <%
      setVar('out', 'ran');
   %>
}
echo T:%out
");
        Assert.Equal(new[] { "T:ran" }, Results(r));
    }

    // ── the second real-world fixture ────────────────────────────────────────

    /// <summary>skilldata.cmd's sort block. Also covers the Genie 4 .length()
    /// compatibility rewrite, which the block path must apply like include/js do —
    /// the script in the wild calls list.length() as a method.</summary>
    [Fact]
    public void SkillDataSortBlockSortsTheList()
    {
        var r = RunFixture(@"var skills Outdoorsmanship|Athletics|Perception
<%
   list = getVar('skills').toString();
   list = list.split('|');
   for(i = 0; i < list.length() - 1; i++)
   {
      if(list[i].localeCompare(list[i+1]) == 1) {
         var temp = list[i];
         list[i] = list[i+1];
         list[i+1] = temp;
         i = -1;
      }
   }
   setVar('skills',list.join('|'));
%>
echo T:%skills
");
        Assert.Equal(new[] { "T:Athletics|Outdoorsmanship|Perception" }, Results(r));
    }

    /// <summary>A JS error inside a block is reported and the script continues — it
    /// must not take down the .cmd tick loop.</summary>
    [Fact]
    public void JsErrorInBlockIsReportedAndScriptContinues()
    {
        var r = RunFixture(@"<%
   this_function_does_not_exist();
%>
echo T:survived
");
        Assert.Equal(new[] { "T:survived" }, Results(r));
        Assert.Contains(r.Echoed, l => l.Contains("block") && l.Contains("JS error"));
    }
}
