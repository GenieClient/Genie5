using Avalonia.Input;

namespace Genie.App.Controls;

/// <summary>
/// Shared helper for converting an Avalonia <see cref="Key"/> + <see cref="KeyModifiers"/>
/// pair into Genie 4's macro key vocabulary, and vice-versa where needed.
///
/// Key string examples:
/// <list type="bullet">
/// <item><c>F1</c> .. <c>F12</c> — function keys (firing macros from these
///   does not require any modifier).</item>
/// <item><c>ctrl+h</c>, <c>alt+x</c>, <c>ctrl+shift+a</c> — letter keys with
///   at least one of Ctrl/Alt. Shift alone is treated as ordinary typing.</item>
/// <item><c>ctrl+1</c>, <c>alt+0</c> — number-row digits with modifier.</item>
/// <item><c>ctrl+num5</c>, <c>alt+num0</c> — numpad digits with modifier.</item>
/// <item><c>num/</c>, <c>num*</c>, <c>num-</c>, <c>num+</c>, <c>num.</c> —
///   numpad operators, with or without modifier.</item>
/// <item><c>esc</c>, <c>shift+esc</c> — Escape, after the global kill switch
///   has had its turn.</item>
/// </list>
/// Genie 4 spells these differently in <c>macros.cfg</c> (<c>NumPad5</c>,
/// <c>Decimal</c>, <c>Escape</c>, <c>F1, Shift</c>); see
/// <c>Genie.Core.Macros.MacroKeyNormalizer</c>, which maps that vocabulary
/// onto this one at lookup time.
/// Returns <c>null</c> for keystrokes that should never fire a macro or be
/// captured into a macro-key field — plain letters, plain digits, navigation
/// keys without modifiers, Tab/Enter, modifier keys themselves.
/// </summary>
public static class MacroKeyConverter
{
    public static string? ToMacroKey(Key key, KeyModifiers mods)
    {
        // Modifier keys alone are not macros.
        if (key is Key.LeftCtrl or Key.RightCtrl or
                   Key.LeftAlt or Key.RightAlt or
                   Key.LeftShift or Key.RightShift or
                   Key.LWin or Key.RWin) return null;

        // Function keys fire regardless of modifier (F1, ctrl+F2, alt+shift+F5, …).
        if (key >= Key.F1 && key <= Key.F12)
            return BuildKeyName(key.ToString().ToLowerInvariant(), mods);

        // Letters only with Ctrl or Alt — Shift alone is normal typing.
        if (key >= Key.A && key <= Key.Z &&
            (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt)))
            return BuildKeyName(key.ToString().ToLowerInvariant(), mods);

        // Number-row digits with Ctrl or Alt.
        if (key >= Key.D0 && key <= Key.D9 &&
            (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt)))
            return BuildKeyName(key.ToString()[1..], mods);   // strip "D" prefix

        // Numpad digits fire with OR without a modifier — Genie 4 parity: the
        // numpad is the classic movement pad (num8 → north, etc.). A plain
        // numpad press only ever gets swallowed when a macro is actually bound
        // to it (see MainWindow.OnGlobalKeyDown), so unbound numpad keys still
        // type normally. (Requires NumLock on, so the OS reports NumPadN
        // rather than the navigation keys.)
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
            return BuildKeyName("num" + key.ToString()[6..], mods);

        // Numpad operators — same fire-with-or-without-modifier rule as the
        // digits (public #140): Genie 3/4 shipped the remaining 10-key
        // hotkeys on these (num/ assess, num* health, num- fatigue,
        // num+ look). Avalonia reports the numpad operators as their own
        // Key values (the main-row equivalents are Oem* keys), so this
        // never captures ordinary typing.
        switch (key)
        {
            case Key.Divide:   return BuildKeyName("num/", mods);
            case Key.Multiply: return BuildKeyName("num*", mods);
            case Key.Subtract: return BuildKeyName("num-", mods);
            case Key.Add:      return BuildKeyName("num+", mods);
            // Numpad '.' — the last seat on Genie 3/4's ten-key movement pad
            // ({Decimal} {up} in the reference macros.cfg). Without this the
            // imported binding had no key to fire from.
            case Key.Decimal:  return BuildKeyName("num.", mods);
        }

        // Escape. MainWindow.OnGlobalKeyDown already handles Esc as the
        // always-available kill switch and deliberately falls through "so such
        // a macro can still fire" when there is nothing to stop — but this
        // method never produced an esc key, so that fallthrough could not
        // resolve to anything. Genie 4's own default binding is
        // {Escape} {#queue clear;#script abort all}, so the stop-first
        // ordering upstream is what keeps the kill switch honest.
        if (key == Key.Escape)
            return BuildKeyName("esc", mods);

        return null;
    }

    private static string BuildKeyName(string keyName, KeyModifiers mods)
    {
        var parts = new List<string>(4);
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("ctrl");
        if (mods.HasFlag(KeyModifiers.Alt))     parts.Add("alt");
        if (mods.HasFlag(KeyModifiers.Shift))   parts.Add("shift");
        parts.Add(keyName);
        return string.Join("+", parts);
    }
}
