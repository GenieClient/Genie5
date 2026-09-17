using Genie.Core.Macros;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 writes macros.cfg in .NET Keys vocabulary with comma-separated
/// modifiers; Genie 5 resolves a '+'-joined lowercase form built from the live
/// keystroke. Nothing translated, so every imported macro was stored under a
/// key the runtime could never produce and silently never fired.
/// </summary>
public class MacroKeyNormalizerTests
{
    [Theory]
    // Numpad digits — Genie 3/4's ten-key movement pad.
    [InlineData("NumPad0", "num0")]
    [InlineData("NumPad5", "num5")]
    [InlineData("NumPad9", "num9")]
    // Numpad operators.
    [InlineData("Multiply", "num*")]
    [InlineData("Divide",   "num/")]
    [InlineData("Subtract", "num-")]
    [InlineData("Add",      "num+")]
    [InlineData("Decimal",  "num.")]
    // Escape — the kill-switch binding.
    [InlineData("Escape", "esc")]
    // Number-row digits carry a D prefix in Genie 4.
    [InlineData("D0", "0")]
    [InlineData("D7", "7")]
    // Function keys and letters just lowercase.
    [InlineData("F1",  "f1")]
    [InlineData("F12", "f12")]
    [InlineData("Z",   "z")]
    public void Genie4_key_names_map_to_the_runtime_vocabulary(string g4, string expected)
        => Assert.Equal(expected, MacroKeyNormalizer.Normalize(g4));

    [Theory]
    [InlineData("F1, Shift",            "shift+f1")]
    [InlineData("F1, Control",          "ctrl+f1")]
    [InlineData("F1, Alt",              "alt+f1")]
    [InlineData("F1, Shift, Control",   "ctrl+shift+f1")]
    [InlineData("Z, Control, Alt",      "ctrl+alt+z")]
    [InlineData("G, Shift, Control",    "ctrl+shift+g")]
    [InlineData("NumPad2, Alt",         "alt+num2")]
    [InlineData("D9, Control",          "ctrl+9")]
    [InlineData("Escape, Shift",        "shift+esc")]
    [InlineData("Add, Control",         "ctrl+num+")]
    [InlineData("Subtract, Alt",        "alt+num-")]
    public void Modifiers_are_reordered_into_canonical_form(string g4, string expected)
        => Assert.Equal(expected, MacroKeyNormalizer.Normalize(g4));

    [Fact]
    public void Numpad_operator_bindings_resolve_from_the_runtime_key()
    {
        // Regression: found by replaying the real macros.cfg, not by the
        // hand-written cases above.
        var engine = new MacroEngine();
        engine.Add("Add",      "look");
        engine.Add("Subtract", "health");
        engine.Add("Multiply", "assess");
        engine.Add("Divide",   "exp");

        Assert.Equal("look",   engine.Get("num+")?.Action);
        Assert.Equal("health", engine.Get("num-")?.Action);
        Assert.Equal("assess", engine.Get("num*")?.Action);
        Assert.Equal("exp",    engine.Get("num/")?.Action);
    }

    [Theory]
    // Already-canonical input must survive untouched — the normalizer runs on
    // every lookup, including for macros authored inside Genie 5.
    [InlineData("num0")]
    [InlineData("num*")]
    [InlineData("num.")]
    [InlineData("num/")]
    // The numpad plus and minus keys ARE spelled with the separator
    // character. Splitting the canonical form on '+' reduced "num+" to "num",
    // so the Add binding in the reference macros.cfg resolved to nothing.
    [InlineData("num+")]
    [InlineData("num-")]
    [InlineData("ctrl+num+")]
    [InlineData("alt+num-")]
    [InlineData("ctrl+shift+num+")]
    [InlineData("esc")]
    [InlineData("f5")]
    [InlineData("ctrl+f1")]
    [InlineData("ctrl+alt+z")]
    [InlineData("ctrl+shift+f1")]
    [InlineData("alt+9")]
    public void Normalization_is_idempotent(string canonical)
    {
        Assert.Equal(canonical, MacroKeyNormalizer.Normalize(canonical));
        Assert.Equal(canonical, MacroKeyNormalizer.Normalize(MacroKeyNormalizer.Normalize(canonical)));
    }

    [Theory]
    [InlineData(null,  "")]
    [InlineData("",    "")]
    [InlineData("   ", "")]
    [InlineData(",,",  "")]
    [InlineData("Shift", "")]      // modifier with no key is not a binding
    public void Degenerate_input_yields_empty(string? input, string expected)
        => Assert.Equal(expected, MacroKeyNormalizer.Normalize(input));

    [Fact]
    public void Modifier_order_in_the_source_does_not_matter()
        => Assert.Equal(MacroKeyNormalizer.Normalize("F1, Control, Shift"),
                        MacroKeyNormalizer.Normalize("F1, Shift, Control"));

    // ── Engine lookup ────────────────────────────────────────────────────

    [Fact]
    public void Engine_finds_a_Genie4_spelled_macro_by_its_runtime_key()
    {
        var engine = new MacroEngine();
        engine.Add("NumPad0", "down");
        engine.Add("F1, Shift, Control", "stance defensive");
        engine.Add("Escape", "#queue clear;#script abort all");
        engine.Add("Decimal", "up");

        Assert.Equal("down",              engine.Get("num0")?.Action);
        Assert.Equal("stance defensive",  engine.Get("ctrl+shift+f1")?.Action);
        Assert.Equal("#queue clear;#script abort all", engine.Get("esc")?.Action);
        Assert.Equal("up",                engine.Get("num.")?.Action);
    }

    [Fact]
    public void Stored_key_keeps_the_users_own_spelling()
    {
        // The macros.cfg dual-write emits MacroRule.Key. Rewriting it to the
        // Genie 5 dialect would corrupt a settings folder shared with a
        // Genie 4 install.
        var engine = new MacroEngine();
        engine.Add("NumPad0", "down");

        Assert.Equal("NumPad0", Assert.Single(engine.Rules).Key);
    }

    [Fact]
    public void Macros_authored_in_Genie5_still_resolve()
    {
        var engine = new MacroEngine();
        engine.Add("num0", "down");
        engine.Add("ctrl+f1", "look");

        Assert.Equal("down", engine.Get("num0")?.Action);
        Assert.Equal("look", engine.Get("ctrl+f1")?.Action);
    }

    [Fact]
    public void Removing_a_macro_also_clears_its_normalized_lookup()
    {
        var engine = new MacroEngine();
        engine.Add("NumPad0", "down");
        Assert.True(engine.Remove("NumPad0"));

        Assert.Null(engine.Get("num0"));
        Assert.Null(engine.Get("NumPad0"));
    }

    [Fact]
    public void Clearing_the_engine_clears_the_normalized_lookup()
    {
        var engine = new MacroEngine();
        engine.Add("NumPad0", "down");
        engine.Clear();

        Assert.Null(engine.Get("num0"));
    }

    [Fact]
    public void Rebinding_through_the_other_spelling_wins()
    {
        var engine = new MacroEngine();
        engine.Add("NumPad0", "down");
        engine.Add("num0", "climb down");

        Assert.Equal("climb down", engine.Get("num0")?.Action);
    }

    [Fact]
    public void Unknown_key_still_returns_null()
    {
        var engine = new MacroEngine();
        engine.Add("NumPad0", "down");

        Assert.Null(engine.Get("num1"));
        Assert.Null(engine.Get("ctrl+q"));
    }
}
