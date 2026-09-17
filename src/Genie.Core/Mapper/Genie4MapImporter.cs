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

        // A zone file may repeat a node id (Taisidon_Mystery.xml declares 256
        // twice). Pass 1 lets the last declaration win for the node's own
        // attributes; pass 2 must then read arcs from that SAME element, or it
        // appends both elements' arcs onto one node and inflates the room's
        // exits on every save. Keyed here so both passes agree on the winner.
        var nodeElementById = new Dictionary<int, XmlElement>();

        // ── Pass 1: build all nodes (preserve Genie4 integer IDs) ────────────
        foreach (XmlElement nodeEl in zoneEl.SelectNodes("node")!)
        {
            if (!int.TryParse(nodeEl.GetAttribute("id"), out int nodeId)) continue;
            nodeElementById[nodeId] = nodeEl;

            var node = new MapNode
            {
                Id    = nodeId,
                Title = nodeEl.GetAttribute("name"),
            };

            // Descriptions — a node may carry several (seasonal / day-night
            // variants), and some are empty. All of them are kept in file
            // order so export can write them back. Taking only the first
            // silently destroyed 4,731 elements across 3,963 nodes in the
            // community corpus on the next save.
            foreach (XmlElement descEl in nodeEl.SelectNodes("description")!)
                node.Descriptions.Add(descEl.InnerText.Trim());

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
                // Keep the pixels verbatim; MapNode.X/Y derive the grid cell.
                // The old `X = px / 20` moved 44.6% of corpus rooms (they are
                // not all on the 20px grid) and truncated the 10,900 negative
                // coordinates toward zero. See MapNode.PixelX.
                node.PixelX = px;
                node.PixelY = py;
                node.Z      = pz;
            }

            zone.Nodes[nodeId] = node;
        }

        // ── Pass 2: resolve arcs ─────────────────────────────────────────────
        foreach (var (nodeId, nodeEl) in nodeElementById)
        {
            if (!zone.Nodes.TryGetValue(nodeId, out var node)) continue;

            foreach (XmlElement arcEl in nodeEl.SelectNodes("arc")!)
            {
                var exitStr     = arcEl.GetAttribute("exit");
                var moveStr     = arcEl.GetAttribute("move");
                var destStr     = arcEl.GetAttribute("destination");
                var requiresStr = arcEl.GetAttribute("requires");
                // Genie 4 legacy attributes. `name` is the pre-`move` spelling
                // of the movement command (58 corpus arcs still use it, and
                // Genie 4 falls back to it); `hidden` marks an undrawn arc.
                var nameStr     = arcEl.GetAttribute("name");
                var hiddenStr   = arcEl.GetAttribute("hidden");
                // Phase 3 additions — optional in zone XML, fall back to null
                // when absent. Old Genie 4 client ignores unknown attributes,
                // preserving backwards compat for round-trip.
                var rtStr       = arcEl.GetAttribute("rt");
                var waitMinStr  = arcEl.GetAttribute("wait_min");
                var waitMaxStr  = arcEl.GetAttribute("wait_max");
                var envStr      = arcEl.GetAttribute("env");
                var notesStr    = arcEl.GetAttribute("notes");

                var dir = DirectionHelper.Parse(exitStr);

                // A dangling destination (id not in this zone) stays out of
                // DestinationId so the pathfinder ignores it, but the raw text
                // is kept so export doesn't erase what Genie 4 preserves.
                int? destId = null;
                string rawDest = string.Empty;
                if (int.TryParse(destStr, out int parsedDest))
                {
                    if (zone.Nodes.ContainsKey(parsedDest)) destId  = parsedDest;
                    else                                    rawDest = destStr.Trim();
                }

                // Movement command precedence mirrors Genie 4: move, then the
                // legacy name attribute, then the bare exit token.
                var move = !string.IsNullOrEmpty(moveStr) ? moveStr
                         : !string.IsNullOrEmpty(nameStr) ? nameStr
                         : exitStr;

                node.Exits.Add(new MapExit
                {
                    Direction      = dir,
                    ExitToken      = exitStr,
                    LegacyName     = nameStr?.Trim()   ?? string.Empty,
                    Hidden         = hiddenStr?.Trim() ?? string.Empty,
                    RawDestination = rawDest,
                    MoveCommand    = move,
                    DestinationId  = destId,
                    Requires       = requiresStr?.Trim() ?? string.Empty,
                    RtCost         = int.TryParse(rtStr,      out var rt)      ? rt      : null,
                    WaitMin        = int.TryParse(waitMinStr, out var waitMin) ? waitMin : null,
                    WaitMax        = int.TryParse(waitMaxStr, out var waitMax) ? waitMax : null,
                    Environment    = envStr?.Trim() ?? string.Empty,
                    Notes          = notesStr?.Trim() ?? string.Empty,
                });
            }
        }

        // ── Pass 3: free-floating <label> elements ───────────────────────────
        // Genie 4 stores landmark text ("East Gate", "Guard House") as top-level
        // <label text="..."><position x y z/></label> siblings of <node>, separate
        // from node notes. The map canvas renders these as the on-map text. We
        // must import AND export them (the exporter writes them back) so a
        // round-trip through Genie 5 doesn't silently destroy a map's labels.
        foreach (XmlElement labelEl in zoneEl.SelectNodes("label")!)
        {
            // Empty-text labels are kept too: six exist in the community
            // corpus, and skipping them deleted the element from the file on
            // the next save.
            var label = new MapLabel { Text = labelEl.GetAttribute("text").Trim() };
            if (labelEl.SelectSingleNode("position") is XmlElement lpos)
            {
                int.TryParse(lpos.GetAttribute("x"), out int lx);
                int.TryParse(lpos.GetAttribute("y"), out int ly);
                int.TryParse(lpos.GetAttribute("z"), out int lz);
                // Same pixel→grid scale as nodes, but NOT truncated: labels are
                // free-floating (not snapped to the 20px node grid), so integer
                // division would stack them onto the nearest room cell.
                label.X = lx / 20.0;
                label.Y = ly / 20.0;
                label.Z = lz;
            }
            zone.Labels.Add(label);
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
