using Genie.Core.Update.Sources;

namespace Genie.Core.Update.Updaters;

/// <summary>
/// Pulls <c>.cmd</c> / <c>.js</c> / <c>.inc</c> script files from one or more
/// <see cref="IFileListSource"/>s into the Scripts directory — git-pull
/// semantics: new files are added, changed files are overwritten, files whose
/// git blob sha already matches the remote are skipped without downloading.
/// Subdirectory structure from the source is mirrored on disk (the script
/// engine resolves <c>run GenieHunter/hunt</c> style relative paths), and
/// local files the source doesn't know about are never touched or deleted.
///
/// Note the overwrite caveat: there is no merge. A locally-edited script that
/// shares a name with an upstream file is replaced on Apply — users who
/// customize a repo script should rename their copy, or set a separate
/// <c>reposcriptdir</c> (issue #221): the callers then pass that folder as
/// <c>scriptsDir</c> here, the script engine resolves the user's ScriptDir
/// first, and a local copy shadows the repo copy instead of being clobbered.
/// The Updates dialog's Scripts tab says this in its tip line.
///
/// Implements <see cref="IUpdater"/> so the Updates dialog can treat Scripts
/// the same way it treats Maps / Plugins / the core app.
/// </summary>
public sealed class ScriptsUpdater : IUpdater
{
    private readonly string            _scriptsDir;
    private readonly IFileListSource[] _sources;

    public string Name           => "Scripts";
    public string CurrentVersion => DescribeInstalled();

    /// <summary>
    /// Construct a Scripts updater that pulls from every supplied source in
    /// turn. Empty source list is valid and means "nothing to check / apply".
    /// </summary>
    public ScriptsUpdater(string scriptsDir, IEnumerable<IFileListSource> sources)
    {
        _scriptsDir = scriptsDir;
        _sources    = sources?.ToArray() ?? Array.Empty<IFileListSource>();
    }

    /// <summary>
    /// Count how many remote files are new or differ from local. Cheap — the
    /// blob-sha comparison means no file bytes are downloaded, same as
    /// <see cref="MapsUpdater.CheckAsync"/>.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        int newCount     = 0;
        int changedCount = 0;
        var notesLines   = new List<string>();

        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();
            FileListInfo listing;
            try
            {
                listing = await source.GetFileListAsync(ct);
            }
            catch (Exception ex)
            {
                notesLines.Add($"{source.Description}: {ex.Message}");
                continue;
            }

            foreach (var entry in listing.Files)
            {
                if (!TryResolveLocalPath(entry.Name, out var localPath))
                    continue; // hostile / malformed path — Apply reports it, Check just skips

                if (!File.Exists(localPath))
                {
                    newCount++;
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.Sha) &&
                    GithubContentsSource.GitBlobSha1(localPath) != entry.Sha)
                {
                    changedCount++;
                }
            }
        }

        int total = newCount + changedCount;
        var summary = total switch
        {
            0 => "Up to date",
            _ => $"{total} update(s) available ({newCount} new, {changedCount} changed)"
        };

        return new UpdateCheckResult(
            UpdateAvailable: total > 0,
            LatestVersion:   summary,
            Notes:           notesLines.Count > 0 ? string.Join("\n", notesLines) : null);
    }

    /// <summary>
    /// Pull every enabled source: list, then per file download-and-write —
    /// unless the local blob sha already matches, in which case the file is
    /// skipped without a download (this is what makes re-running Update after
    /// a fresh pull near-instant). Per-file failures are collected rather
    /// than fatal so one bad file doesn't abort the whole pull.
    /// </summary>
    public async Task<UpdateApplyResult> ApplyAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken          ct       = default)
    {
        Directory.CreateDirectory(_scriptsDir);

        progress?.Report(new UpdateProgress(0, 0, "Listing", "fetching file list…", Indeterminate: true));
        var workItems = new List<(IFileListSource Source, FileEntry Entry)>();
        var errors    = new List<string>();

        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var listing = await source.GetFileListAsync(ct);
                workItems.AddRange(listing.Files.Select(f => (source, f)));
            }
            catch (Exception ex)
            {
                errors.Add($"{source.Description} listing failed: {ex.Message}");
            }
        }

        int total   = workItems.Count;
        int newRows = 0, updatedRows = 0, currentRows = 0;

        for (int i = 0; i < workItems.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (source, entry) = workItems[i];

            try
            {
                if (!TryResolveLocalPath(entry.Name, out var localPath))
                {
                    errors.Add($"{entry.Name}: refused — path escapes the Scripts folder.");
                    continue;
                }

                bool isNew = !File.Exists(localPath);
                if (!isNew &&
                    !string.IsNullOrEmpty(entry.Sha) &&
                    GithubContentsSource.GitBlobSha1(localPath) == entry.Sha)
                {
                    currentRows++;
                    progress?.Report(new UpdateProgress(i + 1, total, entry.Name, "up to date"));
                    continue;
                }

                progress?.Report(new UpdateProgress(i + 1, total, entry.Name, "downloading"));
                var bytes = await source.DownloadFileAsync(entry, ct);

                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                await File.WriteAllBytesAsync(localPath, bytes, ct);

                if (isNew) newRows++; else updatedRows++;
                progress?.Report(new UpdateProgress(i + 1, total, entry.Name, isNew ? "new" : "updated"));
            }
            catch (Exception ex)
            {
                errors.Add($"{entry.Name}: {ex.Message}");
                progress?.Report(new UpdateProgress(i + 1, total, entry.Name, $"failed: {ex.Message}"));
            }
        }

        var counts  = $"{newRows} new, {updatedRows} updated, {currentRows} already current";
        var summary = errors.Count == 0
            ? $"Pulled {total} script(s) ({counts})."
            : $"Pulled {total} script(s) ({counts}, {errors.Count} failed).";

        return new UpdateApplyResult(
            Succeeded: errors.Count == 0,
            Summary:   summary,
            Errors:    errors);
    }

    /// <summary>
    /// Map a source-relative name (may contain forward-slash subdirectories)
    /// to an absolute path under the Scripts dir. Returns false for anything
    /// that would land outside it — rooted paths, drive letters, <c>..</c>
    /// traversal — since the name ultimately comes from a remote repo.
    /// </summary>
    private bool TryResolveLocalPath(string name, out string localPath)
        => FeedPath.TryResolveUnder(_scriptsDir, name, allowSubdirectories: true, out localPath);

    /// <summary>
    /// Human-readable "installed" description for the Updates dialog.
    /// Scripts have no SemVer; we count the local script files (recursively,
    /// since pulled repos mirror their subfolders).
    /// </summary>
    private string DescribeInstalled()
    {
        if (!Directory.Exists(_scriptsDir)) return "0 scripts";
        var count = new[] { "*.cmd", "*.js", "*.inc" }
            .Sum(pattern => Directory.EnumerateFiles(_scriptsDir, pattern, SearchOption.AllDirectories).Count());
        return $"{count} script(s)";
    }
}
