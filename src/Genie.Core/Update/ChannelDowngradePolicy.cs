namespace Genie.Core.Update;

/// <summary>
/// Decides when the Core updater may offer a LOWER version than the one
/// installed (public #455).
///
/// <para>The only case is leaving the beta channel: a pre-release build is
/// installed and the user has chosen <c>"stable"</c>. The newest stable
/// release is then usually older than the installed pre-release
/// (5.0.0 vs 5.0.1-beta.2), and an updater that only moves forward reports
/// "up to date" and holds the install on the beta until the next stable
/// ships.</para>
///
/// <para>A stable build never downgrades, and neither does anything on the
/// beta channel — there the feed already contains every build, so the
/// newest one is never lower.</para>
///
/// <para>This is the eligibility rule only. Callers that run unattended
/// (the startup check, auto-apply) must not use it: a tester who installed
/// a pre-release by hand while still on the stable channel would be rolled
/// back without asking. Only the Updates dialog, where the user reads what
/// is offered and clicks, opts in.</para>
/// </summary>
public static class ChannelDowngradePolicy
{
    /// <summary>True when <paramref name="channel"/> is stable and
    /// <paramref name="installedVersion"/> is a pre-release build.</summary>
    public static bool MayDowngrade(string? channel, string? installedVersion)
        => IsStable(channel) && IsPrerelease(installedVersion);

    /// <summary>An empty channel means stable, matching <see cref="CoreFeed.Channel"/>'s default.</summary>
    public static bool IsStable(string? channel)
        => string.IsNullOrWhiteSpace(channel)
           || string.Equals(channel.Trim(), "stable", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SemVer pre-release test: a <c>-</c> label after the numeric core
    /// (<c>5.0.1-beta.2</c>). Build metadata (<c>+sha</c>) is ignored, so
    /// <c>5.0.0+abc-def</c> is still a stable build.
    /// </summary>
    public static bool IsPrerelease(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var v    = version.Trim();
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        return v.Contains('-');
    }
}
