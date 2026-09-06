using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Genie.Core.Mapper;
using Genie.Core.Update.Sources;
using Genie.Core.Update.Updaters;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// The updaters must actually USE the containment check (2026-08-31 security
/// review). <c>FeedPathTests</c> proves the helper is correct; this proves it is
/// wired in — a correct helper with no caller is exactly how a fix ships without
/// fixing anything.
///
/// <para>Drives a hostile feed through the real <c>MapsUpdater</c>: a listing whose
/// entry name climbs out of the Maps folder. The updater must refuse it and leave
/// nothing behind outside its own directory.</para>
/// </summary>
public class UpdaterPathTraversalTests
{
    /// <summary>A feed that serves whatever names the test asks for, and real
    /// (valid) map XML as the payload, so a refusal can only come from the name.</summary>
    private sealed class HostileFileSource : IFileListSource
    {
        private readonly string[] _names;
        public HostileFileSource(params string[] names) => _names = names;
        public int Downloads { get; private set; }

        public string Description => "hostile/test-feed";

        public Task<FileListInfo> GetFileListAsync(CancellationToken ct = default)
            => Task.FromResult(new FileListInfo(
                Description,
                _names.Select(n => new FileEntry(n, "https://example.invalid/" + n, null, 10)).ToList()));

        public Task<byte[]> DownloadFileAsync(FileEntry file, CancellationToken ct = default)
        {
            Downloads++;
            const string xml = "<zone name=\"Test\" id=\"9001\"><node id=\"1\" name=\"A\"><position x=\"0\" y=\"0\" z=\"0\" /></node></zone>";
            return Task.FromResult(Encoding.UTF8.GetBytes(xml));
        }
    }

    private sealed class Sandbox : IDisposable
    {
        public string Root    { get; }
        public string MapsDir { get; }
        /// <summary>A sibling the traversal names aim at.</summary>
        public string Outside { get; }

        public Sandbox()
        {
            Root    = Path.Combine(Path.GetTempPath(), "gc_traversal_" + Guid.NewGuid().ToString("N"));
            MapsDir = Path.Combine(Root, "Maps");
            Outside = Path.Combine(Root, "Config");
            Directory.CreateDirectory(MapsDir);
            Directory.CreateDirectory(Outside);
        }

        public string[] FilesOutside =>
            Directory.GetFiles(Outside, "*", SearchOption.AllDirectories);

        public string[] FilesInMaps =>
            Directory.GetFiles(MapsDir, "*.xml", SearchOption.AllDirectories);

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private static MapsUpdater Build(Sandbox box, IFileListSource source)
        => new(new MapZoneRepository(), box.MapsDir, new[] { source });

    [Theory]
    [InlineData("../Config/pwned.xml")]
    [InlineData("..\\Config\\pwned.xml")]
    [InlineData("../../pwned.xml")]
    [InlineData("sub/../../Config/pwned.xml")]
    public async Task A_traversing_entry_name_writes_nothing_outside_the_maps_folder(string hostileName)
    {
        using var box = new Sandbox();
        var source = new HostileFileSource(hostileName);

        var result = await Build(box, source).ApplyAsync();

        Assert.Empty(box.FilesOutside);
        Assert.Empty(box.FilesInMaps);
        Assert.Contains(result.Errors, e => e.Contains("refused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_subfolder_name_is_refused_because_the_maps_folder_is_flat()
    {
        // Not an attack, but not something this feed may do either: the updater
        // enumerates its folder top-level-only, so a nested file would be written
        // and then never seen again.
        using var box = new Sandbox();

        var result = await Build(box, new HostileFileSource("zones/Crossing.xml")).ApplyAsync();

        Assert.Empty(box.FilesInMaps);
        Assert.Contains(result.Errors, e => e.Contains("refused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_ordinary_entry_still_installs()
    {
        // The guard must not have cost the feature. Without this the tests above
        // would pass against an updater that refused everything.
        using var box = new Sandbox();

        var result = await Build(box, new HostileFileSource("Crossing.xml")).ApplyAsync();

        Assert.True(result.Succeeded, string.Join(" | ", result.Errors));
        Assert.Single(box.FilesInMaps);
        Assert.Equal("Crossing.xml", Path.GetFileName(box.FilesInMaps[0]));
        Assert.Empty(box.FilesOutside);
    }

    [Fact]
    public async Task A_hostile_entry_does_not_stop_the_good_ones_beside_it()
    {
        // A poisoned listing must not become a denial of service on the rest of
        // the feed: refuse the bad entry, install the good ones.
        using var box = new Sandbox();
        var source = new HostileFileSource("../Config/pwned.xml", "Crossing.xml", "Shard.xml");

        var result = await Build(box, source).ApplyAsync();

        Assert.Empty(box.FilesOutside);
        Assert.Equal(2, box.FilesInMaps.Length);
        Assert.Contains(result.Errors, e => e.Contains("refused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Checking_a_hostile_listing_does_not_throw()
    {
        // CheckAsync runs on a timer/at dialog-open; a malformed feed must not
        // surface as an exception there.
        using var box = new Sandbox();

        var result = await Build(box, new HostileFileSource("../Config/pwned.xml")).CheckAsync();

        Assert.NotNull(result);
        Assert.Empty(box.FilesOutside);
    }
}
