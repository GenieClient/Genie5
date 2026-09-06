using System.Text.Json;
using Genie.Core.Mapper;
using Genie.Core.Update.Sources;

namespace Genie.Core.Update.Updaters;

/// <summary>
/// Pulls zone XML files from one or more <see cref="IFileListSource"/>s,
/// imports each into our JSON zone format, and merges the fresh structure
/// with whatever is already on disk so locally-collected per-node data —
/// chiefly <see cref="MapNode.ServerRoomId"/> values the player has visited —
/// survives the refresh.
///
/// Replaces the original <c>MapRepoUpdater</c> (which hardcoded
/// GenieClient/Maps + bundled the GitHub HTTP plumbing). The HTTP work now
/// lives in <see cref="GithubContentsSource"/>; this class is pure
/// domain logic on top of the source abstraction, which means alternate
/// or community map repos work for free as long as they expose the same
/// zone-XML-files-in-a-folder shape.
///
/// Implements <see cref="IUpdater"/> so the Updates dialog can treat Maps
/// the same way it treats plugins / the core app.
/// </summary>
public sealed class MapsUpdater : IUpdater
{
    private readonly MapZoneRepository      _repo;
    private readonly string                 _mapsDir;
    private readonly IFileListSource[]      _sources;

    public string Name           => "Maps";
    public string CurrentVersion => DescribeInstalled();

    /// <summary>
    /// Construct a Maps updater that pulls from every supplied source in turn.
    /// Pass the enabled feeds materialised as <see cref="GithubContentsSource"/>
    /// (or any other <see cref="IFileListSource"/>) — empty list is valid and
    /// means "nothing to check / nothing to apply".
    /// </summary>
    public MapsUpdater(
        MapZoneRepository           repo,
        string                      mapsDir,
        IEnumerable<IFileListSource> sources)
    {
        _repo    = repo;
        _mapsDir = mapsDir;
        _sources = sources?.ToArray() ?? Array.Empty<IFileListSource>();
    }

    /// <summary>
    /// Count how many remote files differ from local (or don't exist locally).
    /// The "version" surfaced to the UI is the file count — there's no SemVer
    /// for a maps repo, just "N changes available".
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_mapsDir);

        int newCount     = 0;
        int changedCount = 0;
        var notesLines   = new List<string>();
        var recorded     = LoadShaManifest();

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
                if (!FeedPath.TryResolveUnder(_mapsDir, entry.Name, allowSubdirectories: false, out var localPath))
                    continue;   // hostile / malformed name — ApplyAsync reports it, Check just skips
                if (!File.Exists(localPath))
                {
                    newCount++;
                    continue;
                }

                // Cheap diff: compare the upstream sha against the one we
                // recorded when we last applied that file. Hashing the LOCAL
                // file is useless here — ApplyAsync re-serializes through
                // Genie4MapExporter (own formatting + merged server_ids), so
                // the local bytes never match the upstream blob and every
                // zone would count as "changed" forever. Skip the comparison
                // when the source exposes no hash — we'd have to download to
                // know, which defeats the point of a cheap CheckAsync. A file
                // with no recorded sha (pre-manifest install) counts as
                // changed once; the next apply records it and it goes quiet.
                if (!string.IsNullOrEmpty(entry.Sha) &&
                    (!recorded.TryGetValue(entry.Name, out var appliedSha) ||
                     appliedSha != entry.Sha))
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
    /// Pull every enabled source's file list, download each <c>*.xml</c>,
    /// parse, merge with any existing zone of the same filename, and write
    /// back. Per-file failures are collected rather than fatal so a single
    /// bad zone doesn't abort the whole update.
    /// </summary>
    public async Task<UpdateApplyResult> ApplyAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken          ct       = default)
    {
        Directory.CreateDirectory(_mapsDir);

        // First pass across all sources: build a flat work list so progress
        // counters reflect the real total, not per-source totals. The total
        // isn't known yet, so report this beat as indeterminate (marquee).
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

        int total      = workItems.Count;
        int mergedRows = 0;
        int newRows    = 0;
        int currentRows = 0;
        var recorded   = LoadShaManifest();

        for (int i = 0; i < workItems.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (source, entry) = workItems[i];

            // Skip the download when the upstream blob is the one we already
            // applied — mirrors CheckAsync, so auto-apply on startup doesn't
            // re-pull every zone on every launch. Keyed on the RECORDED sha,
            // not a hash of the local file, because the local file is our own
            // re-serialization (see CheckAsync).
            if (!string.IsNullOrEmpty(entry.Sha) &&
                FeedPath.TryResolveUnder(_mapsDir, entry.Name, allowSubdirectories: false, out var currentPath) &&
                File.Exists(currentPath) &&
                recorded.TryGetValue(entry.Name, out var appliedSha) &&
                appliedSha == entry.Sha)
            {
                currentRows++;
                progress?.Report(new UpdateProgress(i + 1, total, entry.Name, "already current"));
                continue;
            }

            progress?.Report(new UpdateProgress(i + 1, total, entry.Name, "downloading"));

            try
            {
                var bytes = await source.DownloadFileAsync(entry, ct);
                var xml   = System.Text.Encoding.UTF8.GetString(bytes);
                var fallback = Path.GetFileNameWithoutExtension(entry.Name);
                var fresh    = Genie4MapImporter.ImportFromContent(xml, fallback);

                // Use the upstream filename (not the zone's <zone name="..."/>
                // attribute) so PR-friendly identity matches the upstream repo
                // exactly. Two zones with the same display name but different
                // filenames stay distinct on disk.
                if (!FeedPath.TryResolveUnder(_mapsDir, entry.Name, allowSubdirectories: false, out var localPath))
                {
                    errors.Add($"{entry.Name}: refused — the name is not a plain filename in the Maps folder.");
                    progress?.Report(new UpdateProgress(i + 1, total, entry.Name, "refused: unsafe path"));
                    continue;
                }
                bool isNew    = !File.Exists(localPath);
                if (!isNew)
                {
                    var existing = _repo.Load(localPath);
                    if (existing != null)
                    {
                        MergePreservingServerIds(existing, fresh);
                        mergedRows++;
                    }
                    else
                    {
                        // File on disk but unreadable — treat as a fresh import.
                        newRows++;
                    }
                }
                else
                {
                    newRows++;
                }

                _repo.Save(localPath, fresh);
                if (!string.IsNullOrEmpty(entry.Sha))
                    recorded[entry.Name] = entry.Sha;
                progress?.Report(new UpdateProgress(i + 1, total, entry.Name, isNew ? "new" : "merged"));
            }
            catch (Exception ex)
            {
                errors.Add($"{entry.Name}: {ex.Message}");
                progress?.Report(new UpdateProgress(i + 1, total, entry.Name, $"failed: {ex.Message}"));
            }
        }

        SaveShaManifest(recorded);

        var summary = errors.Count == 0
            ? $"Updated {newRows + mergedRows} zone(s) ({newRows} new, {mergedRows} merged, {currentRows} already current)."
            : $"Updated {newRows + mergedRows} zone(s) ({newRows} new, {mergedRows} merged, {currentRows} already current, {errors.Count} failed).";

        return new UpdateApplyResult(
            Succeeded: errors.Count == 0,
            Summary:   summary,
            Errors:    errors);
    }

    /// <summary>
    /// Copy locally-collected per-node fields from <paramref name="existing"/>
    /// onto <paramref name="fresh"/>, keyed on Genie4 node id. Right now
    /// that's just <see cref="MapNode.ServerRoomId"/> — populated when the
    /// player has visited the room and seen its <c>&lt;nav rm="…"/&gt;</c>.
    /// Everything else (title, description, exits, coordinates, notes) is
    /// taken from the upstream refresh, since that's what the user wanted
    /// to update.
    /// </summary>
    private static void MergePreservingServerIds(MapZone existing, MapZone fresh)
    {
        foreach (var (id, freshNode) in fresh.Nodes)
        {
            if (existing.Nodes.TryGetValue(id, out var existingNode) &&
                !string.IsNullOrEmpty(existingNode.ServerRoomId))
            {
                freshNode.ServerRoomId = existingNode.ServerRoomId;
            }
        }
    }

    // ── Applied-sha manifest ─────────────────────────────────────────────
    //
    // Maps whose local file is a re-serialization of upstream (import →
    // merge server_ids → export) can't be diffed by hashing the local file.
    // Instead we record the UPSTREAM blob sha of each file at apply time and
    // diff future listings against that. Dot-named so MapZoneRepository's
    // *.xml enumeration and DescribeInstalled never see it as a zone.

    private string ShaManifestPath => Path.Combine(_mapsDir, ".map-shas.json");

    private Dictionary<string, string> LoadShaManifest()
    {
        try
        {
            if (File.Exists(ShaManifestPath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(ShaManifestPath));
                if (loaded != null)
                    return new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* corrupt manifest → treat all as unrecorded; next apply rebuilds it */ }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveShaManifest(Dictionary<string, string> shas)
    {
        try
        {
            File.WriteAllText(ShaManifestPath,
                JsonSerializer.Serialize(shas, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort — a failed write just means a re-check re-flags files */ }
    }

    /// <summary>
    /// Human-readable "installed" description for the Updates dialog.
    /// Maps don't have a SemVer; we count the local *.xml files.
    /// </summary>
    private string DescribeInstalled()
    {
        if (!Directory.Exists(_mapsDir)) return "0 zones";
        var count = Directory.EnumerateFiles(_mapsDir, "*.xml", SearchOption.TopDirectoryOnly).Count();
        return $"{count} zone(s)";
    }
}
