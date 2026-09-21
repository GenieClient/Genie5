namespace Genie.Core.Update;

/// <summary>
/// Top-level on-disk schema for the update system. Loaded from
/// <c>{ConfigDir}/update-feeds.json</c> via <see cref="FeedConfigStore"/>;
/// missing files are seeded with <see cref="CreateDefault"/>.
///
/// Four sections — Core, Maps, Plugins, Scripts — match the four update kinds.
/// Core is a single feed (you can only run one Genie 5 install at a time);
/// Maps, Plugins, and Scripts are lists so users can subscribe to multiple
/// sources.
/// </summary>
public sealed class FeedConfig
{
    public CoreFeed         Core    { get; set; } = CoreFeed.Default();
    public List<FeedEntry>  Maps    { get; set; } = new();
    public List<FeedEntry>  Plugins { get; set; } = new();
    public List<FeedEntry>  Scripts { get; set; } = new();

    // ── Per-kind update policies (Help ▸ Update Settings) ───────────────────
    // One policy per update kind: whether the startup background check covers
    // it, and whether a found update is auto-applied. Old update-feeds.json
    // files without these properties deserialize to the defaults (check ON,
    // auto-apply OFF) — same behavior those files had before policies existed.
    public UpdatePolicy CorePolicy    { get; set; } = new();
    public UpdatePolicy MapsPolicy    { get; set; } = new();
    public UpdatePolicy PluginsPolicy { get; set; } = new();
    public UpdatePolicy ScriptsPolicy { get; set; } = new();

    /// <summary>Out-of-the-box defaults: official Genie5 core, official Maps repo,
    /// and the community scripts repo (disabled — scripts act as your character,
    /// so pulling them is opt-in). Plugins ships EMPTY: the only entry it ever
    /// carried was Plugin_EXPTrackerV5, whose id is retired now that Experience
    /// tracking lives in Core, so seeding it only ever downloaded a DLL the
    /// loader then refused to register (#281).</summary>
    public static FeedConfig CreateDefault() => new()
    {
        Core    = CoreFeed.Default(),
        Maps    = new() { FeedEntry.OfficialMaps()     },
        Plugins = new(),
        Scripts = new() { FeedEntry.CommunityScripts() },
    };
}

/// <summary>
/// Per-update-kind behavior toggles, driven by the Help ▸ Update Settings
/// menu. <see cref="CheckOnStartup"/> gates the silent background check that
/// runs at app start (drives the Help ● badge + status-bar notice);
/// <see cref="AutoApply"/> additionally applies what that check finds —
/// Maps / Plugins / Scripts install immediately, Core downloads and stages
/// via Velopack to apply when the app exits (never a surprise mid-session
/// restart). Auto-apply defaults OFF for every kind: pulling code or a new
/// client build without a click is strictly opt-in.
/// </summary>
public sealed class UpdatePolicy
{
    public bool CheckOnStartup { get; set; } = true;
    public bool AutoApply      { get; set; }
}

/// <summary>
/// Settings for the Core App updater. Only one Core feed exists at a time
/// (you don't subscribe to multiple Genie 5 builds — you pick a channel).
/// </summary>
public sealed class CoreFeed
{
    /// <summary>Source kind; today only <c>"github-releases"</c> is supported for Core.</summary>
    public string Kind         { get; set; } = "github-releases";
    public string Owner        { get; set; } = "GenieClient";
    public string Repo         { get; set; } = "Genie5";

    /// <summary>Release channel — <c>"stable"</c> or <c>"beta"</c>. Beta = GitHub releases marked prerelease.
    /// Defaults to <c>"beta"</c> during the alpha/beta period: every shipped build is a GitHub
    /// prerelease, so a <c>"stable"</c> default would never surface an update. Flip back to
    /// <c>"stable"</c> at the 5.0.0 GA release (when a non-prerelease exists to track).</summary>
    public string Channel      { get; set; } = "beta";

    /// <summary>
    ///   Asset filename pattern. Supports <c>{os}</c> (win/osx/linux) and
    ///   <c>{arch}</c> (x64/arm64) placeholders; the Core updater fills them
    ///   in from the runtime before matching against release assets.</summary>
    public string AssetPattern { get; set; } = "Genie5-{os}-{arch}.zip";

    /// <summary>Superseded by <see cref="FeedConfig.CorePolicy"/>.CheckOnStartup —
    /// kept only so pre-policy JSON round-trips without a schema error. Never
    /// consulted.</summary>
    public bool CheckOnStartup { get; set; } = true;

    public static CoreFeed Default() => new();
}

/// <summary>One entry in the Maps or Plugins feeds list.</summary>
public sealed class FeedEntry
{
    /// <summary>Stable identifier for this entry — never changes, used as a dictionary key.</summary>
    public string Id            { get; set; } = "";

    /// <summary>Display name shown in the Updates dialog.</summary>
    public string Name          { get; set; } = "";

    /// <summary>
    ///   Source kind — drives how the entry is materialised into an
    ///   <see cref="Sources.IFileListSource"/> or future IReleaseSource.
    ///   Today: <c>"github-contents"</c> (Maps — single flat folder),
    ///   <c>"github-tree"</c> (Scripts — whole repo tree, recursive),
    ///   <c>"github-releases"</c> (Plugins), <c>"http-manifest"</c> (Phase 2).</summary>
    public string Kind          { get; set; } = "";

    // ── github-* common fields ───────────────────────────────────────────
    public string Owner         { get; set; } = "";
    public string Repo          { get; set; } = "";

    /// <summary>Optional subdirectory for github-contents sources; root if blank.</summary>
    public string Path          { get; set; } = "";

    /// <summary>Optional file extension filter (e.g. <c>".xml"</c>). For
    /// github-tree sources a comma-separated list is accepted (<c>".cmd,.js,.inc"</c>).</summary>
    public string Extension     { get; set; } = "";

    /// <summary>Asset filename pattern for github-releases sources; supports same placeholders as <see cref="CoreFeed.AssetPattern"/>.</summary>
    public string AssetPattern  { get; set; } = "";

    // ── http-manifest fields ─────────────────────────────────────────────
    public string ManifestUrl   { get; set; } = "";

    // ── common state ─────────────────────────────────────────────────────
    public bool             Enabled     { get; set; } = true;
    public DateTimeOffset?  LastChecked { get; set; }

    /// <summary>The default GenieClient/Maps repo (XML-files-in-root, .xml-filtered).</summary>
    public static FeedEntry OfficialMaps() => new()
    {
        Id        = "official-maps",
        Name      = "GenieClient/Maps (official)",
        Kind      = "github-contents",
        Owner     = "GenieClient",
        Repo      = "Maps",
        Extension = ".xml",
        Enabled   = true,
    };

    /// <summary>
    /// The community DR scripts repo (~55 <c>.cmd</c> scripts incl. the
    /// GenieHunter suite). Ships DISABLED: scripts send commands as the
    /// player's character, so the user flips the row on (or just clicks its
    /// Update button) once they've decided they want them.
    /// </summary>
    public static FeedEntry CommunityScripts() => new()
    {
        Id        = "tirost-dr-genie-scripts",
        Name      = "Tirost/DR-Genie-Scripts (community)",
        Kind      = "github-tree",
        Owner     = "Tirost",
        Repo      = "DR-Genie-Scripts",
        Extension = ".cmd,.js,.inc",
        Enabled   = false,
    };

    /// <summary>
    /// The retired Plugin_EXPTrackerV5 release feed. Experience tracking moved
    /// into Core, so <c>genie.experience</c> is on <c>PluginManager</c>'s retired
    /// list and a downloaded DLL is refused at registration. Kept only so
    /// <see cref="FeedConfigStore"/> can recognise and drop the seeded row from
    /// existing <c>update-feeds.json</c> files; it is no longer seeded (#281).
    /// </summary>
    public static FeedEntry RetiredExpTracker() => new()
    {
        Id           = RetiredExpTrackerId,
        Name         = "Plugin_EXPTrackerV5 (official)",
        Kind         = "github-releases",
        Owner        = "GenieClient",
        Repo         = "Plugin_EXPTrackerV5",
        AssetPattern = "Plugin_EXPTrackerV5.dll",
        Enabled      = true,
    };

    /// <summary>Feed id of the seeded row retired by #281.</summary>
    public const string RetiredExpTrackerId = "official-exptracker";
}
