using System.Reflection;
using System.Xml;

namespace Genie.Core.Mapper;

/// <summary>
/// XML I/O for <c>ZoneConnections.xml</c> — the meta-graph of links
/// between zones that the multi-zone pathfinder consults.
///
/// <para>File schema:</para>
/// <code>
/// &lt;connections&gt;
///   &lt;connection id="boat-cross-throne"
///               from-zone="Map01_Crossing"  from-room="#37666999"
///               to-zone="Map35_Throne_City" to-room="#37666500"
///               verb="board boat" transit-type="boat"
///               wait-min="300" wait-max="600"
///               requires="" rt="0" notes="" /&gt;
/// &lt;/connections&gt;
/// </code>
///
/// <para>
/// First-launch behavior: if the target path doesn't exist yet, the
/// repository extracts an embedded baseline file (a documented set of
/// example DR cross-zone routes with TODO room IDs the user fills in)
/// to disk before loading. This gives new users a starter template
/// they can edit, rather than a blank file. The community Maps repo
/// seeds richer versions over time.
/// </para>
///
/// <para>
/// If the user explicitly deletes the file after seeding, we DO NOT
/// re-seed — that would feel like the app fighting their intent.
/// We track this by checking the parent Maps directory: if the
/// directory exists but the file doesn't, the user has been here
/// before and chosen to remove the file, so we just return empty.
/// The seed only fires when the parent directory has to be created
/// in the first place, OR when this is the very first run against
/// an existing Maps directory that pre-dates ZoneConnections.
/// </para>
/// </summary>
public sealed class ZoneConnectionsRepository
{
    private const string BaselineResourceName =
        "Genie.Core.Mapper.Resources.ZoneConnections.baseline.xml";

    /// <summary>
    /// Marker file dropped alongside ZoneConnections.xml the first
    /// time this repo seeds it. Its presence means "we've already
    /// run the seed step against this Maps directory" — even if the
    /// user later deletes ZoneConnections.xml, the marker stays so
    /// we don't keep re-seeding it on every Load().
    /// </summary>
    private const string SeedMarkerFileName = ".genie5-zone-connections-seeded";

    private readonly string _path;

    public ZoneConnectionsRepository(string path)
    {
        _path = path;
    }

    public IReadOnlyList<ZoneConnection> Load()
    {
        // First-launch seed: if neither the target file nor the marker
        // exists, drop the embedded baseline to disk so the user has a
        // documented starting point to edit.
        TrySeedBaseline();

        if (!File.Exists(_path)) return Array.Empty<ZoneConnection>();

        try
        {
            var doc = new XmlDocument();
            doc.Load(_path);

            var list = new List<ZoneConnection>();
            foreach (XmlElement el in doc.SelectNodes("/connections/connection") ?? (System.Collections.IEnumerable)Array.Empty<XmlElement>())
            {
                list.Add(new ZoneConnection
                {
                    Id          = el.GetAttribute("id"),
                    FromZone    = el.GetAttribute("from-zone"),
                    FromRoom    = el.GetAttribute("from-room"),
                    ToZone      = el.GetAttribute("to-zone"),
                    ToRoom      = el.GetAttribute("to-room"),
                    Verb        = el.GetAttribute("verb"),
                    TransitType = el.GetAttribute("transit-type"),
                    Requires    = el.GetAttribute("requires"),
                    RtCost      = int.TryParse(el.GetAttribute("rt"),       out var rt)      ? rt      : null,
                    WaitMin     = int.TryParse(el.GetAttribute("wait-min"), out var waitMin) ? waitMin : null,
                    WaitMax     = int.TryParse(el.GetAttribute("wait-max"), out var waitMax) ? waitMax : null,
                    Notes       = el.GetAttribute("notes"),
                    ReturnsToOrigin = string.Equals(el.GetAttribute("returns-to-origin"), "true", StringComparison.OrdinalIgnoreCase),
                });
            }
            return list;
        }
        catch
        {
            // Bad XML — return empty so the pathfinder still works.
            return Array.Empty<ZoneConnection>();
        }
    }

    public void Save(IEnumerable<ZoneConnection> connections)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  " };
        using var writer = XmlWriter.Create(_path, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("connections");

        foreach (var c in connections)
        {
            writer.WriteStartElement("connection");
            if (!string.IsNullOrEmpty(c.Id))          writer.WriteAttributeString("id", c.Id);
            if (!string.IsNullOrEmpty(c.FromZone))    writer.WriteAttributeString("from-zone", c.FromZone);
            if (!string.IsNullOrEmpty(c.FromRoom))    writer.WriteAttributeString("from-room", c.FromRoom);
            if (!string.IsNullOrEmpty(c.ToZone))      writer.WriteAttributeString("to-zone", c.ToZone);
            if (!string.IsNullOrEmpty(c.ToRoom))      writer.WriteAttributeString("to-room", c.ToRoom);
            if (!string.IsNullOrEmpty(c.Verb))        writer.WriteAttributeString("verb", c.Verb);
            if (!string.IsNullOrEmpty(c.TransitType)) writer.WriteAttributeString("transit-type", c.TransitType);
            if (!string.IsNullOrEmpty(c.Requires))    writer.WriteAttributeString("requires", c.Requires);
            if (c.RtCost.HasValue)                    writer.WriteAttributeString("rt", c.RtCost.Value.ToString());
            if (c.WaitMin.HasValue)                   writer.WriteAttributeString("wait-min", c.WaitMin.Value.ToString());
            if (c.WaitMax.HasValue)                   writer.WriteAttributeString("wait-max", c.WaitMax.Value.ToString());
            if (!string.IsNullOrEmpty(c.Notes))       writer.WriteAttributeString("notes", c.Notes);
            if (c.ReturnsToOrigin)                    writer.WriteAttributeString("returns-to-origin", "true");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>
    /// Drop the embedded <c>ZoneConnections.baseline.xml</c> to disk at
    /// the configured path, but only if (a) the file doesn't exist and
    /// (b) the seed-marker doesn't exist either (meaning we've never
    /// seeded this Maps directory before). Both conditions matter:
    /// users who deliberately delete the file after seeing the baseline
    /// shouldn't have it silently reappear next launch.
    /// </summary>
    private void TrySeedBaseline()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir)) return;

            var marker = Path.Combine(dir, SeedMarkerFileName);
            if (File.Exists(_path) || File.Exists(marker))
            {
                // Either already seeded once (marker present) OR the
                // user has a real connections file. Either way, leave
                // their state alone.
                return;
            }

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Pull the embedded baseline. The resource is keyed under
            // the assembly that owns this class (Genie.Core).
            var asm = typeof(ZoneConnectionsRepository).Assembly;
            using var stream = asm.GetManifestResourceStream(BaselineResourceName);
            if (stream is null)
            {
                // Embedded resource missing — shouldn't happen in a
                // shipped build, but don't blow up if so. Just write
                // an empty stub the user can populate via the UI.
                File.WriteAllText(_path,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<connections>\n</connections>\n");
            }
            else
            {
                using var fs = File.Create(_path);
                stream.CopyTo(fs);
            }

            // Drop the marker so we never re-seed even if the user
            // deletes the file later.
            File.WriteAllText(marker,
                "Genie 5 seeded ZoneConnections.xml here. Delete this " +
                "marker if you want the baseline restored on next launch.");
        }
        catch
        {
            // Filesystem hiccup (perms, disk full, antivirus) — swallow
            // and let Load() return empty. Pathfinding degrades to
            // single-zone routing, which is the correct fallback.
        }
    }
}
