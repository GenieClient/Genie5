using System.Text.Json;

namespace Genie.Core.Update;

/// <summary>
/// JSON load/save for <see cref="FeedConfig"/>. File lives at
/// <c>{ConfigDir}/update-feeds.json</c> where <c>ConfigDir</c> is resolved
/// by <see cref="Config.GenieConfig.ConfigDir"/>. Missing or malformed
/// files fall back to <see cref="FeedConfig.CreateDefault"/> so a fresh
/// install always has the official Maps feed available.
/// </summary>
public sealed class FeedConfigStore
{
    private const string FileName = "update-feeds.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented           = true,
        PropertyNamingPolicy    = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition  = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _configDir;

    public FeedConfigStore(string configDir)
    {
        _configDir = configDir;
    }

    /// <summary>Absolute path of the feed config file.</summary>
    public string FilePath => Path.Combine(_configDir, FileName);

    /// <summary>
    /// Load the feed config, falling back to defaults if the file is missing
    /// or unreadable. Never throws — a corrupt file is treated as "first run"
    /// so the user always gets a working set of default feeds.
    /// </summary>
    public FeedConfig Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
            return FeedConfig.CreateDefault();

        try
        {
            var json = File.ReadAllText(path);
            var cfg  = JsonSerializer.Deserialize<FeedConfig>(json, JsonOpts);
            if (cfg is null) return FeedConfig.CreateDefault();

            // Migration: configs written before the Scripts section existed
            // have no "scripts" property at all — seed the same default a
            // fresh install gets (disabled community repo). A user who
            // removed every scripts row has an explicit "scripts": [] in the
            // file, which this deliberately leaves empty.
            if (cfg.Scripts.Count == 0 && !json.Contains("\"scripts\"", StringComparison.OrdinalIgnoreCase))
                cfg.Scripts.Add(FeedEntry.CommunityScripts());

            // Migration (#281): drop the seeded Plugin_EXPTrackerV5 row. Its
            // plugin id is retired now that Experience tracking is in Core, so
            // the row can only ever download a DLL the loader refuses. Only the
            // untouched seeded row is removed — if the user repointed it at
            // another owner/repo, it is theirs and stays.
            cfg.Plugins.RemoveAll(IsSeededExpTrackerRow);

            return cfg;
        }
        catch
        {
            // Corrupt JSON or schema drift — fall back rather than crash on
            // startup. The user can re-save from the Updates dialog to
            // overwrite the bad file.
            return FeedConfig.CreateDefault();
        }
    }

    /// <summary>
    /// True for the untouched seeded Plugin_EXPTrackerV5 row — matched on id AND
    /// source, so a row the user repointed elsewhere is never silently removed.
    /// </summary>
    private static bool IsSeededExpTrackerRow(FeedEntry e) =>
        string.Equals(e.Id,    FeedEntry.RetiredExpTrackerId,  StringComparison.OrdinalIgnoreCase)
     && string.Equals(e.Owner, "GenieClient",                  StringComparison.OrdinalIgnoreCase)
     && string.Equals(e.Repo,  "Plugin_EXPTrackerV5",          StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Persist the feed config to disk, creating the config directory if it
    /// doesn't exist yet. Returns true on success, false on any I/O failure
    /// (no exception is propagated — the GUI surfaces failure as a status
    /// message rather than a crash).
    /// </summary>
    public bool Save(FeedConfig config)
    {
        try
        {
            Directory.CreateDirectory(_configDir);
            var json = JsonSerializer.Serialize(config, JsonOpts);
            File.WriteAllText(FilePath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
