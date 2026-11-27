using System.Diagnostics;
using System.Runtime.InteropServices;
using Genie.Core.Update;
using Velopack;
using Velopack.Sources;

namespace Genie.App.Services;

/// <summary>
/// <see cref="IUpdater"/> over Velopack's <see cref="UpdateManager"/>. Lives
/// in <c>Genie.App</c> (not <c>Genie.Core</c>) so the Velopack platform
/// dependency stays out of the pure-domain core assembly.
///
/// <para><b>Install state.</b> <see cref="UpdateManager.IsInstalled"/> is
/// only true when Genie is launched from a Velopack-built install (the
/// <c>vpk pack</c> + installer path). When the user is running from
/// <c>dotnet run</c> or a raw <c>publish/</c> directory, IsInstalled is
/// false and both Check and Apply short-circuit with a friendly diagnostic
/// instead of throwing — so the Updates dialog stays useful in dev too.</para>
///
/// <para><b>Channels.</b> Velopack's GitHubSource takes a <c>prerelease</c>
/// flag: <c>"beta"</c> in our feed config maps to <c>prerelease: true</c>
/// (include releases marked prerelease on GitHub); <c>"stable"</c> maps to
/// <c>prerelease: false</c> (skip them). Switching channels rebuilds the
/// source — cheap, no state to migrate.</para>
///
/// <para><b>Restart pivot.</b> <see cref="UpdateManager.ApplyUpdatesAndRestart"/>
/// is fire-and-forget: it spawns Velopack's helper, exits the running
/// process, and relaunches at the new version. The Apply call therefore
/// never observably "returns" in success — the process is gone by then.
/// We report success in the result for paper completeness; the caller
/// should expect the app to vanish shortly after.</para>
///
/// <para><b>Platform channel.</b> Velopack supports per-platform "channels"
/// — separate RELEASES files per OS/arch so a single GitHub release can
/// host Windows + Linux + macOS payloads side-by-side. Our
/// <c>release.yml</c> emits <c>RELEASES</c> (Windows, default channel),
/// <c>RELEASES-linux</c>, <c>RELEASES-osx</c> (arm64), and
/// <c>RELEASES-osx-x64</c>. Velopack normally auto-detects the channel
/// from the installed package's manifest, but we set
/// <see cref="UpdateOptions.ExplicitChannel"/> at runtime as
/// belt-and-suspenders — it makes the routing explicit in code,
/// survives any future Velopack default-detection change, and gives the
/// Updates dialog a stable string to display. The mapping is in
/// <see cref="ResolvePlatformChannel"/> and MUST stay in sync with the
/// channel suffixes used by the velopack* jobs in
/// <c>.github/workflows/release.yml</c>.</para>
/// </summary>
public sealed class CoreAppUpdater : IUpdater
{
    private readonly UpdateManager _mgr;   // assigned twice in the ctor, never after
    private readonly string        _channel;
    private readonly string?       _platformChannel;
    private          UpdateInfo?   _pendingUpdate;

    /// <summary>How long the download percent may sit unchanged before the
    /// watchdog flips the bar to an indeterminate "still working" state.
    /// Long enough to ride out normal network jitter, short enough that the
    /// reconstruction tail doesn't read as frozen.</summary>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(1500);

    /// <summary>Percent at/above which a plateau is treated as the delta
    /// reconstruction tail rather than a stalled download. Heuristic, tied to
    /// Velopack's roughly-70/30 download-vs-reconstruct weighting of its single
    /// progress scalar — only affects the label shown, not behaviour.</summary>
    private const int PatchPhaseStartPercent = 60;

    public string Name => "Genie 5";

    /// <summary>
    ///   Running app version per Velopack's own bookkeeping (the installed
    ///   package's manifest, NOT the assembly file version). Falls back to
    ///   the assembly informational version when running uninstalled — which
    ///   is what the Updates dialog already displays on the Core tab.
    /// </summary>
    public string CurrentVersion
        => _mgr.CurrentVersion?.ToString()
           ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
           ?? "(unknown)";

    /// <summary>True when launched from a Velopack-built install — gate for the Apply path.</summary>
    public bool IsInstalled => _mgr.IsInstalled;

    /// <summary>
    ///   The Velopack platform channel this updater is configured for —
    ///   <c>null</c> on Windows (default channel, no RELEASES suffix),
    ///   <c>"linux"</c>, <c>"osx"</c>, or <c>"osx-x64"</c> elsewhere.
    ///   Useful for diagnostic display in the Updates dialog.
    /// </summary>
    public string PlatformChannel => _platformChannel ?? "(default / win)";

    /// <summary>
    ///   True when the last <see cref="CheckAsync"/> found a release that is
    ///   LOWER than the installed one — only possible when the updater was
    ///   built with <c>allowChannelDowngrade</c> and a pre-release install is
    ///   on the stable channel (public #455).
    /// </summary>
    public bool PendingIsDowngrade => _pendingUpdate?.IsDowngrade == true;

    /// <param name="repoUrl">GitHub repository URL of the release feed.</param>
    /// <param name="channel"><c>"stable"</c> or <c>"beta"</c>.</param>
    /// <param name="allowChannelDowngrade">
    ///   Let a pre-release install on the stable channel be offered the latest
    ///   stable release even though it is a lower version (see
    ///   <see cref="ChannelDowngradePolicy"/>). Pass true only where the user
    ///   sees the offer and confirms it — the Updates dialog. The unattended
    ///   startup check and auto-apply leave it false so nobody is rolled back
    ///   without asking.</param>
    public CoreAppUpdater(string repoUrl, string channel = "stable", bool allowChannelDowngrade = false)
    {
        _channel = string.IsNullOrWhiteSpace(channel) ? "stable" : channel;
        var prerelease = string.Equals(_channel, "beta", StringComparison.OrdinalIgnoreCase);
        _platformChannel = ResolvePlatformChannel();

        var source  = new GithubSource(repoUrl, accessToken: null, prerelease: prerelease);
        var options = new UpdateOptions { ExplicitChannel = _platformChannel };
        _mgr        = new UpdateManager(source, options);

        // The installed version is only known once a manager exists, and the
        // downgrade switch is fixed at construction — so decide, then rebuild.
        if (allowChannelDowngrade
            && ChannelDowngradePolicy.MayDowngrade(_channel, _mgr.CurrentVersion?.ToString()))
        {
            options = new UpdateOptions
            {
                ExplicitChannel       = _platformChannel,
                AllowVersionDowngrade = true,
            };
            _mgr = new UpdateManager(source, options);
        }
    }

    /// <summary>
    /// Maps the current runtime platform + arch to the Velopack channel
    /// name used when packing for that platform in
    /// <c>.github/workflows/release.yml</c>. Returning <c>null</c> means
    /// "use Velopack's default channel" — which on Windows is the empty
    /// channel that produces a plain <c>RELEASES</c> file without a
    /// suffix. Add new cases here whenever a new velopack job appears in
    /// the workflow (e.g. a future <c>linux-arm64</c> build).
    /// </summary>
    private static string? ResolvePlatformChannel()
    {
        if (OperatingSystem.IsWindows())
            return null;                       // Windows  → RELEASES  (no suffix)

        if (OperatingSystem.IsLinux())
            return "linux";                    // Linux    → RELEASES-linux

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "osx",       // Apple Silicon → RELEASES-osx
                Architecture.X64   => "osx-x64",   // Intel         → RELEASES-osx-x64
                _                  => null,         // future archs → let Velopack fall back
            };
        }

        return null;                           // Unknown OS — best-effort default
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!_mgr.IsInstalled)
        {
            return new UpdateCheckResult(
                UpdateAvailable: false,
                LatestVersion:   "(dev build — install via Velopack to enable in-app updates)",
                Notes:           null);
        }

        try
        {
            _pendingUpdate = await _mgr.CheckForUpdatesAsync();
            if (_pendingUpdate is null)
            {
                return new UpdateCheckResult(
                    UpdateAvailable: false,
                    LatestVersion:   CurrentVersion,
                    Notes:           "Up to date.");
            }

            var v = _pendingUpdate.TargetFullRelease.Version.ToString();
            return new UpdateCheckResult(
                UpdateAvailable: true,
                LatestVersion:   v,
                Notes:           _pendingUpdate.IsDowngrade
                    ? $"The latest {_channel} release is older than the installed build."
                    : $"Update available on the {_channel} channel.");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                UpdateAvailable: false,
                LatestVersion:   $"unreachable ({ex.Message})",
                Notes:           null);
        }
    }

    /// <summary>
    /// Auto-update path (Help ▸ Update Settings ▸ Auto-Apply ▸ Core): download
    /// the pending update and hand it to Velopack's <c>WaitExitThenApplyUpdates</c>
    /// so it installs when the app closes — unlike <see cref="ApplyAsync"/>,
    /// this never restarts the running session. Safe to call when already
    /// up to date (quiet no-op result).
    /// </summary>
    public async Task<UpdateApplyResult> StageOnExitAsync(CancellationToken ct = default)
    {
        if (!_mgr.IsInstalled)
        {
            return new UpdateApplyResult(
                Succeeded: false,
                Summary:   "Cannot stage update — running from source (no Velopack install).",
                Errors:    new[] { "UpdateManager.IsInstalled is false." });
        }

        try
        {
            _pendingUpdate ??= await _mgr.CheckForUpdatesAsync();
            if (_pendingUpdate is null)
            {
                return new UpdateApplyResult(
                    Succeeded: true,
                    Summary:   "Already up to date.",
                    Errors:    Array.Empty<string>());
            }

            await _mgr.DownloadUpdatesAsync(_pendingUpdate, cancelToken: ct);
            _mgr.WaitExitThenApplyUpdates(_pendingUpdate, silent: true, restart: false);

            return new UpdateApplyResult(
                Succeeded: true,
                Summary:   $"v{_pendingUpdate.TargetFullRelease.Version} downloaded — installs when Genie closes.",
                Errors:    Array.Empty<string>());
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(
                Succeeded: false,
                Summary:   $"Update staging failed: {ex.Message}",
                Errors:    new[] { ex.Message });
        }
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken          ct       = default)
    {
        if (!_mgr.IsInstalled)
        {
            return new UpdateApplyResult(
                Succeeded: false,
                Summary:   "Cannot update — running from source (no Velopack install).",
                Errors:    new[] { "UpdateManager.IsInstalled is false." });
        }

        try
        {
            // CheckAsync may not have run if the caller hit Apply directly.
            _pendingUpdate ??= await _mgr.CheckForUpdatesAsync();
            if (_pendingUpdate is null)
            {
                return new UpdateApplyResult(
                    Succeeded: true,
                    Summary:   "Already up to date.",
                    Errors:    Array.Empty<string>());
            }

            // Velopack hands us a SINGLE int 0–100 from DownloadUpdatesAsync
            // that folds two very different operations together: the HTTP
            // download of the delta/base packages (incremental, ~0–70%) and
            // the CPU-bound delta *reconstruction* that rebuilds the full
            // package on disk (the ~70–100% tail, which reports NOTHING). The
            // raw percent therefore parks at ~70% during reconstruction and
            // then jumps to 100, which reads as "stuck". We break that out:
            //   • report the climbing percent as a determinate "Downloading" bar
            //   • a watchdog detects the plateau and re-reports the phase as an
            //     indeterminate "Applying patch…" so the bar keeps moving and
            //     the label explains the wait
            //   • then name the post-download beats (Verifying → Restarting).
            int lastPct       = -1;
            var sinceAdvance  = Stopwatch.StartNew();
            var stallReported = false;

            void OnVpkProgress(int pct)
            {
                lastPct = pct;
                sinceAdvance.Restart();
                stallReported = false;
                progress?.Report(new UpdateProgress(pct, 100, "Downloading", $"{pct}%"));
            }

            // Fires off the UI thread; the Progress<T> in the VM marshals back.
            using var watchdog = new System.Threading.Timer(_ =>
            {
                if (stallReported || lastPct is < 0 or >= 100) return;
                if (sinceAdvance.Elapsed < StallThreshold) return;

                stallReported = true;
                // A plateau high in the range is the reconstruction tail; a
                // plateau low down is a stalled download. Both get a marquee so
                // the bar isn't frozen, but the label stays honest about which.
                var (item, status) = lastPct >= PatchPhaseStartPercent
                    ? ("Applying patch", "reconstructing package…")
                    : ("Downloading",    "waiting for data…");
                progress?.Report(new UpdateProgress(lastPct, 100, item, status, Indeterminate: true));
            }, null, StallThreshold, TimeSpan.FromMilliseconds(500));

            progress?.Report(new UpdateProgress(0, 100, "Downloading", "starting…"));
            await _mgr.DownloadUpdatesAsync(_pendingUpdate, OnVpkProgress, cancelToken: ct);

            progress?.Report(new UpdateProgress(100, 100, "Verifying", "verifying package…", Indeterminate: true));
            progress?.Report(new UpdateProgress(100, 100, "Restarting", "relaunching…", Indeterminate: true));
            // Fire-and-forget — process exits inside this call after
            // launching the Velopack updater. The next two lines are only
            // reached if the relaunch hasn't kicked in yet (rare).
            _mgr.ApplyUpdatesAndRestart(_pendingUpdate);

            return new UpdateApplyResult(
                Succeeded: true,
                Summary:   $"Updating to {_pendingUpdate.TargetFullRelease.Version} — restarting.",
                Errors:    Array.Empty<string>());
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(
                Succeeded: false,
                Summary:   $"Update failed: {ex.Message}",
                Errors:    new[] { ex.Message });
        }
    }
}
