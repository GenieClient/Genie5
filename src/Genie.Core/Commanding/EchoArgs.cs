namespace Genie.Core.Commanding;

public static class EchoArgs
{
    public static void Parse(
        IReadOnlyList<string> tokens, int startIndex,
        out string? window, out string? color, out string message)
        => Parse(tokens, startIndex, out window, out color, out _, out message);

    /// <summary>
    /// Parse the leading <c>#echo</c> option tokens (Genie 4 parity): an optional
    /// <c>&gt;window</c> redirect, an optional colour (named or <c>#rrggbb</c>),
    /// and an optional <c>mono</c> flag (render the line in a monospaced font).
    /// Options may appear in any order before the message; the first token that
    /// isn't an option begins the message.
    /// </summary>
    public static void Parse(
        IReadOnlyList<string> tokens, int startIndex,
        out string? window, out string? color, out bool mono, out string message)
    {
        window = null; color = null; mono = false;
        int idx = startIndex;
        while (idx < tokens.Count)
        {
            var tok = tokens[idx];
            if (tok.Length > 0 && tok[0] == '>') { window = tok[1..]; idx++; continue; }
            if (string.Equals(tok, "mono", StringComparison.OrdinalIgnoreCase)) { mono = true; idx++; continue; }
            if (IsEchoColor(tok)) { color = tok; idx++; continue; }
            break;
        }
        message = idx < tokens.Count ? string.Join(" ", tokens.Skip(idx)) : string.Empty;
    }

    public static bool IsEchoColor(string tok)
    {
        if (string.IsNullOrEmpty(tok)) return false;
        if (tok[0] == '#' && tok.Length >= 4)
        {
            for (int i = 1; i < tok.Length; i++)
            {
                var c = tok[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }
        return NamedColors.Contains(tok);
    }

    private static readonly HashSet<string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "AliceBlue","AntiqueWhite","Aqua","Aquamarine","Azure","Beige","Bisque",
        "Black","BlanchedAlmond","Blue","BlueViolet","Brown","BurlyWood","CadetBlue",
        "Chartreuse","Chocolate","Coral","CornflowerBlue","Cornsilk","Crimson","Cyan",
        "DarkBlue","DarkCyan","DarkGoldenrod","DarkGray","DarkGreen","DarkKhaki",
        "DarkMagenta","DarkOliveGreen","DarkOrange","DarkOrchid","DarkRed","DarkSalmon",
        "DarkSeaGreen","DarkSlateBlue","DarkSlateGray","DarkTurquoise","DarkViolet",
        "DeepPink","DeepSkyBlue","DimGray","DodgerBlue","Firebrick","FloralWhite",
        "ForestGreen","Fuchsia","Gainsboro","GhostWhite","Gold","Goldenrod","Gray",
        "Green","GreenYellow","Honeydew","HotPink","IndianRed","Indigo","Ivory","Khaki",
        "Lavender","LavenderBlush","LawnGreen","LemonChiffon","LightBlue","LightCoral",
        "LightCyan","LightGoldenrodYellow","LightGray","LightGreen","LightPink",
        "LightSalmon","LightSeaGreen","LightSkyBlue","LightSlateGray","LightSteelBlue",
        "LightYellow","Lime","LimeGreen","Linen","Magenta","Maroon","MediumAquamarine",
        "MediumBlue","MediumOrchid","MediumPurple","MediumSeaGreen","MediumSlateBlue",
        "MediumSpringGreen","MediumTurquoise","MediumVioletRed","MidnightBlue",
        "MintCream","MistyRose","Moccasin","NavajoWhite","Navy","OldLace","Olive",
        "OliveDrab","Orange","OrangeRed","Orchid","PaleGoldenrod","PaleGreen",
        "PaleTurquoise","PaleVioletRed","PapayaWhip","PeachPuff","Peru","Pink","Plum",
        "PowderBlue","Purple","Red","RosyBrown","RoyalBlue","SaddleBrown","Salmon",
        "SandyBrown","SeaGreen","SeaShell","Sienna","Silver","SkyBlue","SlateBlue",
        "SlateGray","Snow","SpringGreen","SteelBlue","Tan","Teal","Thistle","Tomato",
        "Turquoise","Violet","Wheat","White","WhiteSmoke","Yellow","YellowGreen",
    };
}
