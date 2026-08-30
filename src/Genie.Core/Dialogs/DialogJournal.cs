using System.Text.RegularExpressions;

namespace Genie.Core.Dialogs;

/// <summary>
/// The #156 dialog capture journal: the raw XML of the FIRST sighting of each
/// server dialog id is appended to <c>Logs/dialog_journal.xml</c>, so
/// real-world dialog schemas (bank / store / feats / profile-edit / …) accrue
/// from normal play with zero effort and become renderer fixtures. Seen ids
/// persist across sessions by re-scanning the journal's marker comments at
/// construction. Excluded ids (injuries, minivitals) never journal — their
/// schemas are already test fixtures. Never throws: a journaling failure
/// must not touch the game loop.
/// </summary>
public sealed class DialogJournal
{
    public const string FileName = "dialog_journal.xml";

    private static readonly Regex MarkerRe =
        new("<!-- dialog id=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly string _path;
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase)
    {
        "injuries", "minivitals",
    };
    private readonly HashSet<string> _openLogged = new(StringComparer.OrdinalIgnoreCase);

    public DialogJournal(string logsDir)
    {
        _path = Path.Combine(logsDir, FileName);
        try
        {
            if (File.Exists(_path))
                foreach (Match m in MarkerRe.Matches(File.ReadAllText(_path)))
                    _seen.Add(m.Groups[1].Value);
        }
        catch { /* unreadable journal — start fresh; appends may still work */ }
    }

    /// <summary>A dialog's <c>&lt;openDialog&gt;</c> tag (geometry/title). Logged
    /// once per unseen id, WITHOUT marking the id seen — the content-bearing
    /// dialogData block that follows completes the entry via
    /// <see cref="Observe"/>.</summary>
    public void ObserveOpen(string dialogId, string rawXml)
    {
        if (string.IsNullOrEmpty(dialogId) || string.IsNullOrEmpty(rawXml)) return;
        lock (_gate)
        {
            if (_seen.Contains(dialogId) || !_openLogged.Add(dialogId)) return;
            Append($"<!-- openDialog id=\"{dialogId}\" {Stamp()} -->", rawXml);
        }
    }

    /// <summary>Record a dialogData block if its dialog id is new. Returns true
    /// exactly once per id (the caller may announce the capture).</summary>
    public bool Observe(string dialogId, string rawXml)
    {
        if (string.IsNullOrEmpty(dialogId) || string.IsNullOrEmpty(rawXml)) return false;
        lock (_gate)
        {
            if (!_seen.Add(dialogId)) return false;
            if (!Append($"<!-- dialog id=\"{dialogId}\" first seen {Stamp()} -->", rawXml))
            {
                _seen.Remove(dialogId);   // failed write — try again next time
                return false;
            }
            return true;
        }
    }

    private static string Stamp() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'");

    private bool Append(string marker, string rawXml)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, marker + Environment.NewLine + rawXml + Environment.NewLine);
            return true;
        }
        catch { return false; }
    }
}
