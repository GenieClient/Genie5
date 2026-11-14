using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Genie.Core.Mapper;
using Genie.Core.Update;
using Genie.Core.Update.Sources;
using Genie.Core.Update.Updaters;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// The official Maps feed lists one folder — the repo root — so festival maps
/// (parked in "Festivals Copy to the Maps Folder/") only ever reached Genie 5
/// users when someone also committed a temporary copy at the root (Hollow Eve
/// 2026, Maps PR #268). <see cref="FeedEntry.IncludeFestivalMaps"/> /
/// <see cref="FeedEntry.IncludeQuestMaps"/> add the subfolder as a second
/// source behind the root one; files land flat in the Maps folder, the
/// manifest tracks them like any root zone, and a zone present in both places
/// is taken from the root. Both toggles default OFF — quest maps are spoilers.
/// </summary>
public class MapsFestivalFolderTests : IDisposable
{
    private readonly string _dir;
    private readonly MapZoneRepository _repo = new();

    public MapsFestivalFolderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie5-maps-festival-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string ZoneXml(string name, string roomTitle, string id = "1") =>
        $"<zone name=\"{name}\" id=\"{id}\">" +
        $"<node id=\"1\" name=\"{roomTitle}\"><position x=\"20\" y=\"20\" z=\"0\" /></node>" +
        "</zone>";

    /// <summary>In-memory IFileListSource standing in for one repo folder. Each
    /// instance has its own description so a "root wins" note can name both.</summary>
    private sealed class FakeFolder : IFileListSource
    {
        private readonly Dictionary<string, byte[]> _files;
        public int Downloads { get; private set; }
        public string Description { get; }

        public FakeFolder(string description, params (string Name, string Content)[] files)
        {
            Description = description;
            _files = files.ToDictionary(f => f.Name, f => Encoding.UTF8.GetBytes(f.Content));
        }

        public Task<FileListInfo> GetFileListAsync(CancellationToken ct = default) =>
            Task.FromResult(new FileListInfo(Description, _files
                .Select(kv => new FileEntry(kv.Key, "mem://" + Description + "/" + kv.Key, BlobSha(kv.Value), kv.Value.Length))
                .ToList()));

        public Task<byte[]> DownloadFileAsync(FileEntry file, CancellationToken ct = default)
        {
            Downloads++;
            return Task.FromResult(_files[file.Name]);
        }

        public static string BlobSha(byte[] content)
        {
            var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
            using var sha = System.Security.Cryptography.SHA1.Create();
            sha.TransformBlock(header, 0, header.Length, null, 0);
            sha.TransformFinalBlock(content, 0, content.Length);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
    }

    private const string RootZone     = "Map1_Crossing.xml";
    private const string FestivalZone = "Map612f_Hollow_Eve_Festival_2026.xml";

    private static FakeFolder Root(params (string Name, string Content)[] extra) =>
        new("fake/Maps", new[] { (RootZone, ZoneXml("Crossing", "Town Green")) }.Concat(extra).ToArray());

    private static FakeFolder Festivals(string content) =>
        new("fake/Maps/" + FeedEntry.FestivalMapsFolder, (FestivalZone, content));

    private MapsUpdater NewUpdater(params IFileListSource[] sources) => new(_repo, _dir, sources);

    private Dictionary<string, string> Manifest() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(_dir, ".map-shas.json")))!;

    // ── The toggles decide which folders a feed resolves to ─────────────────

    [Fact]
    public void Official_feed_defaults_to_root_only_with_both_toggles_off()
    {
        var feed = FeedEntry.OfficialMaps();
        Assert.False(feed.IncludeFestivalMaps);
        Assert.False(feed.IncludeQuestMaps);

        var sources = MapsFeedSources.Create(feed);
        var only    = Assert.Single(sources);
        Assert.Equal("https://api.github.com/repos/GenieClient/Maps/contents/",
                     ((GithubContentsSource)only).ListingUrl);
    }

    [Fact]
    public void Festival_toggle_adds_the_encoded_subfolder_behind_the_root()
    {
        var feed = FeedEntry.OfficialMaps();
        feed.IncludeFestivalMaps = true;

        var sources = MapsFeedSources.Create(feed).Cast<GithubContentsSource>().ToList();
        Assert.Equal(2, sources.Count);
        Assert.Equal("https://api.github.com/repos/GenieClient/Maps/contents/", sources[0].ListingUrl);
        Assert.Equal("https://api.github.com/repos/GenieClient/Maps/contents/Festivals%20Copy%20to%20the%20Maps%20Folder",
                     sources[1].ListingUrl);
        // The human name is what shows in status text, not the %20 form.
        Assert.Equal("GenieClient/Maps/" + FeedEntry.FestivalMapsFolder, sources[1].Description);
    }

    [Fact]
    public void Quest_toggle_is_its_own_switch_and_follows_festivals()
    {
        var feed = FeedEntry.OfficialMaps();
        feed.IncludeQuestMaps = true;
        var questOnly = MapsFeedSources.Create(feed).Cast<GithubContentsSource>().ToList();
        Assert.Equal(2, questOnly.Count);
        Assert.Equal("https://api.github.com/repos/GenieClient/Maps/contents/Quests%20%28Spoiler%20Alert%29%20Copy%20to%20the%20Maps%20Folder",
                     questOnly[1].ListingUrl);

        feed.IncludeFestivalMaps = true;
        var both = MapsFeedSources.Create(feed).Cast<GithubContentsSource>().ToList();
        Assert.Equal(3, both.Count);
        Assert.Contains("Festivals", both[1].ListingUrl);
        Assert.Contains("Quests",     both[2].ListingUrl);
    }

    [Fact]
    public void Subfolder_hangs_off_a_feed_with_its_own_path()
    {
        var feed = FeedEntry.OfficialMaps();
        feed.Path = "maps";
        feed.IncludeFestivalMaps = true;

        var sources = MapsFeedSources.Create(feed).Cast<GithubContentsSource>().ToList();
        Assert.Equal("https://api.github.com/repos/GenieClient/Maps/contents/maps/Festivals%20Copy%20to%20the%20Maps%20Folder",
                     sources[1].ListingUrl);
    }

    // ── Updater behaviour with the subfolder on / off ────────────────────────

    [Fact]
    public async Task Festival_zone_is_ignored_when_the_toggle_is_off()
    {
        // Toggle off = the feed materialises to the root source alone.
        var root    = Root();
        var updater = NewUpdater(root);

        var check = await updater.CheckAsync();
        Assert.Contains("1 new", check.LatestVersion);

        var apply = await updater.ApplyAsync();
        Assert.True(apply.Succeeded);
        Assert.True(File.Exists(Path.Combine(_dir, RootZone)));
        Assert.False(File.Exists(Path.Combine(_dir, FestivalZone)));
        Assert.DoesNotContain(FestivalZone, Manifest().Keys);
    }

    [Fact]
    public async Task Festival_zone_is_pulled_flat_and_recorded_in_the_manifest_when_on()
    {
        var festivalXml = ZoneXml("Hollow Eve Festival", "Festival Gate", id: "612f");
        var root        = Root();
        var festivals   = Festivals(festivalXml);
        var updater     = NewUpdater(root, festivals);

        var check = await updater.CheckAsync();
        Assert.True(check.UpdateAvailable);
        Assert.Contains("2 new", check.LatestVersion);
        Assert.Contains(check.Changes!, c => c.Name == FestivalZone && c.IsNew);

        var apply = await updater.ApplyAsync();
        Assert.True(apply.Succeeded);
        Assert.Contains("2 new", apply.Summary);
        Assert.Null(apply.Notes);

        // Flat: the zone sits beside the root maps, no "Festivals…" folder.
        Assert.True(File.Exists(Path.Combine(_dir, FestivalZone)));
        Assert.Empty(Directory.GetDirectories(_dir));

        // The manifest tracks it like a root zone — so the next check is quiet
        // and the next apply does not re-download it.
        var manifest = Manifest();
        Assert.Equal(FakeFolder.BlobSha(Encoding.UTF8.GetBytes(festivalXml)), manifest[FestivalZone]);

        var after = await updater.CheckAsync();
        Assert.False(after.UpdateAvailable);

        var again = await updater.ApplyAsync();
        Assert.Contains("2 already current", again.Summary);
        Assert.Equal(1, festivals.Downloads);
    }

    [Fact]
    public async Task Festival_zone_counts_as_changed_when_the_subfolder_copy_moves_upstream()
    {
        var festivals = Festivals(ZoneXml("Hollow Eve Festival", "Festival Gate", id: "612f"));
        var updater   = NewUpdater(Root(), festivals);
        await updater.ApplyAsync();

        var moved = new FakeFolder(festivals.Description,
            (FestivalZone, ZoneXml("Hollow Eve Festival", "Festival Gate (moved)", id: "612f")));
        var next  = NewUpdater(Root(), moved);

        var check = await next.CheckAsync();
        Assert.True(check.UpdateAvailable);
        Assert.Contains("1 changed", check.LatestVersion);
        Assert.Contains(check.Changes!, c => c.Name == FestivalZone && !c.IsNew);

        var apply = await next.ApplyAsync();
        Assert.Contains("1 merged", apply.Summary);
        Assert.Equal("Festival Gate (moved)", _repo.Load(Path.Combine(_dir, FestivalZone))!.Nodes[1].Title);
    }

    // ── Root wins on a name collision ────────────────────────────────────────

    [Fact]
    public async Task Root_copy_wins_when_the_same_zone_is_in_both_folders()
    {
        // Hollow Eve 2026: the festival map was ALSO committed at the root so
        // users would receive it without the toggle. Two listings, one file.
        var rootXml     = ZoneXml("Hollow Eve Festival", "Festival Gate (root)",      id: "612f");
        var festivalXml = ZoneXml("Hollow Eve Festival", "Festival Gate (subfolder)", id: "612f");
        var root        = Root((FestivalZone, rootXml));
        var festivals   = Festivals(festivalXml);
        var updater     = NewUpdater(root, festivals);

        // Check counts the zone once, not once per folder.
        var check = await updater.CheckAsync();
        Assert.Contains("2 new", check.LatestVersion);
        Assert.Single(check.Changes!, c => c.Name == FestivalZone);
        Assert.Contains(FestivalZone, check.Notes ?? "");
        Assert.Contains("wins", check.Notes ?? "");

        var apply = await updater.ApplyAsync();
        Assert.True(apply.Succeeded);
        Assert.Contains("1 duplicate(s) skipped", apply.Summary);
        var note = Assert.Single(apply.Notes!);
        Assert.StartsWith(FestivalZone, note);
        Assert.Contains(festivals.Description, note);
        Assert.Contains(root.Description,      note);

        // The root content is what landed, the subfolder copy was never fetched,
        // and the manifest records the ROOT sha.
        Assert.Equal("Festival Gate (root)", _repo.Load(Path.Combine(_dir, FestivalZone))!.Nodes[1].Title);
        Assert.Equal(0, festivals.Downloads);
        Assert.Equal(FakeFolder.BlobSha(Encoding.UTF8.GetBytes(rootXml)), Manifest()[FestivalZone]);

        // And the subfolder's different sha must not keep the banner lit.
        var after = await updater.CheckAsync();
        Assert.False(after.UpdateAvailable);
    }

    [Fact]
    public async Task Collision_is_case_insensitive_like_the_manifest()
    {
        var root      = Root(("MAP612F_Hollow_Eve_Festival_2026.XML", ZoneXml("Hollow Eve", "Root", id: "612f")));
        var festivals = Festivals(ZoneXml("Hollow Eve", "Subfolder", id: "612f"));
        var updater   = NewUpdater(root, festivals);

        var apply = await updater.ApplyAsync();
        Assert.Contains("1 duplicate(s) skipped", apply.Summary);
        Assert.Equal(0, festivals.Downloads);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [Fact]
    public void Toggles_round_trip_through_the_feed_store_and_default_off_on_legacy_files()
    {
        Directory.CreateDirectory(_dir);
        var store = new FeedConfigStore(_dir);

        var cfg = FeedConfig.CreateDefault();
        cfg.Maps[0].IncludeFestivalMaps = true;
        Assert.True(store.Save(cfg));

        var loaded = store.Load();
        Assert.True(loaded.Maps[0].IncludeFestivalMaps);
        Assert.False(loaded.Maps[0].IncludeQuestMaps);

        // A pre-toggle update-feeds.json has neither property: both read as off.
        File.WriteAllText(Path.Combine(_dir, "update-feeds.json"), """
        {
          "core": { "kind": "github-releases", "owner": "GenieClient", "repo": "Genie5",
                    "channel": "beta", "assetPattern": "Genie5-{os}-{arch}.zip" },
          "maps": [ { "id": "official-maps", "name": "GenieClient/Maps (official)",
                      "kind": "github-contents", "owner": "GenieClient", "repo": "Maps",
                      "extension": ".xml", "enabled": true } ],
          "plugins": [], "scripts": []
        }
        """);
        var legacy = Assert.Single(store.Load().Maps);
        Assert.False(legacy.IncludeFestivalMaps);
        Assert.False(legacy.IncludeQuestMaps);
    }
}
