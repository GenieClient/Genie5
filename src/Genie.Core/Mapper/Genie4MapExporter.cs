using System.Text;
using System.Xml;

namespace Genie.Core.Mapper;

/// <summary>
/// Writes a <see cref="MapZone"/> as Genie 4 compatible XML. The output matches
/// the schema consumed by <see cref="Genie4MapImporter"/> so round-tripping a
/// zone through Genie 5 does not break compatibility with Genie 4, Lich, or
/// the official <c>GenieClient/Maps</c> repository.
///
/// Format notes:
/// <list type="bullet">
///   <item>UTF-8 without BOM, LF line endings, 2-space indent — matches the
///         conventions in the upstream repo so <c>git diff</c> output stays
///         minimal across forks and platforms.</item>
///   <item>Position values are written as the pixel coordinates the zone was
///         imported with. Genie 4 stores pixels and they are not all on the
///         20px grid, so we keep them verbatim rather than recomputing them
///         from the internal grid units.</item>
///   <item>Nodes are emitted in ascending node-id order so two clients walking
///         the same zone produce identical files.</item>
///   <item>The <c>server_id</c> attribute is a Genie 5 extension carrying the
///         server's room id from <c>&lt;nav rm="..."/&gt;</c>. Old clients
///         ignore it; new clients use it for instant room matching. Including
///         it in PRs lets the community accumulate the server-id → node-id
///         mapping over time.</item>
/// </list>
/// </summary>
public static class Genie4MapExporter
{
    /// <summary>Serialize a zone to a file at <paramref name="xmlPath"/>.</summary>
    public static void Export(MapZone zone, string xmlPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
        File.WriteAllText(xmlPath, Serialize(zone), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Serialize a zone to a string. Useful for tests and previews.</summary>
    public static string Serialize(MapZone zone)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent             = true,
            IndentChars        = "  ",
            NewLineChars       = "\n",
            NewLineHandling    = NewLineHandling.Replace,
            OmitXmlDeclaration = false,
            Encoding           = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        using (var writer = XmlWriter.Create(sb, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("zone");
            writer.WriteAttributeString("name", zone.Name);
            if (!string.IsNullOrEmpty(zone.Genie4Id))
                writer.WriteAttributeString("id", zone.Genie4Id);

            // Sort by node id so two clients walking the same zone produce
            // byte-identical output — critical for git-friendly diffs.
            foreach (var nodeKv in zone.Nodes.OrderBy(kv => kv.Key))
                WriteNode(writer, nodeKv.Value);

            // Free-floating <label> elements after the nodes (matches Genie 4's
            // file layout). Written verbatim so a round-trip preserves a map's
            // landmark labels instead of destroying them.
            foreach (var label in zone.Labels)
                WriteLabel(writer, label);

            writer.WriteEndElement();   // zone
            writer.WriteEndDocument();
        }

        return sb.ToString();
    }

    private static void WriteNode(XmlWriter writer, MapNode node)
    {
        writer.WriteStartElement("node");
        // Attribute order matches the importer's expectations and Genie 4's
        // hand-edited convention: id, name, note, color, server_id.
        writer.WriteAttributeString("id",   node.Id.ToString());
        writer.WriteAttributeString("name", node.Title);
        if (!string.IsNullOrEmpty(node.Notes))        writer.WriteAttributeString("note",      node.Notes);
        if (!string.IsNullOrEmpty(node.Color))        writer.WriteAttributeString("color",     node.Color);
        if (!string.IsNullOrEmpty(node.ServerRoomId)) writer.WriteAttributeString("server_id", node.ServerRoomId);
        // tags — Genie 5 extension, '|'-separated. Sorted (case-insensitive) so
        // two clients emit byte-identical output for git-friendly diffs. Omitted
        // when empty so unchanged upstream maps produce no diff.
        if (node.Tags.Count > 0)
        {
            var tags = string.Join('|', node.Tags.Select(t => t.Trim())
                                                  .Where(t => t.Length > 0)
                                                  .OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
            if (tags.Length > 0) writer.WriteAttributeString("tags", tags);
        }

        // Every description, in file order — a room may legitimately carry
        // several (seasonal / day-night variants) and Genie 4 matches against
        // all of them. Writing only the first erased the rest from disk.
        foreach (var desc in node.Descriptions)
        {
            writer.WriteStartElement("description");
            writer.WriteString(desc);
            writer.WriteEndElement();
        }

        // Position — the authoritative pixel values, verbatim. Recomputing
        // these as X*20 re-snapped every off-grid room to the 20px grid (44.6%
        // of the corpus) and skewed negative coordinates. See MapNode.PixelX.
        writer.WriteStartElement("position");
        writer.WriteAttributeString("x", node.PixelX.ToString());
        writer.WriteAttributeString("y", node.PixelY.ToString());
        writer.WriteAttributeString("z", node.Z.ToString());
        writer.WriteEndElement();

        // Arcs: write in declared order so consecutive walks of the same room
        // produce identical files. We preserve the move command verbatim when
        // present — it may contain shop/climb/jump commands distinct from the
        // bare exit name.
        foreach (var exit in node.Exits)
        {
            writer.WriteStartElement("arc");
            // The raw token wins over the parsed Direction. Direction has no
            // Go/Climb member, so writing Direction.ToString() turned 22.0% of
            // community arcs into exit="none" and destroyed multi-word tokens
            // like "go branches". See MapExit.ExitToken.
            // An arc with no exit token at all is legal Genie 4 (58 corpus
            // arcs identify themselves by name= instead). Emit no exit
            // attribute for those rather than inventing exit="none".
            var exitToken = !string.IsNullOrEmpty(exit.ExitToken) ? exit.ExitToken
                          : exit.Direction != Direction.None
                                ? exit.Direction.ToString().ToLowerInvariant()
                                : string.Empty;
            if (!string.IsNullOrEmpty(exitToken))
                writer.WriteAttributeString("exit", exitToken);
            // Genie 4's legacy movement attribute, kept where the source used
            // it so those arcs keep their command.
            if (!string.IsNullOrEmpty(exit.LegacyName))
                writer.WriteAttributeString("name", exit.LegacyName);
            writer.WriteAttributeString("move",
                string.IsNullOrEmpty(exit.MoveCommand) ? exitToken : exit.MoveCommand);
            if (!string.IsNullOrEmpty(exit.Hidden))
                writer.WriteAttributeString("hidden", exit.Hidden);
            if (exit.DestinationId.HasValue)
                writer.WriteAttributeString("destination", exit.DestinationId.Value.ToString());
            else if (!string.IsNullOrEmpty(exit.RawDestination))
                writer.WriteAttributeString("destination", exit.RawDestination);
            // Genie 5 extensions. Omitted when null/empty so PR diffs
            // against unchanged upstream maps stay minimal. Old Genie 4
            // clients silently ignore unknown attributes — backwards
            // compatible round-trip.
            if (!string.IsNullOrEmpty(exit.Requires))
                writer.WriteAttributeString("requires", exit.Requires);
            if (exit.RtCost.HasValue)
                writer.WriteAttributeString("rt", exit.RtCost.Value.ToString());
            if (exit.WaitMin.HasValue)
                writer.WriteAttributeString("wait_min", exit.WaitMin.Value.ToString());
            if (exit.WaitMax.HasValue)
                writer.WriteAttributeString("wait_max", exit.WaitMax.Value.ToString());
            if (!string.IsNullOrEmpty(exit.Environment))
                writer.WriteAttributeString("env", exit.Environment);
            if (!string.IsNullOrEmpty(exit.Notes))
                writer.WriteAttributeString("notes", exit.Notes);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();   // node
    }

    private static void WriteLabel(XmlWriter writer, MapLabel label)
    {
        writer.WriteStartElement("label");
        writer.WriteAttributeString("text", label.Text);
        // Position — scale fractional grid units back to integer pixels
        // (importer divides by 20.0; round so a clean round-trip is exact).
        writer.WriteStartElement("position");
        writer.WriteAttributeString("x", ((int)Math.Round(label.X * 20)).ToString());
        writer.WriteAttributeString("y", ((int)Math.Round(label.Y * 20)).ToString());
        writer.WriteAttributeString("z", label.Z.ToString());
        writer.WriteEndElement();   // position
        writer.WriteEndElement();   // label
    }
}
