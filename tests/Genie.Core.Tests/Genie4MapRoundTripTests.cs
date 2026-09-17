using System.Linq;
using System.Xml;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Round-trip fidelity for the Genie 4 zone format: import → export must not
/// change anything the format carries.
///
/// This matters more than an ordinary serializer test because
/// <c>MapsUpdater.ApplyAsync</c> re-serializes every downloaded zone through
/// this pair and writes the result over the user's Maps folder — the folder
/// the docs tell users to keep as a git clone and send PRs from. Any field the
/// importer drops is therefore not just missing in memory; it is erased from
/// disk on the next "Update Maps". There was no such test before, which is how
/// five separate losses shipped unnoticed:
/// <list type="bullet">
///   <item>go / climb arcs re-emitted as <c>exit="none"</c></item>
///   <item>off-grid and negative positions re-snapped by integer px ÷ 20</item>
///   <item>second and later <c>&lt;description&gt;</c> elements dropped</item>
///   <item><c>hidden</c> arcs and legacy <c>name</c> arcs stripped</item>
///   <item>dangling <c>destination</c> ids nulled out</item>
/// </list>
/// </summary>
public class Genie4MapRoundTripTests
{
    private static MapZone RoundTrip(string xml)
        => Genie4MapImporter.ImportFromContent(
               Genie4MapExporter.Serialize(
                   Genie4MapImporter.ImportFromContent(xml, "Zone")),
               "Zone");

    private static string Export(string xml)
        => Genie4MapExporter.Serialize(Genie4MapImporter.ImportFromContent(xml, "Zone"));

    // ── Arc exit tokens ──────────────────────────────────────────────────

    [Theory]
    [InlineData("go")]
    [InlineData("climb")]
    [InlineData("go branches")]   // multi-word — the enum cannot express it
    [InlineData("go moss")]
    [InlineData("none")]
    [InlineData("north")]
    [InlineData("southwest")]
    public void ExitToken_survives_the_round_trip(string token)
    {
        var xml = $"""
            <zone name="Z" id="1">
              <node id="1" name="A"><position x="0" y="0" z="0" />
                <arc exit="{token}" move="{token}" destination="2" /></node>
              <node id="2" name="B"><position x="20" y="0" z="0" /></node>
            </zone>
            """;

        Assert.Contains($"exit=\"{token}\"", Export(xml));
        Assert.Equal(token, RoundTrip(xml).Nodes[1].Exits[0].ExitToken);
    }

    [Fact]
    public void Go_and_climb_arcs_are_not_flattened_to_none()
    {
        var xml = """
            <zone name="Z" id="1">
              <node id="1" name="A"><position x="0" y="0" z="0" />
                <arc exit="go" move="go gate" destination="2" />
                <arc exit="climb" move="climb wall" destination="2" /></node>
              <node id="2" name="B"><position x="20" y="0" z="0" /></node>
            </zone>
            """;

        var exported = Export(xml);

        Assert.DoesNotContain("exit=\"none\"", exported);
        Assert.Contains("move=\"go gate\"",    exported);
        Assert.Contains("move=\"climb wall\"", exported);
    }

    // ── Positions ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(260, 100)]      // on-grid
    [InlineData(37, 93)]        // off-grid — 44.6% of the real corpus
    [InlineData(-30, -50)]      // negative AND off-grid; int division skewed these
    [InlineData(-620, 480)]
    [InlineData(19, -1)]
    public void Position_pixels_survive_the_round_trip(int x, int y)
    {
        var xml = $"""
            <zone name="Z" id="1">
              <node id="1" name="A"><position x="{x}" y="{y}" z="0" /></node>
            </zone>
            """;

        var node = RoundTrip(xml).Nodes[1];

        Assert.Equal(x, node.PixelX);
        Assert.Equal(y, node.PixelY);
        Assert.Contains($"x=\"{x}\" y=\"{y}\"", Export(xml));
    }

    [Fact]
    public void Grid_accessor_still_reports_whole_cells_and_is_symmetric()
    {
        var node = new MapNode { PixelX = 260, PixelY = -30 };

        Assert.Equal(13, node.X);           // 260 / 20
        Assert.Equal(-2, node.Y);           // -1.5 → -2, not truncated to -1

        node.X = 4;                          // a drag snaps pixels to the grid
        Assert.Equal(80, node.PixelX);
    }

    // ── Descriptions ─────────────────────────────────────────────────────

    [Fact]
    public void All_descriptions_survive_the_round_trip()
    {
        var xml = """
            <zone name="Z" id="1">
              <node id="1" name="A">
                <description>Summer growth crowds the path.</description>
                <description>Snow has buried the path.</description>
                <description>Ash covers everything.</description>
                <position x="0" y="0" z="0" /></node>
            </zone>
            """;

        var node = RoundTrip(xml).Nodes[1];

        Assert.Equal(3, node.Descriptions.Count);
        Assert.Equal("Summer growth crowds the path.", node.Description);
        Assert.Equal("Snow has buried the path.", node.Descriptions[1]);
        Assert.Equal("Ash covers everything.", node.Descriptions[2]);
    }

    [Fact]
    public void Empty_description_elements_survive_the_round_trip()
    {
        // 31 corpus nodes carry an empty <description/>; dropping it deleted
        // the element from the file on the next save.
        var xml = """
            <zone name="Z" id="1">
              <node id="1" name="A">
                <description></description>
                <description>Strangely-shaped trees rise here.</description>
                <position x="0" y="0" z="0" /></node>
            </zone>
            """;

        var node = RoundTrip(xml).Nodes[1];

        Assert.Equal(2, node.Descriptions.Count);
        Assert.Equal("", node.Descriptions[0]);
        Assert.Equal("Strangely-shaped trees rise here.", node.Descriptions[1]);
    }

    // ── Malformed-but-real input ─────────────────────────────────────────

    [Fact]
    public void Duplicate_node_ids_do_not_inflate_the_exit_list()
    {
        // Taisidon_Mystery.xml declares node 256 twice. The node's own
        // attributes take the last declaration; its arcs must come from that
        // same element, not from both — otherwise every save grows the room's
        // exits.
        var xml = """
            <zone name="Z" id="1">
              <node id="256" name="First"><position x="0" y="0" z="0" />
                <arc exit="north" move="north" /></node>
              <node id="256" name="Second"><position x="20" y="0" z="0" />
                <arc exit="south" move="south" />
                <arc exit="east" move="east" /></node>
            </zone>
            """;

        var node = RoundTrip(xml).Nodes[256];

        Assert.Equal("Second", node.Title);
        Assert.Equal(2, node.Exits.Count);
        Assert.DoesNotContain(node.Exits, e => e.ExitToken == "north");
    }

    [Fact]
    public void Empty_text_labels_survive_the_round_trip()
    {
        var xml = """
            <zone name="Z" id="1">
              <node id="1" name="A"><position x="0" y="0" z="0" /></node>
              <label text=""><position x="60" y="40" z="0" /></label>
              <label text="East Gate"><position x="110" y="-50" z="0" /></label>
            </zone>
            """;

        Assert.Equal(2, RoundTrip(xml).Labels.Count);
    }

    [Fact]
    public void Arc_with_no_exit_attribute_does_not_gain_one()
    {
        // 58 corpus arcs identify themselves by name= and carry no exit=.
        // Inventing exit="none" for them changed the file.
        var xml = """
            <zone name="Z" id="1">
              <node id="1" name="A"><position x="0" y="0" z="0" />
                <arc name="go trail" destination="2" /></node>
              <node id="2" name="B"><position x="20" y="0" z="0" /></node>
            </zone>
            """;

        var exported = Export(xml);

        Assert.DoesNotContain("exit=", exported);
        Assert.Contains("name=\"go trail\"", exported);
        Assert.Equal("go trail", RoundTrip(xml).Nodes[1].Exits[0].MoveCommand);
    }

    // ── Legacy arc attributes ────────────────────────────────────────────

    [Fact]
    public void Hidden_arcs_survive_the_round_trip()
    {
        var xml = """
            <zone name="Z" id="1">
              <node id="1" name="A"><position x="0" y="0" z="0" />
                <arc exit="north" move="north" hidden="1" destination="2" /></node>
              <node id="2" name="B"><position x="0" y="-20" z="0" /></node>
            </zone>
            """;

        Assert.Contains("hidden=\"1\"", Export(xml));
        Assert.Equal("1", RoundTrip(xml).Nodes[1].Exits[0].Hidden);
    }

    [Fact]
    public void Legacy_name_attribute_supplies_the_move_command_and_survives()
    {
        // Genie 4 falls back to name= when there is no move= (58 corpus arcs).
        var xml = """
            <zone name="Z" id="1">
              <node id="1" name="A"><position x="0" y="0" z="0" />
                <arc exit="go" name="go wooden door" destination="2" /></node>
              <node id="2" name="B"><position x="20" y="0" z="0" /></node>
            </zone>
            """;

        var exit = RoundTrip(xml).Nodes[1].Exits[0];

        Assert.Equal("go wooden door", exit.MoveCommand);
        Assert.Equal("go wooden door", exit.LegacyName);
        Assert.Contains("name=\"go wooden door\"", Export(xml));
    }

    [Fact]
    public void Dangling_destination_is_preserved_but_not_pathable()
    {
        var xml = """
            <zone name="Z" id="1">
              <node id="1" name="A"><position x="0" y="0" z="0" />
                <arc exit="north" move="north" destination="999" /></node>
            </zone>
            """;

        var exit = RoundTrip(xml).Nodes[1].Exits[0];

        Assert.Null(exit.DestinationId);            // pathfinder ignores it
        Assert.Equal("999", exit.RawDestination);   // but it is not erased
        Assert.Contains("destination=\"999\"", Export(xml));
    }

    // ── Genie 5 extensions still round-trip ──────────────────────────────

    [Fact]
    public void Genie5_extension_attributes_still_survive()
    {
        var xml = """
            <zone name="Z" id="1">
              <node id="1" name="A" note="Bank|bank" color="#FF00FF"
                    server_id="12345" tags="bank|moongate">
                <position x="37" y="-93" z="2" />
                <arc exit="go" move="go door" destination="2"
                     requires="climbing&gt;=50" rt="5" wait_min="10" wait_max="20"
                     env="Boat" notes="only at night" /></node>
              <node id="2" name="B"><position x="20" y="0" z="0" /></node>
              <label text="East Gate"><position x="110" y="-50" z="0" /></label>
            </zone>
            """;

        var zone = RoundTrip(xml);
        var node = zone.Nodes[1];
        var exit = node.Exits[0];

        Assert.Equal("Bank|bank",  node.Notes);
        Assert.Equal("#FF00FF",    node.Color);
        Assert.Equal("12345",      node.ServerRoomId);
        Assert.Contains("bank",    node.Tags);
        Assert.Equal(2,            node.Z);
        Assert.Equal("climbing>=50", exit.Requires);
        Assert.Equal(5,            exit.RtCost);
        Assert.Equal(10,           exit.WaitMin);
        Assert.Equal(20,           exit.WaitMax);
        Assert.Equal("Boat",       exit.Environment);
        Assert.Equal("only at night", exit.Notes);

        var label = Assert.Single(zone.Labels);
        Assert.Equal("East Gate", label.Text);
    }

    // ── The whole thing, twice ───────────────────────────────────────────

    [Fact]
    public void Export_is_idempotent_across_a_second_round_trip()
    {
        // The updater re-serializes on every run, so drift must not compound.
        var xml = """
            <zone name="Riverhaven" id="30">
              <node id="1" name="Town Square" note="square">
                <description>A wide square.</description>
                <description>Lanterns light the square.</description>
                <position x="-37" y="93" z="0" />
                <arc exit="go" move="go inn" hidden="1" destination="2" />
                <arc exit="climb" move="climb trellis" destination="3" />
                <arc exit="north" move="north" destination="404" /></node>
              <node id="2" name="Inn"><position x="-17" y="93" z="0" /></node>
              <node id="3" name="Roof"><position x="-37" y="73" z="1" /></node>
              <label text="Riverhaven"><position x="-27" y="83" z="0" /></label>
            </zone>
            """;

        var once  = Export(xml);
        var twice = Genie4MapExporter.Serialize(
                        Genie4MapImporter.ImportFromContent(once, "Zone"));

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Round_trip_preserves_every_arc_and_position_of_a_realistic_zone()
    {
        var xml = """
            <zone name="Crossing" id="1">
              <node id="1" name="A"><position x="-620" y="480" z="0" />
                <arc exit="go branches" move="go branches" destination="2" />
                <arc exit="none" move="swim river" destination="3" /></node>
              <node id="2" name="B"><position x="37" y="-93" z="0" />
                <arc exit="southwest" move="sw" destination="1" /></node>
              <node id="3" name="C"><position x="0" y="19" z="-1" /></node>
            </zone>
            """;

        var before = Genie4MapImporter.ImportFromContent(xml, "Zone");
        var after  = RoundTrip(xml);

        Assert.Equal(before.Nodes.Count, after.Nodes.Count);
        foreach (var (id, b) in before.Nodes)
        {
            var a = after.Nodes[id];
            Assert.Equal(b.PixelX, a.PixelX);
            Assert.Equal(b.PixelY, a.PixelY);
            Assert.Equal(b.Z,      a.Z);
            Assert.Equal(b.Exits.Count, a.Exits.Count);
            for (int i = 0; i < b.Exits.Count; i++)
            {
                Assert.Equal(b.Exits[i].ExitToken,   a.Exits[i].ExitToken);
                Assert.Equal(b.Exits[i].MoveCommand, a.Exits[i].MoveCommand);
                Assert.Equal(b.Exits[i].DestinationId, a.Exits[i].DestinationId);
            }
        }
    }

    /// <summary>
    /// Guards the exporter's own promise that its output is loadable XML —
    /// a malformed attribute would otherwise only surface at the user's next
    /// map load, after the good file had already been overwritten.
    /// </summary>
    [Fact]
    public void Exported_xml_is_well_formed()
    {
        var xml = """
            <zone name="Z &amp; Co" id="1">
              <node id="1" name="A &amp; B"><position x="0" y="0" z="0" />
                <arc exit="go" move="go &quot;odd&quot; door" /></node>
            </zone>
            """;

        var doc = new XmlDocument();
        doc.LoadXml(Export(xml));   // throws if the writer emitted bad XML

        Assert.Equal("zone", doc.DocumentElement!.Name);
    }
}
