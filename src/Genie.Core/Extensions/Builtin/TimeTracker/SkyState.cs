using System.Text.RegularExpressions;

namespace Genie.Core.Extensions.Builtin;

/// <summary>Visibility of a single heavenly body, as reported by <c>obs sky</c>.</summary>
internal enum Visibility { Unknown, Clear, Cloudy, BelowHorizon }

/// <summary>
/// Last-known state of the sky, parsed from DR's own output (<c>obs sky</c>,
/// <c>weather</c>, and a Moon Mage's <c>perceive</c>). All parsing is line-based;
/// <see cref="Feed"/> is fed one game-text line at a time and keeps just enough
/// state to assemble the multi-line <c>obs sky</c> block.
/// </summary>
internal sealed partial class SkyState
{
    public static readonly string[] Moons = { "Katamba", "Xibar", "Yavash" };

    public DateTimeOffset? SkyCapturedAt;
    public string Conditions = "";
    public readonly Dictionary<string, Visibility> Bodies = new(StringComparer.Ordinal);

    public DateTimeOffset? PerceiveAt;
    public string InfluenceLine = "";
    public string FavoredLine   = "";

    private bool _inScan;
    private int  _unrecognisedInScan;
    private bool _expectCondLine;

    // ── obs sky body grammar (#355) ──────────────────────────────────────────
    // DR states each body's visibility in one of many phrasings. The three
    // canonical ones are handled by BodyRe; every "partially clouded" wording
    // needs its own pattern, because they put the body name in a different
    // place each time. Measured over 304 recorded blocks, the cloudy wordings
    // below account for the majority of the lines the old single pattern
    // dropped — and dropping one used to end the whole scan.
    //
    // Each pattern captures the body name in group 1, after an optional
    // "the planet " / "the " prefix, so "the planet Szeldia" and "the Wolf"
    // both normalise to the same key the clear wording produces.
    private const string Body = @"(?:[Tt]he planet |[Tt]he )?(.+?)";

    private static readonly Regex BodyRe = BodyRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:The planet |The )?(.+?) is (unobscured by clouds|obscured by clouds|below the horizon)\.$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex BodyRegex();

    /// <summary>Partial/total cloud wordings, all of which mean "up, but
    /// clouded". Tried in order after <see cref="BodyRe"/> misses. The
    /// Piercing Gaze forms ("you focus your enhanced sight", "the clouds …
    /// melt away") report seeing THROUGH cloud cover, so the sky is still
    /// cloudy — the gaze changes what you can read, not the weather.</summary>
    private static readonly Regex[] CloudyRes =
    {
        // "Two-thirds of the planet Szeldia is blocked by cloud cover above."
        // "Two-thirds of the early afternoon sun is blocked by cloud cover above."
        Cloudy1Regex(),
        // "One half of the Raven has been obscured by clouds above."
        Cloudy2Regex(),
        // "A rather large cloud has covered nearly a third of the Wolf."
        Cloudy3Regex(),
        // "A cloud has obscured parts of the Magpie."
        Cloudy4Regex(),
        // "Most of the Cat is obscured from view."
        Cloudy5Regex(),
        // "Clouds obscure the sky where the Heart should appear."
        Cloudy6Regex(),
        // "You focus your enhanced sight, through some of the cloud cover, upon the planet Yoakena."
        Cloudy7Regex(),
        // "The clouds covering the planet Dawgolesh melt away under your gaze."
        Cloudy8Regex(),
    };
    // Source-generated (#285): Body is const, so these interpolations are compile-time constants.
    [System.Text.RegularExpressions.GeneratedRegex($@"^.+? of {Body} is blocked by cloud cover above\.$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex Cloudy1Regex();
    [System.Text.RegularExpressions.GeneratedRegex($@"^.+? of {Body} has been obscured by clouds above\.$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex Cloudy2Regex();
    [System.Text.RegularExpressions.GeneratedRegex($@"^A .*?cloud has covered .+? of {Body}\.$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex Cloudy3Regex();
    [System.Text.RegularExpressions.GeneratedRegex($@"^A .*?cloud has obscured parts of {Body}\.$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex Cloudy4Regex();
    [System.Text.RegularExpressions.GeneratedRegex($@"^.+? of {Body} is obscured from view\.$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex Cloudy5Regex();
    [System.Text.RegularExpressions.GeneratedRegex($@"^Clouds obscure the sky where {Body} should appear\.$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex Cloudy6Regex();
    [System.Text.RegularExpressions.GeneratedRegex($@"^You focus your enhanced sight,.*? upon {Body}\.$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex Cloudy7Regex();
    [System.Text.RegularExpressions.GeneratedRegex($@"^The clouds covering {Body} melt away under your gaze\.$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex Cloudy8Regex();

    /// <summary>Safety valve: how many consecutive lines the scan tolerates
    /// without recognising a body before it gives up. The real terminators are
    /// "Roundtime:" and a blank line, both of which always follow an
    /// <c>obs sky</c>; this only stops a block with neither from swallowing the
    /// rest of the session. A real block's body lines are contiguous, so no
    /// genuine reading comes close to the limit.</summary>
    private const int MaxUnrecognisedInScan = 12;
    private static readonly Regex FavoredRe = FavoredRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"^(.+?) spells are favou?red\.$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex FavoredRegex();
    private static readonly Regex DominantRe = DominantRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"\bis dominant\b", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex DominantRegex();

    /// <summary>Feed one game-text line. Returns true if it was a sky/weather/
    /// perceive line the tracker consumed.</summary>
    public bool Feed(string line, DateTimeOffset now)
    {
        var t = line.Trim();

        if (t == "The following heavenly bodies are visible:")
        {
            Bodies.Clear();
            SkyCapturedAt       = now;
            _inScan             = true;
            _unrecognisedInScan = 0;
            return true;
        }
        if (_inScan)
        {
            if (t.StartsWith("Roundtime:", StringComparison.Ordinal) || t.Length == 0)
            {
                _inScan = false;
                return t.StartsWith("Roundtime:", StringComparison.Ordinal);
            }
            var b = BodyRe.Match(t);
            if (b.Success)
            {
                Bodies[b.Groups[1].Value.Trim()] = Parse(b.Groups[2].Value);
                _unrecognisedInScan = 0;
                return true;
            }
            foreach (var re in CloudyRes)
            {
                var c = re.Match(t);
                if (!c.Success) continue;
                Bodies[c.Groups[1].Value.Trim()] = Visibility.Cloudy;
                _unrecognisedInScan = 0;
                return true;
            }
            // An unrecognised line is a wording we don't know yet, NOT the end
            // of the block (#355): ending the scan here discarded every body
            // after the first partial-cloud line — over half the sky. Skip it
            // and keep reading; only the real terminators above stop the scan.
            if (++_unrecognisedInScan >= MaxUnrecognisedInScan)
                _inScan = false;
        }

        if (t == "You glance up at the sky." || t == "You scan the sky from horizon to horizon.")
        {
            _expectCondLine = true;
            return true;
        }
        if (_expectCondLine && t.Length > 0)
        {
            Conditions      = t;
            SkyCapturedAt   = now;
            _expectCondLine = false;
            return true;
        }

        if (DominantRe.IsMatch(t) && Moons.Any(mn => t.Contains(mn, StringComparison.Ordinal)))
        {
            InfluenceLine = t;
            PerceiveAt    = now;
            return true;
        }
        var fav = FavoredRe.Match(t);
        if (fav.Success)
        {
            FavoredLine = fav.Groups[1].Value.Trim();
            PerceiveAt  = now;
            return true;
        }

        return false;
    }

    public Visibility MoonVisibility(string moon) =>
        Bodies.TryGetValue(moon, out var v) ? v : Visibility.Unknown;

    public int BodiesUp() =>
        Bodies.Count(kv => !Moons.Contains(kv.Key, StringComparer.Ordinal)
                           && kv.Value is Visibility.Clear or Visibility.Cloudy);

    public static string Describe(Visibility v) => v switch
    {
        Visibility.Clear        => "up (clear)",
        Visibility.Cloudy       => "up (cloudy)",
        Visibility.BelowHorizon => "below the horizon",
        _                       => "unknown",
    };

    private static Visibility Parse(string phrase) => phrase switch
    {
        "unobscured by clouds" => Visibility.Clear,
        "obscured by clouds"   => Visibility.Cloudy,
        "below the horizon"    => Visibility.BelowHorizon,
        _                      => Visibility.Unknown,
    };
}
