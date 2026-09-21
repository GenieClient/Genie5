using System;
using System.IO;
using Genie.Core.Update;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #281 — the default plugin feed seeded Plugin_EXPTrackerV5, whose id
/// <c>genie.experience</c> is on <c>PluginManager.RetiredPluginIds</c> since
/// Experience tracking moved into Core. A fresh install could download a DLL
/// the loader then refused to register.
/// </summary>
public class PluginFeedDefaultTests
{
    /// <summary>The plugin id <c>genie.experience</c> is retired now that
    /// Experience tracking lives in Core, so seeding its feed could only ever
    /// download a DLL the loader refuses to register.</summary>
    [Fact]
    public void A_fresh_install_seeds_no_plugin_feeds()
    {
        Assert.Empty(FeedConfig.CreateDefault().Plugins);
    }

    /// <summary>An existing update-feeds.json that still carries the seeded row
    /// is migrated on load.</summary>
    [Fact]
    public void Loading_an_existing_config_drops_the_seeded_exptracker_row()
    {
        var dir = NewTempDir();
        try
        {
            var seeded = FeedConfig.CreateDefault();
            seeded.Plugins.Add(FeedEntry.RetiredExpTracker());
            var store = new FeedConfigStore(dir);
            Assert.True(store.Save(seeded));

            Assert.Empty(store.Load().Plugins);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>…but a row the user repointed at their own repo is theirs, and
    /// survives. The migration matches on id AND source for exactly this.</summary>
    [Fact]
    public void A_user_repointed_row_with_the_same_id_survives_the_migration()
    {
        var dir = NewTempDir();
        try
        {
            var mine = FeedEntry.RetiredExpTracker();
            mine.Owner = "someone-else";
            mine.Repo  = "my-fork";

            var cfg = FeedConfig.CreateDefault();
            cfg.Plugins.Add(mine);
            var store = new FeedConfigStore(dir);
            Assert.True(store.Save(cfg));

            var kept = Assert.Single(store.Load().Plugins);
            Assert.Equal("my-fork", kept.Repo);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewTempDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "genie_feedfix_" + Guid.NewGuid().ToString("N"))).FullName;
}
