using System;
using System.IO;
using Genie.Core.Update;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Until 5.0.0 every build was a GitHub prerelease, so the Core feed defaulted
/// to the <c>"beta"</c> channel and a saved <c>"beta"</c> was not a choice. From
/// 5.0.0 the default is <c>"stable"</c>, and a file written before then is
/// moved to it once — after which a deliberate choice of beta has to stick.
/// </summary>
public class StableChannelMigrationTests
{
    private const string PreStableFile = """
        {
          "core": { "kind": "github-releases", "owner": "GenieClient", "repo": "Genie5",
                    "channel": "beta", "assetPattern": "Genie5-{os}-{arch}.zip" },
          "maps": [], "plugins": [], "scripts": []
        }
        """;

    [Fact]
    public void A_fresh_install_is_on_the_stable_channel()
    {
        var core = FeedConfig.CreateDefault().Core;
        Assert.Equal("stable", core.Channel);
        Assert.True(core.StableChannelMigrated);
    }

    [Fact]
    public void A_missing_file_loads_as_stable()
    {
        var dir = NewTempDir();
        try { Assert.Equal("stable", new FeedConfigStore(dir).Load().Core.Channel); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_file_saved_before_the_first_stable_release_moves_to_stable_once()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "update-feeds.json"), PreStableFile);
            var store  = new FeedConfigStore(dir);
            var loaded = store.Load();

            Assert.Equal("stable", loaded.Core.Channel);
            Assert.True(loaded.Core.StableChannelMigrated);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Choosing_beta_after_the_move_is_kept()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "update-feeds.json"), PreStableFile);
            var store  = new FeedConfigStore(dir);
            var loaded = store.Load();

            // What the Updates dialog does when the user picks beta.
            loaded.Core.Channel = "beta";
            Assert.True(store.Save(loaded));

            var again = store.Load();
            Assert.Equal("beta", again.Core.Channel);
            Assert.True(again.Core.StableChannelMigrated);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_move_repeats_until_the_file_is_saved()
    {
        // Load does not write. A file nobody has saved since is still a
        // pre-stable file on the next start, and still loads as stable.
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "update-feeds.json"), PreStableFile);
            var store = new FeedConfigStore(dir);
            store.Load();
            Assert.Equal("stable", store.Load().Core.Channel);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "genie-stablechan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
