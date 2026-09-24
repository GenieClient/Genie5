using System.Collections.Concurrent;
using Genie.Core.Events;

namespace Genie.App.ViewModels;

/// <summary>
/// DR's server-described container windows (public #336) — the backpack, quiver or
/// any container DR exposes as <c>&lt;container id='stow' title="My Backpack" …/&gt;</c>
/// followed by one <c>&lt;inv id='stow'&gt;</c> per item. The parser puts those items on
/// the container's own stream (<see cref="ContainerStreams"/>), so they no longer run
/// into the worn-items list in My Inventory; this routes them to a window of their
/// own, titled with DR's name for the container.
///
/// <para><b>Created hidden.</b> DR re-sends resident containers at every login, and
/// Genie 4 never had these windows, so popping one open each session would be new
/// and intrusive behaviour. The window fills in the background and is opened from
/// Window ▸ Server Dialogs; once open, it stays wherever the layout puts it.</para>
///
/// <para><c>&lt;clearContainer&gt;</c> empties it before a refill, so a re-<c>look</c>
/// replaces the contents instead of appending a second copy.</para>
/// </summary>
public sealed partial class ContainerWindowRouter
{
    private readonly Func<string, PluginWindowViewModel> _getOrCreateHidden;
    private readonly Func<string, PluginWindowViewModel?> _tryGet;
    private readonly ConcurrentDictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="getOrCreateHidden">The window for a name, created WITHOUT being
    /// shown if it doesn't exist yet.</param>
    /// <param name="tryGet">The window for a name, or null — never creates one.</param>
    public ContainerWindowRouter(Func<string, PluginWindowViewModel> getOrCreateHidden,
                                 Func<string, PluginWindowViewModel?> tryGet)
    {
        _getOrCreateHidden = getOrCreateHidden;
        _tryGet            = tryGet;
    }

    /// <summary>The window name for a container id: DR's title once known
    /// ("My Backpack"), else the id itself.</summary>
    public string WindowName(string logicalId) =>
        _titles.TryGetValue(logicalId, out var title) && title.Length > 0 ? title : logicalId;

    public void OnContainer(ContainerEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.LogicalId) && !string.IsNullOrWhiteSpace(e.Title))
            _titles[e.LogicalId] = e.Title.Trim();
    }

    public void OnClear(ContainerClearEvent e) => _tryGet(WindowName(e.LogicalId))?.Clear();

    /// <summary>Route one text line; false when it isn't a container's.</summary>
    public bool OnText(TextEvent e)
    {
        if (ContainerStreams.IdOf(e.Stream) is not { Length: > 0 } id) return false;
        var text = e.Text?.TrimEnd();
        if (string.IsNullOrWhiteSpace(text)) return true;

        // DR Prime sends no <container title=…> — only Platinum captures carry it
        // — but every burst opens with a header naming the container ("In the
        // backpack:"). Name the window from that rather than the bare id ("stow").
        if (!_titles.ContainsKey(id) && HeaderRegex().Match(text) is { Success: true } h)
        {
            var noun = h.Groups["noun"].Value.Trim();
            _titles[id] = char.ToUpperInvariant(noun[0]) + noun[1..];
        }

        _getOrCreateHidden(WindowName(id)).AppendLine(text);
        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*In (?:the |your |an? )?(?<noun>[^:]{1,60}):\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex HeaderRegex();
}
