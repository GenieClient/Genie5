using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Genie.Core.Update;
using Genie.Core.Update.Sources;
using Genie.Core.Update.Updaters;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// ScriptsUpdater — the Updates dialog's "git pull for scripts". Verifies the
/// pull semantics (new files written with subfolders mirrored, sha-matching
/// files skipped without a download, changed files overwritten), that remote
/// path names can't escape the Scripts folder, and that CheckAsync counts
/// without downloading.
/// </summary>
public class ScriptsUpdaterTests : IDisposable
{
    private readonly string _dir;

    public ScriptsUpdaterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie5-scripts-updater-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>In-memory IFileListSource: name → content, with real git blob shas.</summary>
    private sealed class FakeSource : IFileListSource
    {
        private readonly Dictionary<string, byte[]> _files;
        public int Downloads { get; private set; }
        public string Description => "fake/source";

        public FakeSource(params (string Name, string Content)[] files)
        {
            _files = files.ToDictionary(f => f.Name, f => Encoding.UTF8.GetBytes(f.Content));
        }

        public Task<FileListInfo> GetFileListAsync(CancellationToken ct = default) =>
            Task.FromResult(new FileListInfo(Description, _files
                .Select(kv => new FileEntry(kv.Key, "mem://" + kv.Key, BlobSha(kv.Value), kv.Value.Length))
                .ToList()));

        public Task<byte[]> DownloadFileAsync(FileEntry file, CancellationToken ct = default)
        {
            Downloads++;
            return Task.FromResult(_files[file.Name]);
        }

        private static string BlobSha(byte[] content)
        {
            var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
            using var sha = System.Security.Cryptography.SHA1.Create();
            sha.TransformBlock(header, 0, header.Length, null, 0);
            sha.TransformFinalBlock(content, 0, content.Length);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
    }

    [Fact]
    public async Task Apply_writes_new_files_mirroring_subfolders()
    {
        var source  = new FakeSource(("hunt.cmd", "put hunt\n"), ("GenieHunter/hunt.cmd", "goto start\n"));
        var updater = new ScriptsUpdater(_dir, new[] { source });

        var result = await updater.ApplyAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("put hunt\n",   File.ReadAllText(Path.Combine(_dir, "hunt.cmd")));
        Assert.Equal("goto start\n", File.ReadAllText(Path.Combine(_dir, "GenieHunter", "hunt.cmd")));
    }

    [Fact]
    public async Task Apply_skips_shamatching_files_without_downloading()
    {
        var source  = new FakeSource(("a.cmd", "alpha\n"), ("b.cmd", "beta\n"));
        var updater = new ScriptsUpdater(_dir, new[] { source });

        await updater.ApplyAsync();
        Assert.Equal(2, source.Downloads);

        // Second pull: both files unchanged → zero downloads.
        var second = await updater.ApplyAsync();
        Assert.True(second.Succeeded);
        Assert.Equal(2, source.Downloads);
        Assert.Contains("2 already current", second.Summary);
    }

    [Fact]
    public async Task Apply_overwrites_locally_changed_file()
    {
        var source  = new FakeSource(("a.cmd", "upstream\n"));
        var updater = new ScriptsUpdater(_dir, new[] { source });

        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.cmd"), "local edit\n");

        var result = await updater.ApplyAsync();

        Assert.True(result.Succeeded);
        Assert.Contains("1 updated", result.Summary);
        Assert.Equal("upstream\n", File.ReadAllText(Path.Combine(_dir, "a.cmd")));
    }

    [Fact]
    public async Task Apply_refuses_paths_that_escape_the_scripts_folder()
    {
        var source  = new FakeSource(("../evil.cmd", "boom\n"), ("ok.cmd", "fine\n"));
        var updater = new ScriptsUpdater(_dir, new[] { source });

        var result = await updater.ApplyAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("escapes"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "evil.cmd")));
        Assert.True(File.Exists(Path.Combine(_dir, "ok.cmd")));
    }

    [Fact]
    public async Task Check_counts_new_and_changed_without_downloading()
    {
        var source  = new FakeSource(("a.cmd", "one\n"), ("sub/b.cmd", "two\n"));
        var updater = new ScriptsUpdater(_dir, new[] { source });

        var before = await updater.CheckAsync();
        Assert.True(before.UpdateAvailable);
        Assert.Contains("2 new", before.LatestVersion);
        Assert.Equal(0, source.Downloads);
        Assert.Equal(
            new[] { new UpdateChange("a.cmd", true), new UpdateChange("sub/b.cmd", true) },
            before.Changes!.OrderBy(c => c.Name));

        await updater.ApplyAsync();
        File.WriteAllText(Path.Combine(_dir, "a.cmd"), "drifted\n");

        var after = await updater.CheckAsync();
        Assert.True(after.UpdateAvailable);
        Assert.Contains("1 changed", after.LatestVersion);
        Assert.Equal(new[] { new UpdateChange("a.cmd", false) }, after.Changes);
    }

    [Fact]
    public void Extension_filter_parses_comma_separated_list()
    {
        Assert.Equal(new[] { ".cmd", ".js", ".inc" }, GithubTreeSource.ParseExtensions(".cmd,.js,.inc"));
        Assert.Equal(new[] { ".cmd" },                GithubTreeSource.ParseExtensions("cmd"));
        Assert.Empty(GithubTreeSource.ParseExtensions(null));
        Assert.Empty(GithubTreeSource.ParseExtensions("  "));
    }
}
