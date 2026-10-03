using System;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 script-language parity for the expression evaluator (Group A of the
/// parity audit). Locks in: match() = exact case-sensitive equality (not the
/// old case-insensitive substring), the `eq` / `&lt;&gt;` operators, and the
/// instr/instring/substring/defined aliases.
/// </summary>
public class ScriptExpressionTests
{
    private static ScriptInstance NewInst()
    {
        var inst = new ScriptInstance();
        inst.Vars["foo"] = "bar";
        inst.Vars["empty"] = "";
        inst.DollarStack.Push(new string[10]);
        return inst;
    }

    [Theory]
    // match(): exact equality, case-sensitive (Ordinal) — NOT substring
    [InlineData("match(\"abc\",\"abc\")", true)]
    [InlineData("match(\"abc\",\"ab\")",  false)]   // was TRUE under the substring bug
    [InlineData("match(\"abc\",\"ABC\")", false)]   // case-sensitive
    [InlineData("match(\"ab\",\"abc\")",  false)]
    // `eq` word operator ≡ `=`
    [InlineData("5 eq 5", true)]
    [InlineData("5 eq 6", false)]
    [InlineData("\"abc\" eq \"abc\"", true)]
    [InlineData("\"abc\" eq \"ABC\"", false)]
    // `<>` operator ≡ `!=`
    [InlineData("5 <> 6", true)]
    [InlineData("5 <> 5", false)]
    [InlineData("\"a\" <> \"b\"", true)]
    // regression guard: inserting `<>` must not break `<` / `<=` / `>` / `>=`
    [InlineData("3 < 5",  true)]
    [InlineData("5 < 3",  false)]
    [InlineData("5 <= 5", true)]
    [InlineData("6 >= 7", false)]
    [InlineData("8 > 2",  true)]
    // instr / instring are boolean `contains` aliases (G4-faithful, not a position)
    [InlineData("instr(\"hello world\",\"world\")", true)]
    [InlineData("instring(\"hello\",\"xyz\")",      false)]
    [InlineData("contains(\"hello\",\"ell\")",      true)]
    // defined alias of def
    [InlineData("defined(foo)",   true)]
    [InlineData("defined(empty)", true)]    // #129: EXISTS (even set to "") ⇒ defined (Genie 4 ContainsKey)
    [InlineData("defined(nope)",  false)]   // never set ⇒ not defined
    [InlineData("def(foo)",       true)]
    // Group B: string predicates are case-SENSITIVE (Genie 4 parity)
    [InlineData("contains(\"Hello\",\"hello\")", false)]   // case mismatch ⇒ no match
    [InlineData("contains(\"Hello\",\"Hel\")",   true)]    // same-case still matches (see Group A block)
    [InlineData("startswith(\"Hello\",\"hel\")", false)]
    [InlineData("startswith(\"Hello\",\"Hel\")", true)]
    [InlineData("endswith(\"Hello\",\"LO\")",    false)]
    [InlineData("endswith(\"Hello\",\"lo\")",    true)]
    // Group B: indexof is 1-based ⇒ not-found is 0 (falsy); the GenieHunter/
    // hunt.cmd idiom `if !indexof(hay, needle)` means "needle absent".
    [InlineData("!indexof(\"longsword\",\"$\")", true)]    // no $  ⇒ absent ⇒ fires
    [InlineData("!indexof(\"a$b\",\"$\")",       false)]   // $ present ⇒ doesn't fire
    [InlineData("indexof(\"Hello\",\"h\")",      false)]   // case-sensitive miss ⇒ 0 ⇒ falsy
    // Bare MULTI-WORD operands (Genie 4 splits on operators, so spaces are
    // legal inside unquoted text). mm_train: `if (%guild = Moon Mage)`,
    // `if $selection = DIVINATION TOOL`, and Menu.Build's `!($5 = "")` where
    // $5 substitutes to "Moonmage Training Menu". Before the fix these threw
    // (bad condition ⇒ silently false).
    // Genie 4 BuildArgs parity: commas between function args are optional
    // (uber.cmd: `replacere("%x", "\|+" "|")`, `matchre("%a" "(?i)b")`).
    [InlineData("replacere(\"a||b\", \"\\|+\" \"|\") = \"a|b\"",   true)]
    [InlineData("replace(\"a||b\", \"||\" \"|\") = \"a|b\"",      true)]
    [InlineData("matchre(\"Hello\" \"(?i)hello\")",              true)]
    [InlineData("Moon Mage = Moon Mage",                     true)]
    [InlineData("(Moonmage Training Menu = \"\")",           false)]
    [InlineData("!(Moonmage Training Menu = \"\")",          true)]
    [InlineData("DIVINATION TOOL = DIVINATION TOOL",         true)]
    [InlineData("Moon Mage = Warrior Mage",                  false)]
    // ...and the word operators still terminate a bare operand:
    [InlineData("Moon Mage eq Moon Mage",                    true)]
    [InlineData("Moon Mage = Moon Mage and 1 = 1",           true)]
    [InlineData("Moon Mage = Barbarian or 2 = 2",            true)]
    public void EvalBool_matches_Genie4(string expr, bool expected)
        => Assert.Equal(expected, ScriptExpression.EvalBool(expr, NewInst()));

    [Theory]
    // Group B: indexof / lastindexof are 1-based and case-sensitive (Genie 4).
    [InlineData("indexof(\"hello\",\"ell\")",   "2")]   // 0-based 1 → 1-based 2
    [InlineData("indexof(\"hello\",\"h\")",     "1")]
    [InlineData("indexof(\"hello\",\"xyz\")",   "0")]   // not found ⇒ 0
    [InlineData("indexof(\"Hello\",\"h\")",     "0")]   // case-sensitive miss ⇒ 0
    [InlineData("lastindexof(\"hello\",\"l\")", "4")]   // last l: 0-based 3 → 4
    [InlineData("lastindexof(\"hello\",\"z\")", "0")]
    public void Eval_indexof_is_1based_and_caseSensitive(string expr, string expected)
        => Assert.Equal(expected, ScriptExpression.Eval(expr, NewInst())?.ToString());

    [Theory]
    [InlineData("substring(\"hello\",1,3)", "ell")]
    [InlineData("substr(\"hello\",1,3)",    "ell")]
    [InlineData("equipment", "equipment")]  // `eq` must NOT fire inside an identifier
    public void Eval_string_results(string expr, string expected)
        => Assert.Equal(expected, ScriptExpression.Eval(expr, NewInst())?.ToString());

    [Theory]
    // #133: a missing operand (an unset %var substituted to nothing) reads as
    // the empty string, so the defined side of an || still evaluates. Before
    // the fix, "( = 1)" was a parse error that failed the WHOLE condition.
    [InlineData("((1 = 1) || ( = 1))", true)]    // the #133 repro shape
    [InlineData("(( = 1) || (1 = 1))", true)]    // undefined side first
    [InlineData("( = 1)",              false)]   // "" = "1"
    [InlineData("( != 1)",             true)]    // "" != "1"
    [InlineData("(1 = )",              false)]   // missing right side
    [InlineData("()",                  false)]   // fully-empty substitution
    // #135: chained || (3+ clauses) — well-formed form works; the report's
    // repro had an unbalanced quote, which correctly errors.
    [InlineData("((\"foo\" = \"foo\") || (\"foo\" = \"bar\") || (\"foo\" = \"baz\"))", true)]
    [InlineData("((\"zap\" = \"foo\") || (\"zap\" = \"bar\") || (\"zap\" = \"baz\"))", false)]
    public void EvalBool_missing_operands_and_chained_or(string expr, bool expected)
        => Assert.Equal(expected, ScriptExpression.EvalBool(expr, NewInst()));

    [Theory]
    // #134: count() counts OCCURRENCES (Genie 4 Eval.cs Count), not elements.
    [InlineData("count(\"barbar\",\"foo\")", "0")]   // the #134 repro: absent ⇒ 0
    [InlineData("count(\"foo\",\"foo\")",    "1")]
    [InlineData("count(\"barbar\",\"bar\")", "2")]
    [InlineData("count(\"a|b|c\",\"|\")",    "2")]   // pipe-list idiom: elements − 1
    [InlineData("count(\"\",\"foo\")",       "0")]
    [InlineData("count(\"abc\",\"\")",       "0")]   // G4 hangs here; we return 0
    public void Eval_count_counts_occurrences(string expr, string expected)
        => Assert.Equal(expected, ScriptExpression.Eval(expr, NewInst())?.ToString());

    [Theory]
    // #323: element() clamps like Genie 4 (Eval.cs:1204) instead of returning ""
    // out of range. Order matters — clamp high, then wrap a negative from the
    // end, then floor at the first element.
    [InlineData("element(\"a|b|c\",0)",  "a")]    // in range, unchanged
    [InlineData("element(\"a|b|c\",2)",  "c")]
    [InlineData("element(\"a|b|c\",3)",  "c")]    // one past the end — mm_train's shape
    [InlineData("element(\"a|b|c\",5)",  "c")]    // far past the end
    [InlineData("element(\"a|b|c\",-1)", "c")]    // negative counts from the end
    [InlineData("element(\"a|b|c\",-3)", "a")]
    [InlineData("element(\"a|b|c\",-9)", "a")]    // past the start floors at the first
    // parentheses are stripped from a pipe list before splitting, G4-style
    [InlineData("element(\"(a|b|c)\",0)", "a")]
    [InlineData("element(\"(a|b|c)\",2)", "c")]
    [InlineData("element(\"a|(b)|c\",1)", "b")]   // stripped anywhere, not just the ends
    // degenerate lists still land on a real element rather than throwing
    [InlineData("element(\"\",0)",       "")]
    [InlineData("element(\"\",4)",       "")]
    [InlineData("element(\"solo\",7)",   "solo")]
    // the third separator argument is our extension: it clamps the same way, but
    // keeps parentheses, because that list is the caller's format and not a G4
    // pipe list.
    [InlineData("element(\"a,b,c\",9,\",\")",     "c")]
    [InlineData("element(\"a,b,c\",-1,\",\")",    "c")]
    [InlineData("element(\"(a),(b)\",0,\",\")",   "(a)")]
    [InlineData("element(\"(a|b)\",0,\"|\")",     "a")]   // explicit '|' still strips
    public void Eval_element_clamps_like_Genie4(string expr, string expected)
        => Assert.Equal(expected, ScriptExpression.Eval(expr, NewInst())?.ToString());
    [Theory]
    // Genie 4 MathEval.cs: `log` is base-10 (same as `log10`); `ln` is natural.
    [InlineData("log(100)",   2.0)]
    [InlineData("log10(1000)", 3.0)]
    [InlineData("ln(1)",      0.0)]
    // Trig works in radians, straight Math calls (MathEval.cs sin/cos/tan/arc*).
    [InlineData("sin(0)",     0.0)]
    [InlineData("cos(0)",     1.0)]
    [InlineData("tan(0)",     0.0)]
    [InlineData("arcsin(1)",  Math.PI / 2)]
    [InlineData("arccos(1)",  0.0)]
    [InlineData("arctan(1)",  Math.PI / 4)]
    public void Eval_math_functions_match_Genie4(string expr, double expected)
        => Assert.Equal(expected, ScriptExpression.ToNum(ScriptExpression.Eval(expr, NewInst())), 10);

    [Fact]
    public void Ln_is_natural_log_and_log_is_not()
    {
        Assert.Equal(1.0, ScriptExpression.ToNum(ScriptExpression.Eval("ln(2.718281828459045)", NewInst())), 10);
        Assert.Equal(Math.Log10(Math.E),
                     ScriptExpression.ToNum(ScriptExpression.Eval("log(2.718281828459045)", NewInst())), 10);
    }

    [Fact]
    public void Matchre_updates_argcount_on_the_existing_frame()
    {
        // TryMatch parity: overwriting $0..$9 in place also rewrites the frame's
        // DollarCounts entry, so $argcount describes the captures.
        var inst = new ScriptInstance();
        inst.DollarStack.Push(new string[10]);
        inst.DollarCounts.Push(0);

        Assert.True(ScriptExpression.EvalBool("matchre(\"ab cd\", \"([a-z]+) ([a-z]+)\")", inst));

        Assert.Single(inst.DollarStack);
        Assert.Single(inst.DollarCounts);
        Assert.Equal(2, inst.DollarCounts.Peek());
        Assert.Equal("cd", inst.DollarStack.Peek()[2]);
    }

    [Fact]
    public void Matchre_pushing_a_fresh_frame_keeps_the_stacks_in_lockstep()
    {
        var inst = new ScriptInstance();   // no frame at all

        Assert.True(ScriptExpression.EvalBool("matchre(\"xyz\", \"(y)\")", inst));

        Assert.Equal(inst.DollarStack.Count, inst.DollarCounts.Count);
        Assert.Equal(1, inst.DollarCounts.Peek());
        Assert.Equal("y", inst.DollarStack.Peek()[1]);
    }
}
