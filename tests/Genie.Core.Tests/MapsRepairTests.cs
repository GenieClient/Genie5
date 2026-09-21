using System;
using System.IO;
using System.Linq;
using Genie.Core.Mapper;
using Genie.Core.Update.Updaters;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #352 — maps damaged by pre-<c>d883728</c> updates are never repaired
/// by a normal Update Maps.
///
/// <para>The lossy round-trip preserved each arc's move text but dropped its
/// exit TYPE and hidden flag, and dropped descriptions entirely. The damage is
/// invisible from inside Genie: the zone still draws and travel mostly works,
/// because the move text survived. And an ordinary update can never fix it,
/// because the updater skips zones whose upstream SHA is unchanged — so a
/// damaged file that is current upstream is skipped forever.</para>
///
/// <para>Two pieces make the repair possible, and both are covered here:
/// recognising the damage (so the repair can report what it found instead of
/// asking for a full re-download on faith), and forgetting the installed
/// revisions (so the next apply rewrites every zone rather than one).</para>
/// </summary>
public class MapsRepairTests : IDisposable
{
    private readonly string _dir;

    public MapsRepairTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_maprepair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private MapsUpdater NewUpdater() =>
        new(new MapZoneRepository(), _dir, Array.Empty<Genie.Core.Update.Sources.IFileListSource>());

    private void WriteZone(string name, string arcsXml) =>
        File.WriteAllText(Path.Combine(_dir, name),
            $"<zone><node id=\"1\" name=\"A\">{arcsXml}</node></zone>");

    private string ManifestPath => Path.Combine(_dir, ".map-shas.json");

    // ── Recognising the damage ───────────────────────────────────────────────

    /// <summary>The signature: a portal move with no exit type. That
    /// combination is always wrong, so the detection needs no heuristics.</summary>
    [Fact]
    public void A_portal_move_with_no_exit_type_is_counted_as_damaged()
    {
        WriteZone("Map1.xml",
            "<arc exit=\"none\" move=\"go wide arch\" destination=\"2\" />" +
            "<arc exit=\"none\" move=\"climb steep slope\" destination=\"3\" />");

        Assert.Equal(2, NewUpdater().CountDamagedArcs());
    }

    /// <summary>A healthy folder must count zero, or the repair would report
    /// damage on maps that have none and train users to ignore it.</summary>
    [Fact]
    public void A_healthy_zone_counts_nothing()
    {
        WriteZone("Map1.xml",
            "<arc exit=\"go\" move=\"go wide arch\" hidden=\"True\" destination=\"2\" />" +
            "<arc exit=\"climb\" move=\"climb steep slope\" destination=\"3\" />" +
            "<arc exit=\"north\" move=\"north\" destination=\"4\" />");

        Assert.Equal(0, NewUpdater().CountDamagedArcs());
    }

    /// <summary>An ordinary compass arc with exit="none" is not damage — only
    /// the portal/climb move texts are, so the counter must not fire on it.</summary>
    [Fact]
    public void A_plain_move_with_no_exit_type_is_not_damage()
    {
        WriteZone("Map1.xml", "<arc exit=\"none\" move=\"out\" destination=\"2\" />");

        Assert.Equal(0, NewUpdater().CountDamagedArcs());
    }

    /// <summary>A zone that will not parse is not evidence either way, and the
    /// repair is safe regardless — so the scan skips it rather than aborting
    /// and leaving the rest of the folder uncounted.</summary>
    [Fact]
    public void An_unparseable_zone_does_not_abort_the_scan()
    {
        File.WriteAllText(Path.Combine(_dir, "Broken.xml"), "<zone><node");
        WriteZone("Map2.xml", "<arc exit=\"none\" move=\"go wide arch\" destination=\"2\" />");

        Assert.Equal(1, NewUpdater().CountDamagedArcs());
    }

    // ── Forgetting the installed revisions ───────────────────────────────────

    /// <summary>The manifest is what makes a damaged-but-current zone
    /// untouchable; clearing it is the whole repair.</summary>
    [Fact]
    public void Forgetting_installed_revisions_removes_the_manifest()
    {
        File.WriteAllText(ManifestPath, "{\"Map1.xml\":\"abc123\"}");

        Assert.True(NewUpdater().ForgetInstalledRevisions());
        Assert.False(File.Exists(ManifestPath));
    }

    /// <summary>No manifest is not a failure — a fresh install has none, and a
    /// repair there is simply a full download.</summary>
    [Fact]
    public void Forgetting_with_no_manifest_reports_nothing_to_clear()
    {
        Assert.False(NewUpdater().ForgetInstalledRevisions());
    }
}
