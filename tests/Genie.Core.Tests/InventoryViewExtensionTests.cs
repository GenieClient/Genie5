using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Genie.Core.Events;
using Genie.Core.Extensions;
using Genie.Core.Extensions.Builtin.InventoryView;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// The built-in Inventory View catalog (/iv scan). The centerpiece fixture is
/// the REAL merged <c>INV LIST</c> line from a live 2026-07-05 session — DR
/// concatenates the whole inventory onto one raw line (items keep their old
/// leading indent: 2 spaces = level 1, 5 + dash = 2, 8 + dash = 3), which the
/// extension re-splits into the item tree. Also covers the classic
/// one-item-per-line format, load-time repair of pre-fix merged catalogs, the
/// vault/trader indent schemes, and the /iv command surface.
/// </summary>
public class InventoryViewExtensionTests
{
    // The verbatim game line (leading two spaces significant), captured live.
    private const string LiveMergedLine =
"""
  a large canvas sack     -a sunstone runestone     -a razor-edged scimitar  a black gem pouch  a red gem pouch (closed)  a kyanite gwethdesuan  a brushed platinum and gemstone ring bearing the crest of the Moon Mage Guild  an oak staff topped by a coiled dragon clutching a crystal ball  a glob of translucent slime  a frayed straw hat banded with tattered brown ribbon  an electrum skull and crossbones nosestud  a deeply-cowled black samite mantle with a tenebrous crimson velvet lining  a pair of platinum cufflinks inlaid with golden coins  a pair of golden spectacles bearing Fantasy Excursions' emblem  a tower-shaped moonstone engraved with the Moon Mage Guild crest  a small white crystal strung on a thin leather strap  a petite hedgehog wearing a vermilion knit hat  an etched steel parry stick with black leather straps  an elaborate samite harness sewn in copper and gold thread with autumnal maple leaves  a black spidersilk robe stenciled with a tower under a starry sky     -a small leather pouch with ornate detail        -a shapeless grey blob comprised of shifting shadows     -a small gift bag        -a colorful bamboo dye tub emblazoned with a dark dragon        -some sparkling twilight purple dye        -some rich chocolate dye        -some metallic pink dye        -some shadowy black dye        -some smokey white dye     -a small raffle token     -a large piece of ivory     -a crafter's bag dyed the colors of sunset     -a large faceted orange-red morganite     -a tiny purple sapphire     -a huge pink pearl     -a white pearl     -a large flawless diamond     -a tiny scarlet ruby     -a white pearl     -a brilliant icy blue crystal     -a tiny mint green pearl     -a filigreed pair of rencate and acenite dustanjis earcuffs     -a glittering invitation     -a tiny peach pearl     -a potency crystal     -a small pink pearl     -a tawny clay bottle molded into the shape of a cloaked wizard (closed)     -some glossy e'erdream worry beads dangling from a sana'ati heartwood fob     -a flagon of orangeberry liqueur     -a platinum ring shaped like a coiled snake holding a cambrinth orb in its mouth     -a crimson glass ring     -a rot-brown glass ring     -a bottle of Guardsman's Black Label ale     -some small golden book-shaped earrings     -a small black pearl     -a heavy steel wristcuff comprised of interlocking gears     -a potency crystal     -a milk chocolate dragon with currant eyes     -a pair of glistening flame opal chandelier earrings     -a water-filled crystal sphere     -a water-filled crystal sphere     -a little black book embossed with a tiny rose in the corner     -a pair of glistening flame opal chandelier earrings     -an amethyst signet ring etched with a constellation (closed)     -an agate signet ring with a stylized gladiolus     -a water-filled crystal sphere     -a parchment scroll     -a cushion-cut rainbow sapphire     -a large freshwater pearl     -a large light-purple alexandrite     -a cabochon rainbow sapphire     -a huge scratched emerald     -a huge scratched emerald     -an iron fragment     -a brass compass with a cracked glass covering     -a tiny flax-colored pearl     -a platinum ring shaped like a coiled snake holding a cambrinth orb in its mouth     -a sanowret crystal     -a soft woven sack with a golden piglet charm     -a simple parchment     -a tiny chartreuse sapphire     -a drake's heart amber     -a huge cerulean sapphire     -a drake's heart amber     -a dainty acenite rat pin with its mouth gaping in a yawn     -a scarlet emerald     -a tiny smoky grey diamond     -a viperscale alexandrite     -an awl with a sturdy osage handle     -a Dalaeji black sapphire     -a tiny purple sapphire     -a Gemfire ruby     -a master's iron lockpick     -a diamondique lockpick ring  a braided gold wristband  a curved skinning blade with an antler hilt  a heavy silver bracelet engraved with playful shadowlings  a gold and silver leather badge stitched with the words "Proud Member of the AGA!"  a ghostly white jackal  a coarse burlap sack (closed)     -a flea-ridden river rat  a rusty target shield with tattered leather bindings  a silver cambrinth armband in the shape of a sinuous adder  a platinum diamond-shaped amulet with a faceted ruby in the center  a dark silver spellbook case inset with a circle of milky blue moonstone (closed)     -a ripped black silk spellbook  a supple leather telescope case     -a flamewood telescope with platinum bands  a snow white silk thigh bag with sapphire trim and a crescent moon made from tiny blue crystals  a lacquered writer's box tinted night-black (closed)     -a textured sheet of paper stamped with the image of three moons     -a textured sheet of paper stamped with the image of three moons     -a textured sheet of paper stamped with the image of three moons     -a textured sheet of paper stamped with the image of three moons     -a textured sheet of paper stamped with the image of three moons     -a textured sheet of paper stamped with the image of three moons     -a textured sheet of paper stamped with the image of three moons     -a textured sheet of paper stamped with the image of three moons  some steelsilk footwraps  some grey cotton socks stained black along the bottom  a frayed straw hat banded with tattered brown ribbon  a sapphire prism suspended from a rainbow painted chain  a leather backsheath studded with cambrinth discs and wrapped with a coiling dragon     -a sapphire-tinted gladius with a small celpeze-shaped pommel     -a carved oaken parry stick shaped like a scrawny rat     -a patinated bronze club set with a multitude of shattered gemstones     -a shucking knife with mother-of-pearl inlay upon the handle     -a shucking knife with mother-of-pearl inlay upon the handle     -a carving knife     -a thin steel dagger etched with the insignia of the Moon Mage Guild     -a blackened steel katar shaped like a raven's head     -some steelsilk handwraps     -a silken cord strung with uneven bits of coral     -a patinated bronze club set with a multitude of shattered gemstones     -some blowgun darts     -a carved oaken parry stick shaped like a scrawny rat     -a patinated bronze club set with a multitude of shattered gemstones     -a patinated bronze club set with a multitude of shattered gemstones     -a carved oaken parry stick shaped like a scrawny rat     -a badly gouged steel gladius with a reticulated mistwood grip     -a badly gouged steel gladius with a reticulated mistwood grip     -a badly gouged steel gladius with a reticulated mistwood grip     -an origami primer (closed)     -an engraved steel round axe     -a tattered rag  a cambrinth shadowling charm suspended from a black satin cord  a sturdy leather thigh pouch elaborately tooled with the image of a fire-breathing dragon     -a leather-bound book embossed with the title "Twisted Tales" in ornate gold script (closed)        -a twisted gold key     -a weathered textbook with a tattered cover     -an acanth bank book with openwork carvings     -a weathered textbook with a tattered cover     -a book bound in black leather titled "Battle of the Reshal Sea" (closed)        -a little key  a darkened leather hip pouch branded with a rolled scroll  a dainty acenite rat pin with its teeth bared in a vicious snarl  a delicate silver anklet with dangling cambrinth raspberries  an onyx shadowling pin  a blued steel chain hauberk worked with a wave pattern  a rusty chain balaclava lined with stained leather  a silver eyebrow ring accented with three tiny moon-colored gems  a platinum ring engraved with the Estate Holder's crest[Use INVENTORY HELP for more options.]
""";

    private sealed class FakeHost : IExtensionHost
    {
        public readonly ConcurrentDictionary<string, string> Vars = new();
        public readonly List<string> Echoed = new();
        public readonly List<string> Sent = new();
        public readonly List<string> Injected = new();
        public readonly List<string> HashCmds = new();
        public IDictionary<string, string> Globals => Vars;
        public string ConfigDir { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "genie-iv-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Echo(string text) => Echoed.Add(text);
        public void SendCommand(string command) => Sent.Add(command);
        public void SetWindow(string window, string content) { }
        public void Log(string message) { }
        public void InjectParsedLine(string line) => Injected.Add(line);
        public void RunHashCommand(string command) => HashCmds.Add(command);
    }

    private static FakeHost NewHost()
    {
        var host = new FakeHost();
        host.Vars["connected"]     = "1";
        host.Vars["charactername"] = "Renucci";
        host.Vars["guild"]         = "Moon Mage";
        return host;
    }

    private static void Feed(InventoryViewExtension ext, string line) =>
        ext.OnGameEvent(new TextEvent("main", line));

    /// <summary>Run a full scan: inventory (the given lines), vault/deed absent,
    /// a one-furniture home, prompt to finish.</summary>
    private static void RunScan(InventoryViewExtension ext, params string[] invLines)
    {
        ext.OnSlashCommand("/iv scan");
        Feed(ext, "You have:");
        foreach (var l in invLines) Feed(ext, l);
        Feed(ext, "Roundtime: 1 sec.");
        Feed(ext, "What were you referring to?");   // no vault book
        Feed(ext, "What were you referring to?");   // no deed register
        Feed(ext, "The home contains:");
        Feed(ext, "Floor: a dusty oak floor.");
        Feed(ext, "  Attached: a tasseled cotton rug.");
        ext.OnPrompt();                              // end of home → complete
    }

    private static ItemData? Find(IEnumerable<CharacterData> catalog, string tapStart)
    {
        foreach (var c in catalog)
            foreach (var item in Walk(c.Items))
                if (item.Tap.StartsWith(tapStart, StringComparison.Ordinal))
                    return item;
        return null;
    }

    private static IEnumerable<ItemData> Walk(IEnumerable<ItemData> items)
    {
        foreach (var i in items)
        {
            yield return i;
            foreach (var c in Walk(i.Items)) yield return c;
        }
    }

    // ── The real merged live line ───────────────────────────────────────────────

    [Fact]
    public void Merged_live_line_rebuilds_the_item_tree()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);
        RunScan(ext, LiveMergedLine);

        Assert.Contains(host.Echoed, e => e.Contains("Scan Complete"));
        Assert.Contains("InventoryView scan complete", host.Injected);

        var catalog = ext.SnapshotCatalog();
        var inv = catalog.Single(c => c.Source == "Inventory");
        Assert.Equal("Renucci", inv.Name);
        Assert.Equal(46, inv.Items.Count);   // level-1 items in the recording

        // Level 2: the canvas sack holds exactly the two items that followed it.
        var sack = inv.Items.Single(i => i.Tap == "a large canvas sack");
        Assert.Equal(new[] { "a sunstone runestone", "a razor-edged scimitar" },
                     sack.Items.Select(i => i.Tap).ToArray());

        // Level 3: the gift bag (inside the spidersilk robe) holds 6 dye items.
        var robe = inv.Items.Single(i => i.Tap.StartsWith("a black spidersilk robe"));
        var bag  = robe.Items.Single(i => i.Tap == "a small gift bag");
        Assert.Equal(6, bag.Items.Count);
        Assert.StartsWith("a colorful bamboo dye tub", bag.Items[0].Tap);

        // Level 3 via search: full nested path survives.
        var paths = ext.FindMatches("twisted gold key");
        var path  = Assert.Single(paths);
        Assert.Equal(
            "Renucci > Inventory > a sturdy leather thigh pouch elaborately tooled with the image of a fire-breathing dragon" +
            " > a leather-bound book embossed with the title \"Twisted Tales\" in ornate gold script (closed)" +
            " > a twisted gold key",
            path);

        // The glued "[Use INVENTORY HELP …]" hint is stripped, and nothing merged.
        Assert.Equal("a platinum ring engraved with the Estate Holder's crest", inv.Items[^1].Tap);
        Assert.DoesNotContain(Walk(inv.Items), i => i.Tap.Contains("[Use") || i.Tap.Contains("  "));

        // Home parsed alongside.
        var home = catalog.Single(c => c.Source == "Home");
        Assert.Equal("a dusty oak floor.", home.Items[0].Tap);
        Assert.Equal("a tasseled cotton rug.", home.Items[0].Items.Single().Tap);
    }

    [Fact]
    public void Classic_one_item_per_line_format_still_parses()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);
        RunScan(ext,
            "  a backpack",
            "     -a pouch",
            "        -a gem",
            "  a sword");

        var inv = ext.SnapshotCatalog().Single(c => c.Source == "Inventory");
        Assert.Equal(new[] { "a backpack", "a sword" }, inv.Items.Select(i => i.Tap).ToArray());
        var pouch = Assert.Single(inv.Items[0].Items);
        Assert.Equal("a pouch", pouch.Tap);
        Assert.Equal("a gem", Assert.Single(pouch.Items).Tap);
    }

    // ── Load-time repair of pre-fix merged catalogs ─────────────────────────────

    [Fact]
    public void Garbled_saved_catalog_repairs_to_the_fresh_scan_tree()
    {
        // Reference: a fresh scan of the live line.
        var freshHost = NewHost();
        var fresh = new InventoryViewExtension();
        fresh.Initialize(freshHost);
        RunScan(fresh, LiveMergedLine);
        var expected = fresh.FindMatches("");        // every path, in order

        // A pre-fix save: the whole merged line stored as ONE tap (trimmed, with
        // the HELP hint still glued on — the fixture line ends with it), exactly
        // what the old plugin wrote.
        var host = NewHost();
        var file = Path.Combine(host.ConfigDir, "InventoryView.xml");
        var garbledTap = LiveMergedLine.Trim();
        File.WriteAllText(file,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><ArrayOfCharacterData><CharacterData>" +
            "<name>Renucci</name><source>Inventory</source><items><ItemData>" +
            "<storage>false</storage><tap>" + System.Security.SecurityElement.Escape(garbledTap) +
            "</tap><items /></ItemData></items></CharacterData></ArrayOfCharacterData>");

        var ext = new InventoryViewExtension();
        ext.Initialize(host);                        // load runs the repair

        Assert.Contains(host.Echoed, e => e.Contains("repaired 1 merged entry"));
        var repairedInv = ext.SnapshotCatalog().Single(c => c.Source == "Inventory");
        Assert.Equal(46, repairedInv.Items.Count);
        var freshInv = fresh.SnapshotCatalog().Single(c => c.Source == "Inventory");
        Assert.Equal(
            Walk(freshInv.Items).Select(i => i.Tap),
            Walk(repairedInv.Items).Select(i => i.Tap));

        // Idempotent: a second load of the repaired file stays quiet.
        var host2 = new FakeHost();
        foreach (var kv in host.Vars) host2.Vars[kv.Key] = kv.Value;
        File.Copy(file, Path.Combine(host2.ConfigDir, "InventoryView.xml"));
        var ext2 = new InventoryViewExtension();
        ext2.Initialize(host2);
        Assert.DoesNotContain(host2.Echoed, e => e.Contains("repaired"));

        _ = expected;   // parity asserted via the Walk comparison above
    }

    // ── Splitter schemes ────────────────────────────────────────────────────────

    [Fact]
    public void Splitter_handles_vault_and_trader_schemes_merged_and_classic()
    {
        var vault = InventoryViewExtension.SplitMergedItems(
            "    a large steel trunk      -a jeweled sword        -a ruby",
            InventoryViewExtension.VaultLevel);
        Assert.Equal(new[] { (1, "a large steel trunk"), (2, "a jeweled sword"), (3, "a ruby") }, vault);

        var vaultClassic = InventoryViewExtension.SplitMergedItems(
            "      -a jeweled sword", InventoryViewExtension.VaultLevel);
        Assert.Equal(new[] { (2, "a jeweled sword") }, vaultClassic);

        var trader = InventoryViewExtension.SplitMergedItems(
            "    a crate        -a bolt of silk            -a silver pin",
            InventoryViewExtension.TraderLevel);
        Assert.Equal(new[] { (1, "a crate"), (2, "a bolt of silk"), (3, "a silver pin") }, trader);

        // The HELP hint glued to the last item is stripped.
        var inv = InventoryViewExtension.SplitMergedItems(
            "  a ring[Use INVENTORY HELP for more options.]",
            InventoryViewExtension.InventoryLevel);
        Assert.Equal(new[] { (1, "a ring") }, inv);
    }

    // ── Command surface / guards ────────────────────────────────────────────────

    [Fact]
    public void Slash_command_claims_iv_and_ignores_others()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);

        Assert.True(ext.OnSlashCommand("/iv help"));
        Assert.True(ext.OnSlashCommand("/inventoryview"));
        Assert.False(ext.OnSlashCommand("/ivy"));          // prefix must be exact
        Assert.False(ext.OnSlashCommand("/sort weapon"));
    }

    [Fact]
    public void Scan_requires_a_connection_and_rejects_reentry()
    {
        var host = NewHost();
        host.Vars["connected"] = "0";
        var ext = new InventoryViewExtension();
        ext.Initialize(host);

        ext.OnSlashCommand("/iv scan");
        Assert.Contains(host.Echoed, e => e.Contains("must be connected"));
        Assert.Empty(host.Sent);

        host.Vars["connected"] = "1";
        ext.OnSlashCommand("/iv scan");
        Assert.Contains("inventory list", host.Sent);
        ext.OnSlashCommand("/iv scan");                    // mid-scan re-entry
        Assert.Contains(host.Echoed, e => e.Contains("already in progress"));
    }

    /// <summary>Spin until <paramref name="done"/> or the budget runs out.</summary>
    private static async Task WaitUntil(Func<bool> done, int budgetMs = 5000)
    {
        for (int waited = 0; waited < budgetMs && !done(); waited += 25)
            await Task.Delay(25);
        Assert.True(done(), "condition never became true");
    }

    // ── Scan lifecycle: nothing may wedge the FSM permanently ────────────────

    [Fact]
    public async Task A_scan_the_game_never_advances_times_out_and_unblocks()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);
        ext.ScanIdleTimeout = TimeSpan.FromMilliseconds(150);

        ext.OnSlashCommand("/iv scan");
        Feed(ext, "You have:");
        Feed(ext, "  a backpack");
        Feed(ext, "Roundtime: 0 secs.");   // → VaultStart, waiting on the vault book
        Assert.True(ext.ScanInProgress);
        // …and the game never answers (stunned, hands full, reworded message).

        await WaitUntil(() => !ext.ScanInProgress);

        Assert.Contains(host.Echoed, e => e.Contains("gave up waiting"));
        Assert.Empty(ext.SnapshotCatalog());          // the partial rows went with it

        // The whole point: /iv scan works again instead of echoing forever.
        host.Sent.Clear();
        ext.OnSlashCommand("/iv scan");
        Assert.Contains("inventory list", host.Sent);
        Assert.DoesNotContain(host.Echoed, e => e.Contains("already in progress"));
    }

    [Fact]
    public async Task A_scan_the_game_is_still_feeding_is_not_timed_out()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);
        ext.ScanIdleTimeout = TimeSpan.FromMilliseconds(400);

        ext.OnSlashCommand("/iv scan");
        Feed(ext, "You have:");
        // A long list trickling in over more than one timeout window must survive:
        // every catalogued line pushes the deadline out.
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(150);
            Feed(ext, $"  item {i}");
        }
        Assert.True(ext.ScanInProgress);
        Assert.Equal(6, ext.SnapshotCatalog().Single(c => c.Source == "Inventory").Items.Count);

        ext.OnSlashCommand("/iv cancel");
    }

    [Fact]
    public void Cancel_drops_a_stuck_scan_and_its_partial_rows()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);

        ext.OnSlashCommand("/iv scan");
        Feed(ext, "You have:");
        Feed(ext, "  a backpack");
        Assert.True(ext.ScanInProgress);
        Assert.NotEmpty(ext.SnapshotCatalog());       // half a scan sitting in memory

        Assert.True(ext.OnSlashCommand("/iv cancel"));
        Assert.False(ext.ScanInProgress);
        Assert.Contains(host.Echoed, e => e.Contains("scan cancelled"));
        Assert.Empty(ext.SnapshotCatalog());          // and discarded, not persisted

        ext.OnSlashCommand("/iv cancel");             // idempotent
        Assert.Contains(host.Echoed, e => e.Contains("no scan is running"));
    }

    [Fact]
    public void Reload_is_refused_mid_scan_so_the_scan_is_not_orphaned()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);

        ext.OnSlashCommand("/iv scan");
        Feed(ext, "You have:");
        ext.OnSlashCommand("/iv reload");             // would swap _characterData out
        Assert.Contains(host.Echoed, e => e.Contains("reload skipped"));
        Assert.DoesNotContain(host.Echoed, e => e.Contains("data reloaded"));

        // The scan still lands in the live catalog rather than a detached object.
        Feed(ext, "  a backpack");
        Feed(ext, "Roundtime: 1 sec.");
        Feed(ext, "What were you referring to?");
        Feed(ext, "What were you referring to?");
        Feed(ext, "The home contains:");
        ext.OnPrompt();

        Assert.Contains(host.Echoed, e => e.Contains("Scan Complete"));
        Assert.Equal("a backpack",
            Assert.Single(ext.SnapshotCatalog().Single(c => c.Source == "Inventory").Items).Tap);
    }

    [Fact]
    public void A_character_switch_mid_scan_leaves_no_partial_rows_behind()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);

        ext.OnSlashCommand("/iv scan");
        Feed(ext, "You have:");
        Feed(ext, "  a backpack");
        Assert.NotEmpty(ext.SnapshotCatalog());

        ext.OnReset();                                // switched to another character
        Assert.False(ext.ScanInProgress);
        Assert.Empty(ext.SnapshotCatalog());

        // Renucci's truncated Inventory must not ride along into Naper's save.
        host.Vars["charactername"] = "Naper";
        RunScan(ext, "  a sword");
        Assert.All(ext.SnapshotCatalog(), c => Assert.Equal("Naper", c.Name));
    }

    [Fact]
    public void A_combat_roundtime_before_the_list_does_not_end_the_inventory_phase()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);

        ext.OnSlashCommand("/iv scan");
        Feed(ext, "You have:");
        // Busy room: a swing and ITS roundtime arrive before the list does. Taking
        // that roundtime as the terminator would file an empty Inventory and move
        // on to the vault book.
        Feed(ext, "The Shard sentinel swings a mace at you!");
        Feed(ext, "Roundtime: 5 secs.");

        // The real list still arrives, and is still catalogued.
        Feed(ext, "  a backpack");
        Feed(ext, "     -a gem");
        Feed(ext, "Roundtime: 1 sec.");           // the list's own roundtime ends it
        Feed(ext, "What were you referring to?");
        Feed(ext, "What were you referring to?");
        Feed(ext, "The home contains:");
        ext.OnPrompt();

        Assert.Contains(host.Echoed, e => e.Contains("Scan Complete"));
        var inv = ext.SnapshotCatalog().Single(c => c.Source == "Inventory");
        Assert.Equal("a backpack", Assert.Single(inv.Items).Tap);
        Assert.Equal("a gem", Assert.Single(inv.Items[0].Items).Tap);
    }

    [Fact]
    public void An_empty_inventory_still_ends_on_its_own_roundtime()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);

        ext.OnSlashCommand("/iv scan");
        Feed(ext, "You have:");
        Feed(ext, "Roundtime: 1 sec.");           // nothing between: carrying nothing
        Feed(ext, "What were you referring to?");
        Feed(ext, "What were you referring to?");
        Feed(ext, "The home contains:");
        ext.OnPrompt();

        Assert.Contains(host.Echoed, e => e.Contains("Scan Complete"));
        Assert.Empty(ext.SnapshotCatalog().Single(c => c.Source == "Inventory").Items);
    }

    [Fact]
    public void Unindented_main_stream_chatter_is_not_catalogued_mid_scan()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);

        ext.OnSlashCommand("/iv scan");
        Feed(ext, "You have:");
        Feed(ext, "  a backpack");
        // Real main-stream traffic arriving mid-list: list entries carry an indent,
        // game prose never does.
        Feed(ext, "The Shard sentinel strides north, his hand on his mace.");
        Feed(ext, "Ice Cold Natarian entered an ebony and steel gate.");
        Feed(ext, "     -a gem");
        Feed(ext, "Roundtime: 1 sec.");
        Feed(ext, "What were you referring to?");
        Feed(ext, "What were you referring to?");
        Feed(ext, "The home contains:");
        ext.OnPrompt();

        var inv = ext.SnapshotCatalog().Single(c => c.Source == "Inventory");
        Assert.Equal("a backpack", Assert.Single(inv.Items).Tap);
        Assert.Equal("a gem", Assert.Single(inv.Items[0].Items).Tap);
    }

    [Fact]
    public void Non_main_stream_lines_are_ignored_mid_scan()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);

        ext.OnSlashCommand("/iv scan");
        Feed(ext, "You have:");
        // A thoughts-stream line arriving mid-scan must not become an item.
        ext.OnGameEvent(new TextEvent("thoughts", "  You hear the thoughts of somebody  rambling on"));
        Feed(ext, "  a backpack");
        Feed(ext, "Roundtime: 1 sec.");
        Feed(ext, "What were you referring to?");
        Feed(ext, "What were you referring to?");
        Feed(ext, "The home contains:");
        ext.OnPrompt();

        var inv = ext.SnapshotCatalog().Single(c => c.Source == "Inventory");
        Assert.Equal("a backpack", Assert.Single(inv.Items).Tap);
    }

    // ── Background wiki resolution ────────────────────────────────────

    [Fact]
    public async Task Taps_requested_while_a_wiki_run_is_active_are_still_resolved()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);

        var asked = new List<string>();
        ext.ItemInfo.QueryApi = titles =>
        {
            lock (asked) asked.AddRange(titles);
            return Task.FromResult<string?>(null);   // API down: nothing gets cached
        };

        ext.RequestItemInfo(new[] { "a first item" });
        // Lands while the run is still going (a second scan finishing, say). The
        // run's work list is built as it goes, so this is picked up rather than
        // stranded until some later request happens to find the run idle.
        await Task.Delay(50);
        ext.RequestItemInfo(new[] { "a second item" });

        await WaitUntil(() => { lock (asked) return asked.Contains("Item:second item"); });
        lock (asked) Assert.Contains("Item:first item", asked);
    }

    [Fact]
    public void Requests_after_shutdown_do_not_throw()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);
        ext.ItemInfo.QueryApi = _ => Task.FromResult<string?>(null);

        ext.Shutdown();
        // Reads _cts.Token on the caller's thread — the window's own refresh path,
        // which is not inside the extension manager's try/catch.
        ext.RequestItemInfo(new[] { "a steel ingot" });
    }

    [Fact]
    public void Search_and_open_route_through_the_ui_seams()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);
        RunScan(ext, "  a backpack");

        string? opened = null;
        ext.OpenRequested += term => opened = term;
        Assert.True(ext.OnSlashCommand("/iv search backpack"));
        Assert.Equal("backpack", opened);
        Assert.True(ext.OnSlashCommand("/iv open"));
        Assert.Equal("", opened);
    }

    [Fact]
    public async Task Wiki_opens_the_exact_page_when_it_exists_else_the_search()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);

        // Exact page exists (resolver finds the Item: title, redirect-resolved).
        ext.ResolveWikiTitle = _ => Task.FromResult<string?>("Item:Steel ingot");
        await ext.WikiLookupAsync("a steel ingot");
        Assert.Equal("#browser https://elanthipedia.play.net/index.php?title=Item%3ASteel%20ingot",
                     host.HashCmds[^1]);

        // No page → the wiki's search, with the article stripped from the tap.
        ext.ResolveWikiTitle = _ => Task.FromResult<string?>(null);
        await ext.WikiLookupAsync("a razor-edged scimitar");
        Assert.Equal("#browser https://elanthipedia.play.net/index.php?title=Special%3ASearch&search=razor-edged%20scimitar",
                     host.HashCmds[^1]);
    }

    [Fact]
    public void Tap_cleaning_strips_articles_and_the_closed_suffix()
    {
        Assert.Equal("steel ingot",   InventoryViewExtension.CleanTap("a steel ingot"));
        Assert.Equal("oak staff",     InventoryViewExtension.CleanTap("an oak staff"));
        Assert.Equal("blowgun darts", InventoryViewExtension.CleanTap("some blowgun darts"));
        Assert.Equal("red gem pouch", InventoryViewExtension.CleanTap("a red gem pouch (closed)"));
        Assert.Equal(new[] { "Item:vault book", "Weapon:vault book", "Armor:vault book", "Shield:vault book" },
                     InventoryViewExtension.CandidateTitles("vault book"));
    }

    [Fact]
    public void Export_writes_a_row_per_item_and_remove_drops_a_character()
    {
        var host = NewHost();
        var ext  = new InventoryViewExtension();
        ext.Initialize(host);
        RunScan(ext, "  a backpack", "     -a gem");

        var csv = ext.ExportCsv(Path.Combine(host.ConfigDir, "iv.csv"));
        Assert.NotNull(csv);
        var lines = File.ReadAllLines(csv!);
        Assert.Equal("Character,Source,Tap,Path", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("Renucci,Inventory,a gem,"));

        Assert.True(ext.RemoveCharacter("renucci"));       // case-insensitive
        Assert.Empty(ext.SnapshotCatalog());
        Assert.False(ext.RemoveCharacter("Renucci"));      // already gone
    }
}
