namespace Genie.Core.Update;

/// <summary>
/// Common shape for every update target — Core App, Maps, individual plugins.
/// Each implementation knows what "installed" and "latest" mean for its domain
/// (SemVer for app/plugin builds, last-commit-sha for file-list sources like
/// the maps repo) and how to apply a refresh in-place.
///
/// The split between <see cref="IUpdater"/> and <see cref="Sources.IFileListSource"/>
/// is deliberate: Sources know how to TALK to a remote (GitHub Releases vs.
/// GitHub Contents vs. an arbitrary HTTP manifest), Updaters know what to DO
/// with what came back (drop a single asset, merge XML preserving local
/// fingerprints, unload-then-replace a loaded plugin DLL).
/// </summary>
public interface IUpdater
{
    /// <summary>Display name surfaced in the Updates dialog (e.g. "Maps", "Plugin_EXPTrackerV5").</summary>
    string Name { get; }

    /// <summary>
    /// What identifies the currently installed version on the user's machine —
    /// a SemVer string for release-shaped updaters or a "tip@{sha}" / file count
    /// for file-list updaters. Used to render the "Installed: …" line and to
    /// compare against <see cref="UpdateCheckResult.LatestVersion"/>.
    /// </summary>
    string CurrentVersion { get; }

    /// <summary>
    /// Ask every configured source whether there's anything newer than the
    /// currently installed state. Does NOT download payloads — meant to be
    /// cheap enough for an auto-check-on-startup background call.
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Apply the latest available update. Progress callback is invoked as the
    /// implementation discovers + processes work items so the GUI can show a
    /// per-item line; the implementation is responsible for marshalling back
    /// to the UI thread if needed.
    /// </summary>
    Task<UpdateApplyResult> ApplyAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>Result of a non-mutating availability check.</summary>
/// <param name="UpdateAvailable">
///   True if at least one source has new content. For file-list sources this
///   means at least one file's sha differs; for release sources it means the
///   latest version is strictly newer than the installed one.</param>
/// <param name="LatestVersion">The latest version string (informational).</param>
/// <param name="Notes">Optional human-readable release notes / change summary.</param>
/// <param name="Changes">
///   Per-file detail for file-list updaters (Maps, Scripts): which remote files
///   are new or changed. Null for release-shaped updaters. Drives the hover
///   list on the Updates dialog's status line.</param>
public sealed record UpdateCheckResult(
    bool                          UpdateAvailable,
    string                        LatestVersion,
    string?                       Notes,
    IReadOnlyList<UpdateChange>?  Changes = null);

/// <summary>One file a check found out of date — new locally, or changed upstream.</summary>
public sealed record UpdateChange(string Name, bool IsNew);

/// <summary>Result of an apply pass.</summary>
/// <param name="Succeeded">True if the apply completed without any per-item failures.</param>
/// <param name="Summary">One-line human-readable summary (e.g. "Updated 42 zones (3 new, 39 merged)").</param>
/// <param name="Errors">Per-item errors collected during the pass; empty on success.</param>
public sealed record UpdateApplyResult(
    bool                  Succeeded,
    string                Summary,
    IReadOnlyList<string> Errors);

/// <summary>
/// One unit of work in an apply pass — file N of M, with a short status word.
/// </summary>
/// <param name="Current">Work units done so far (or a 0–100 percent for sources
///   that only report a single scalar, like Velopack's download callback).</param>
/// <param name="Total">Total work units. <see cref="Current"/>/<see cref="Total"/>
///   gives the determinate bar fraction. A <see cref="Total"/> of 0 (or
///   <see cref="Indeterminate"/> = true) means "no measurable fraction — show a
///   marquee".</param>
/// <param name="Item">The phase or item label — "Downloading", "Applying patch",
///   "Verifying", a filename, etc. Drives the leading word in the status line.</param>
/// <param name="Status">Short human-readable detail for this beat ("70%",
///   "merged", "reconstructing package…").</param>
/// <param name="Indeterminate">
///   True when there's no meaningful fraction to show and the UI should render an
///   indeterminate (marquee) bar instead of a filled one. Set during phases that
///   run with no sub-progress — Velopack's delta-reconstruction tail (which is
///   what makes the raw percent appear frozen at ~70%), a plugin download, or a
///   maps file-listing pass before the work total is known.</param>
public sealed record UpdateProgress(
    int    Current,
    int    Total,
    string Item,
    string Status,
    bool   Indeterminate = false);
