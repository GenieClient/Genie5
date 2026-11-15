using System.Net.Http;
using Genie.Core.Update.Sources;

namespace Genie.Core.Update;

/// <summary>
/// Turns one Maps <see cref="FeedEntry"/> into the <see cref="IFileListSource"/>s
/// a <see cref="Updaters.MapsUpdater"/> pulls from. The root folder is always
/// first; each subfolder the entry opts into (<see cref="FeedEntry.IncludeFestivalMaps"/>,
/// <see cref="FeedEntry.IncludeQuestMaps"/>) follows as its own source. Order
/// matters: <see cref="Updaters.MapsUpdater"/> gives a filename to the FIRST
/// source that lists it, so a zone that exists both at the root and in a
/// subfolder is taken from the root and the subfolder copy is skipped.
///
/// The three places that used to build a <see cref="GithubContentsSource"/>
/// from a feed by hand (Updates dialog row, startup background check, Maps ▸
/// Update Maps) all go through here, so the toggles mean the same thing on
/// every path.
/// </summary>
public static class MapsFeedSources
{
    /// <summary>The subfolders <paramref name="feed"/> opts into, in pull
    /// order, as literal (un-encoded) names. Empty when both toggles are off.</summary>
    public static IReadOnlyList<string> ExtraFolders(FeedEntry feed)
    {
        var folders = new List<string>(2);
        if (feed.IncludeFestivalMaps) folders.Add(FeedEntry.FestivalMapsFolder);
        if (feed.IncludeQuestMaps)    folders.Add(FeedEntry.QuestMapsFolder);
        return folders;
    }

    /// <summary>
    /// Materialise <paramref name="feed"/>: the root source, then one source
    /// per opted-in subfolder. Only <c>github-contents</c> feeds are
    /// supported for Maps — anything else throws, as the per-row builder
    /// always did.
    /// </summary>
    public static IReadOnlyList<IFileListSource> Create(FeedEntry feed, HttpClient? http = null)
    {
        if (!string.Equals(feed.Kind, "github-contents", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Maps source kind '{feed.Kind}' is not supported yet.");

        var extension = string.IsNullOrEmpty(feed.Extension) ? null : feed.Extension;
        var sources   = new List<IFileListSource>
        {
            new GithubContentsSource(feed.Owner, feed.Repo, feed.Path, extension, http),
        };

        foreach (var folder in ExtraFolders(feed))
        {
            // FeedEntry.Path is stored already-encoded (GithubContentsSource
            // takes it as-is); the folder constants are literal, so encode
            // them here and hang them off the feed's own path.
            var encoded = Uri.EscapeDataString(folder);
            var path    = string.IsNullOrEmpty(feed.Path)
                ? encoded
                : feed.Path.TrimEnd('/') + "/" + encoded;
            sources.Add(new GithubContentsSource(
                feed.Owner, feed.Repo, path, extension, http,
                description: $"{feed.Owner}/{feed.Repo}/{folder}"));
        }

        return sources;
    }
}
