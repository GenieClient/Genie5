using Genie.Core.Update;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #455: an install that moved to a beta build could not get back to
/// the stable release, because the latest stable is a LOWER version than the
/// installed pre-release and the updater only moved forward. The updater may
/// now go down in exactly one case — a pre-release install on the stable
/// channel — and these tests pin that case and its boundaries.
/// </summary>
public class ChannelDowngradePolicyTests
{
    [Theory]
    [InlineData("stable", "5.0.1-beta.2")]
    [InlineData("Stable", "5.0.1-beta.1")]
    [InlineData("",       "5.0.1-beta.2")]      // empty channel means stable
    [InlineData(null,     "5.0.0-beta.9.10.1")]
    [InlineData("stable", "5.0.1-beta.2+abc123")]
    public void Prerelease_install_on_stable_may_downgrade(string? channel, string version)
        => Assert.True(ChannelDowngradePolicy.MayDowngrade(channel, version));

    [Theory]
    [InlineData("stable", "5.0.0")]             // a stable build never rolls back
    [InlineData("stable", "5.0.0+abc-def")]     // '-' in build metadata is not a pre-release
    [InlineData("beta",   "5.0.1-beta.2")]      // still on beta: nothing to leave
    [InlineData("beta",   "5.0.0")]
    [InlineData("stable", null)]                // unknown installed version: stay put
    [InlineData("stable", "")]
    public void Everything_else_only_moves_forward(string? channel, string? version)
        => Assert.False(ChannelDowngradePolicy.MayDowngrade(channel, version));
}
