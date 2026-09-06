using System.Diagnostics;
using System.Security.Cryptography;
using Genie.Core.Plugins;
using Genie.Core.Update.Sources;

namespace Genie.Core.Update.Updaters;

/// <summary>
/// Per-plugin updater. One <see cref="PluginUpdater"/> instance maps to one
/// <see cref="FeedEntry"/> in the plugins section of the feed config. Backed
/// by an <see cref="IReleaseSource"/> (today: <see cref="GithubReleasesSource"/>;
/// later: <c>HttpManifestSource</c> for non-GitHub-hosted plugins).
///
/// <para><b>Update flow.</b> Check is cheap — just hits the source's latest-release
/// endpoint. Apply downloads the asset matching the feed's <see cref="FeedEntry.AssetPattern"/>,
/// SHA256-fingerprints it for the audit log, and (if the plugin is currently
/// loaded) unloads via <see cref="PluginManager.Unload"/> before writing the
/// new DLL to the Plugins folder. Reloading is the caller's choice — Apply
/// returns success once the file is on disk, and the GUI can offer
/// "Reload now" or leave it for the next session.</para>
///
/// <para><b>Trust model in v1.</b> HTTPS-only (the GitHub asset CDN is HTTPS;
/// custom manifest URLs are validated up-front by the source). SHA256 of the
/// downloaded bytes is computed and surfaced in <see cref="UpdateApplyResult"/>;
/// no verification against a signed manifest yet — that's Phase 4 hardening
/// together with the <c>PluginKey</c>/<c>IsVerified</c> trust model ported
/// from Genie 4.</para>
/// </summary>
public sealed class PluginUpdater : IUpdater
{
    private readonly FeedEntry        _feed;
    private readonly IReleaseSource   _source;
    private readonly string           _pluginsDir;
    private readonly PluginManager?   _manager;
    private readonly string           _channel;

    public string Name           => _feed.Name;
    public string CurrentVersion => DescribeInstalled();

    /// <summary>
    /// Construct an updater for a single plugin feed.
    /// </summary>
    /// <param name="feed">
    ///   The plugins-list entry this updater represents. Drives
    ///   <see cref="Name"/> and supplies the asset filename to match.</param>
    /// <param name="source">
    ///   The release source the entry's owner/repo resolves to. Passed
    ///   separately so test code can inject a fake without going through
    ///   <see cref="GithubReleasesSource"/>.</param>
    /// <param name="pluginsDir">
    ///   Absolute path to the Plugins folder where DLLs live.</param>
    /// <param name="manager">
    ///   Optional running <see cref="PluginManager"/>. When provided, Apply
    ///   will unload-then-reload the plugin in-place so the user doesn't have
    ///   to restart. When null, Apply just drops the file — the user can
    ///   load it via the Plugins menu or restart.</param>
    /// <param name="channel">Release channel (<c>"stable"</c> or <c>"beta"</c>).</param>
    public PluginUpdater(
        FeedEntry      feed,
        IReleaseSource source,
        string         pluginsDir,
        PluginManager? manager = null,
        string         channel = "stable")
    {
        _feed       = feed;
        _source     = source;
        _pluginsDir = pluginsDir;
        _manager    = manager;
        _channel    = string.IsNullOrWhiteSpace(channel) ? "stable" : channel;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        ReleaseInfo? latest;
        try
        {
            latest = await _source.GetLatestReleaseAsync(_channel, ct);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                UpdateAvailable: false,
                LatestVersion:   $"unreachable ({ex.Message})",
                Notes:           null);
        }

        if (latest is null)
        {
            return new UpdateCheckResult(
                UpdateAvailable: false,
                LatestVersion:   "(no releases)",
                Notes:           null);
        }

        var installed = ResolveInstalledVersion();
        var newer     = installed is null || VersionComparer.IsNewer(latest.Version, installed);

        return new UpdateCheckResult(
            UpdateAvailable: newer,
            LatestVersion:   latest.Version,
            Notes:           latest.Notes);
    }

    public async Task<UpdateApplyResult> ApplyAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken          ct       = default)
    {
        var errors = new List<string>();

        ReleaseInfo? latest;
        try
        {
            // Each plugin step is a discrete, unmeasured operation (the asset
            // download doesn't stream a percent), so every beat is reported
            // indeterminate — a marquee bar rather than a 0%-then-100% jump.
            progress?.Report(new UpdateProgress(0, 1, _feed.Name, "checking", Indeterminate: true));
            latest = await _source.GetLatestReleaseAsync(_channel, ct);
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(false, $"Couldn't reach {_source.Description}: {ex.Message}", new[] { ex.Message });
        }

        if (latest is null)
            return new UpdateApplyResult(false, $"{_source.Description} has no releases on the {_channel} channel.", Array.Empty<string>());

        var assetName = ResolveAssetName(_feed.AssetPattern);
        var asset     = latest.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            return new UpdateApplyResult(false,
                $"Release {latest.Version} on {_source.Description} has no asset matching '{assetName}'.",
                Array.Empty<string>());

        byte[] bytes;
        try
        {
            progress?.Report(new UpdateProgress(0, 1, asset.Name, "downloading", Indeterminate: true));
            bytes = await _source.DownloadAssetAsync(asset, ct);
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(false, $"Download failed: {ex.Message}", new[] { ex.Message });
        }

        var sha256   = ComputeSha256(bytes);
        Directory.CreateDirectory(_pluginsDir);

        // The asset name comes from the remote release. This routine writes the file
        // and then LOADS it, so a name that escapes the Plugins folder is arbitrary
        // code at an attacker-chosen path, not just a stray write.
        if (!FeedPath.TryResolveUnder(_pluginsDir, asset.Name, allowSubdirectories: false, out var localPath))
        {
            var refusal = $"Refused '{asset.Name}': a plugin asset must be a plain filename in the Plugins folder.";
            return new UpdateApplyResult(false, refusal, new[] { refusal });
        }

        // Unload first if the plugin is currently loaded — releases the file
        // lock so the write succeeds on Windows. Best-effort: if Unload fails
        // we still try to write, since the file may just not be locked.
        var wasLoaded = false;
        var pluginId  = (string?)null;
        if (_manager is not null && File.Exists(localPath) && _manager.IsFileLoaded(localPath))
        {
            pluginId  = _manager.Plugins.FirstOrDefault(p => string.Equals(
                Path.GetFileName(localPath),
                Path.GetFileName(_feed.AssetPattern),
                StringComparison.OrdinalIgnoreCase))?.Id;

            if (pluginId is not null)
            {
                progress?.Report(new UpdateProgress(0, 1, _feed.Name, "unloading", Indeterminate: true));
                wasLoaded = _manager.Unload(pluginId);
            }
        }

        try
        {
            progress?.Report(new UpdateProgress(0, 1, asset.Name, "writing", Indeterminate: true));
            File.WriteAllBytes(localPath, bytes);
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(false, $"Write to '{localPath}' failed: {ex.Message}", new[] { ex.Message });
        }

        // Reload if it was loaded before — keep the running session functional
        // without forcing a restart.
        var reloaded = false;
        if (wasLoaded && _manager is not null)
        {
            progress?.Report(new UpdateProgress(1, 1, _feed.Name, "reloading", Indeterminate: true));
            reloaded = _manager.LoadFile(localPath);
        }

        var summary = reloaded
            ? $"Updated {asset.Name} to {latest.Version} (reloaded). SHA256 {sha256[..16]}…"
            : wasLoaded
                ? $"Updated {asset.Name} to {latest.Version} (reload failed; restart to apply). SHA256 {sha256[..16]}…"
                : $"Installed {asset.Name} {latest.Version}. SHA256 {sha256[..16]}…";

        return new UpdateApplyResult(
            Succeeded: errors.Count == 0,
            Summary:   summary,
            Errors:    errors);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve the asset filename to match in the release. Today the only
    /// supported placeholders are <c>{os}</c> and <c>{arch}</c>; managed-only
    /// plugins (the common case for now) pass a literal filename like
    /// <c>Plugin_EXPTrackerV5.dll</c> and skip placeholders entirely.
    /// </summary>
    public static string ResolveAssetName(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "";
        var os = OperatingSystem.IsWindows() ? "win"
               : OperatingSystem.IsMacOS()   ? "osx"
               : OperatingSystem.IsLinux()   ? "linux"
               : "any";
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64   => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86   => "x86",
            _ => "any",
        };
        return pattern
            .Replace("{os}",   os,   StringComparison.OrdinalIgnoreCase)
            .Replace("{arch}", arch, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Best-effort installed-version lookup. Prefers a loaded plugin's
    /// declared <see cref="Genie.Plugins.IGeniePlugin.Version"/>; falls back
    /// to the DLL's FileVersionInfo when present-but-unloaded; returns null
    /// when no file exists in the Plugins folder.
    /// </summary>
    private string? ResolveInstalledVersion()
    {
        var assetName = ResolveAssetName(_feed.AssetPattern);
        if (!FeedPath.TryResolveUnder(_pluginsDir, assetName, allowSubdirectories: false, out var localPath))
            return null;   // an unusable pattern has nothing installed under it

        // 1. Loaded plugin (most authoritative — Version is what the plugin
        // declares in its own Id metadata, not the assembly version).
        if (_manager is not null && File.Exists(localPath))
        {
            var byPath = _manager.Plugins.FirstOrDefault(p => string.Equals(
                Path.GetFileName(localPath),
                assetName,
                StringComparison.OrdinalIgnoreCase));
            if (byPath is not null) return byPath.Version;
        }

        // 2. File exists but isn't loaded — read its FileVersion.
        if (File.Exists(localPath))
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(localPath);
                return fvi.FileVersion ?? fvi.ProductVersion;
            }
            catch { /* fall through */ }
        }

        return null;
    }

    private string DescribeInstalled()
    {
        var v = ResolveInstalledVersion();
        return v ?? "(not installed)";
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
