using System.Xml;

namespace Genie.Core.Mapper;

/// <summary>
/// Imports Genie4 XML map files into the Genie5 MapZone format.
///
/// Genie4 XML structure:
///   &lt;zone name="..." id="10"&gt;
///     &lt;node id="1" name="Room Title"&gt;
///       &lt;description&gt;...&lt;/description&gt;
///       &lt;position x="260" y="100" z="0" /&gt;
///       &lt;arc exit="north" move="north" destination="2" /&gt;
///     &lt;/node&gt;
///   &lt;/zone&gt;
///
/// Node IDs in Genie4 are integers local to the zone file. Genie5 preserves
/// them as-is so users can reference rooms by their Genie4 ID (e.g. #goto 232).
/// </summary>
public static class Genie4MapImporter
{
    public static MapZone Import(string xmlPath)
        => ImportFromContent(File.ReadAllText(xmlPath),
                             Path.GetFileNameWithoutExtension(xmlPath));

    /// <summary>
    /// Parse a Genie4-format zone XML from a string (e.g. one fetched
    /// directly from the GenieClient/Maps GitHub repo) without first
    /// writing it to disk. <paramref name="fallbackName"/> is used when
    /// the XML's root element doesn't supply a <c>name</c> attribute.
    /// </summary>
    public static MapZone ImportFromContent(string xmlContent, string fallbackName)
    {
        // Strip a leading UTF-8 BOM (U+FEFF). When the source is a byte[] decoded
        // via Encoding.UTF8.GetString — e.g. the Maps updater's download path — the
        // BOM survives as a leading character and XmlDocument.LoadXml rejects it
        // ("data at the root level is invalid"). File.ReadAllText already strips it,
        // so file-load callers never hit this; the byte-array path did. This silently
        // dropped every BOM-prefixed upstream map (Map30_Riverhaven, Map4_Crossing_
        // West_Gate, Map69_Shard_West_Gate, … — 13 of the 90 GenieClient/Maps files),
        // since the importer threw and MapsUpdater swallowed it as a per-file error.
        const char Bom = (char)0xFEFF;   // U+FEFF — UTF-8 BOM, decoded as one leading char
        if (xmlContent.Length > 0 && xmlContent[0] == Bom)
            xmlContent = xmlContent[1..];

        var doc = new XmlDocument();
        doc.LoadXml(xmlContent);

        var zoneEl = doc.DocumentElement
            ?? throw new InvalidDataException("XML has no root element.");

        var zoneName = zoneEl.GetAttribute("name");
        if (string.IsNullOrEmpty(zoneName))
            zoneName = fallbackName;

        var zone = new MapZone
        {
            Name     = zoneName,
            Genie4Id = zoneEl.GetAttribute("id"),
        };

        // ── Pass 1: build all nodes (preserve Genie4 integer IDs) ────────────
        foreach (XmlElement nodeEl in zoneEl.SelectNodes("node")!)
        {
            if (!int.TryParse(nodeEl.GetAttribute("id"), out int nodeId)) continue;

            var node = new MapNode
            {
                Id    = nodeId,
                Title = nodeEl.GetAttribute("name"),
            };

            // Description — take the first <description> child
            var descEl = nodeEl.SelectSingleNode("description");
            if (descEl != null)
                node.Description = descEl.InnerText.Trim();

            // Note — Genie4 stores it as an attribute on <node>, with multiple
            // labels separated by '|' (used by #goto for label lookup).
            var noteAttr = nodeEl.GetAttribute("note");
            if (!string.IsNullOrEmpty(noteAttr))
                node.Notes = noteAttr.Trim();

            // Color — also a node attribute (e.g. "#FF00FF")
            var colorAttr = nodeEl.GetAttribute("color");
            if (!string.IsNullOrEmpty(colorAttr))
                node.Color = colorAttr.Trim();

            // server_id — Genie 5 extension. Locally collected from <nav rm="..."/>
            // events and written back on export so users can contribute the
            // mapping upstream via PR. Not in the original Genie 4 schema, but
            // forward-compatible: unknown to old clients, harmless if seen.
            var serverIdAttr = nodeEl.GetAttribute("server_id");
            if (!string.IsNullOrEmpty(serverIdAttr))
                node.ServerRoomId = serverIdAttr.Trim();

            // tags — Genie 5 extension, '|'-separated (mirrors the note attribute).
            // Drive #goto @tag nearest-routing. Unknown to old Genie 4 clients.
            var tagsAttr = nodeEl.GetAttribute("tags");
            if (!string.IsNullOrEmpty(tagsAttr))
                foreach (var t in tagsAttr.Split('|',
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    node.Tags.Add(t);

            // Position
            var posEl = nodeEl.SelectSingleNode("position") as XmlElement;
            if (posEl != null)
            {
                int.TryParse(posEl.GetAttribute("x"), out int px);
                int.TryParse(posEl.GetAttribute("y"), out int py);
                int.TryParse(posEl.GetAttribute("z"), out int pz);
                // Genie4 uses pixel coordinates (multiples of ~20).
                // Divide by 20 to convert to grid units.
                node.X = px / 20;
                node.Y = py / 20;
                node.Z = pz;
            }

            zone.Nodes[nodeId] = node;
        }

        // ── Pass 2: resolve arcs ─────────────────────────────────────────────
        foreach (XmlElement nodeEl in zoneEl.SelectNodes("node")!)
        {
            if (!int.TryParse(nodeEl.GetAttribute("id"), out int nodeId)) continue;
            if (!zone.Nodes.TryGetValue(nodeId, out var node)) continue;

            foreach (XmlElement arcEl in nodeEl.SelectNodes("arc")!)
            {
                var exitStr     = arcEl.GetAttribute("exit");
                var moveStr     = arcEl.GetAttribute("move");
                var destStr     = arcEl.GetAttribute("destination");
                var requiresStr = arcEl.GetAttribute("requires");
                // Phase 3 additions — optional in zone XML, fall back to null
                // when absent. Old Genie 4 client ignores unknown attributes,
                // preserving backwards compat for round-trip.
                var rtStr       = arcEl.GetAttribute("rt");
                var waitMinStr  = arcEl.GetAttribute("wait_min");
                var waitMaxStr  = arcEl.GetAttribute("wait_max");
                var envStr      = arcEl.GetAttribute("env");
                var notesStr    = arcEl.GetAttribute("notes");

                var dir = DirectionHelper.Parse(exitStr);

                int? destId = null;
                if (int.TryParse(destStr, out int parsedDest) && zone.Nodes.ContainsKey(parsedDest))
                    destId = parsedDest;

                node.Exits.Add(new MapExit
                {
                    Direction     = dir,
                    MoveCommand   = string.IsNullOrEmpty(moveStr) ? exitStr : moveStr,
                    DestinationId = destId,
                    Requires      = requiresStr?.Trim() ?? string.Empty,
                    RtCost        = int.TryParse(rtStr,      out var rt)      ? rt      : null,
                    WaitMin       = int.TryParse(waitMinStr, out var waitMin) ? waitMin : null,
                    WaitMax       = int.TryParse(waitMaxStr, out var waitMax) ? waitMax : null,
                    Environment   = envStr?.Trim() ?? string.Empty,
                    Notes         = notesStr?.Trim() ?? string.Empty,
                });
            }
        }

        return zone;
    }

    /// <summary>
    /// Imports all .xml files in a directory, returning one MapZone per file.
    /// </summary>
    public static IReadOnlyList<MapZone> ImportDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return [];

        var results = new List<MapZone>();
        foreach (var file in Directory.GetFiles(directory, "*.xml"))
        {
            try   { results.Add(Import(file)); }
            catch { /* skip malformed files */ }
        }
        return results;
    }

}
