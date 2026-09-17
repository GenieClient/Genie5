namespace Genie.Core.Macros;

/// <summary>
/// Translates a macro key string written in Genie 4's vocabulary into the
/// canonical form Genie 5 resolves at runtime, and leaves an already-canonical
/// string untouched.
/// </summary>
/// <remarks>
/// Genie 4 writes <c>macros.cfg</c> using .NET <c>Keys</c> enum names with
/// comma-separated modifiers:
/// <code>
/// #macro {NumPad0} {down}
/// #macro {F1, Shift, Control} {stance defensive}
/// #macro {Escape} {#queue clear;#script abort all}
/// </code>
/// Genie 5 resolves <c>num0</c>, <c>ctrl+shift+f1</c>, <c>esc</c> — built by
/// <c>MacroKeyConverter.ToMacroKey</c> from the live keystroke. Nothing
/// translated between the two, so <em>every</em> imported macro was stored
/// under a key the runtime could never produce and silently never fired: all
/// 95 macros in the reference settings tree, including the whole numpad
/// movement pad and the Escape kill switch.
/// <para>
/// Normalization happens at lookup rather than on the way in, so the key the
/// user's file already contains is never rewritten — a macros.cfg shared with
/// a Genie 4 install keeps working.
/// </para>
/// </remarks>
public static class MacroKeyNormalizer
{
    /// <summary>
    /// Canonical form: modifiers in <c>ctrl+alt+shift</c> order, then the key
    /// name, all lowercase. Unrecognised input is returned lowercased and
    /// trimmed so it at least compares consistently.
    /// </summary>
    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;

        bool ctrl = false, alt = false, shift = false;
        string? name;

        if (key.Contains(','))
        {
            // Genie 4 form: "F1, Shift, Control" — key first, then modifiers.
            var parts = key.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return string.Empty;

            name = null;
            foreach (var raw in parts)
            {
                if (TryModifier(raw, ref ctrl, ref alt, ref shift)) continue;
                name ??= NormalizeKeyName(raw);
            }
        }
        else
        {
            // Genie 5 form: "ctrl+shift+f1" — strip modifier prefixes one at a
            // time rather than splitting on '+', because the numpad plus key
            // IS "num+". Splitting turned it into "num", so the Add binding
            // in the reference macros.cfg resolved to nothing.
            var rest = key.Trim();
            while (true)
            {
                var plus = rest.IndexOf('+');
                if (plus <= 0) break;                       // no prefix, or the key itself starts with '+'
                var head = rest[..plus];
                if (!TryModifier(head, ref ctrl, ref alt, ref shift)) break;
                rest = rest[(plus + 1)..].TrimStart();
            }

            // What is left must be an actual key. A bare modifier ("shift",
            // "ctrl+") is not a binding.
            name = IsModifier(rest) ? null : NormalizeKeyName(rest);
        }

        if (string.IsNullOrEmpty(name)) return string.Empty;

        var result = new List<string>(4);
        if (ctrl)  result.Add("ctrl");
        if (alt)   result.Add("alt");
        if (shift) result.Add("shift");
        result.Add(name);
        return string.Join("+", result);
    }

    private static bool IsModifier(string token) => token.Trim().ToLowerInvariant()
        is "ctrl" or "control" or "alt" or "shift";

    private static bool TryModifier(string token, ref bool ctrl, ref bool alt, ref bool shift)
    {
        switch (token.Trim().ToLowerInvariant())
        {
            case "ctrl" or "control": ctrl  = true; return true;
            case "alt":               alt   = true; return true;
            case "shift":             shift = true; return true;
            default:                                return false;
        }
    }

    private static string NormalizeKeyName(string raw)
    {
        var k = raw.Trim().ToLowerInvariant();

        // Numpad digits: Genie 4 "NumPad7" → "num7".
        if (k.StartsWith("numpad") && k.Length > 6)
            return "num" + k[6..];

        // Numpad operators. Genie 4 uses the .NET Keys names; Genie 5 spells
        // them after the glyph on the key. "Decimal" is the numpad '.' —
        // part of Genie 3/4's ten-key movement pad ({Decimal} {up}).
        switch (k)
        {
            case "multiply": return "num*";
            case "divide":   return "num/";
            case "subtract": return "num-";
            case "add":      return "num+";
            case "decimal":  return "num.";
            case "escape":   return "esc";
        }

        // Number-row digits: Genie 4 "D7" → "7".
        if (k.Length == 2 && k[0] == 'd' && char.IsAsciiDigit(k[1]))
            return k[1..];

        // f1..f12, single letters, and anything already canonical (num3,
        // num*, esc, …) pass through lowercased.
        return k;
    }
}
