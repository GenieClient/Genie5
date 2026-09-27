using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Genie.Core.Extensions;
using Genie.Core.Extensions.Builtin.SpellInfo;
using Genie.Core.Parsing;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #267 — the SpellInfo port (lookup half). The fixture is the real nightly
/// feed trimmed to thirteen spells, kept in its original shape: every printout an
/// array, <c>Guild</c> an object, <c>&amp;nbsp;</c> in the effects, the literal
/// <c>none</c> abbreviation, and a <c>(spell)</c> disambiguation title.
/// </summary>
public class SpellInfoTests
{
    private static readonly string FixtureJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spells_fixture.json"));

    private static SpellTable Table() => SpellFeed.Parse(FixtureJson);

    // ── Feed parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void TheFixtureParsesInTheFeedsRealShape()
    {
        var t = Table();

        Assert.Equal(13, t.Spells.Count);
        Assert.Equal("2026-09-27T06:00:29Z", t.SyncedAt!.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Equal(t.Spells.Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase), t.Spells.Select(s => s.Name));

        var heart = t.Spells.Single(s => s.Name == "Abandoned Heart");
        Assert.Equal("ABAN", heart.Abbrev);
        Assert.Equal("Bard", heart.Guild);                 // an object in the feed
        Assert.Equal("Elemental Magic", heart.ManaType);
        Assert.Equal("Emotion Control", heart.Spellbook);
        Assert.Equal("advanced", heart.Difficulty);
        Assert.True(heart.Signature);
        Assert.Equal(new[] { "cyclic" }, heart.CastTypes);
        Assert.Equal(new[] { "targeted" }, heart.Skills);
        Assert.Equal(new[] { "area of effect" }, heart.SpellTypes);
        Assert.Null(heart.OffContest);                      // [] in the feed
        Assert.Equal(7, heart.MinPrep);
        Assert.Equal(37, heart.Cap);
        Assert.Equal(250, heart.MinSkill);
        Assert.Equal(1000, heart.MaxSkill);
        Assert.Equal(3, heart.Slots);
        Assert.Equal("https://elanthipedia.play.net/Abandoned_Heart", heart.WikiUrl);
    }

    [Fact]
    public void EffectEntitiesAreDecoded()
    {
        var heart = Table().Spells.Single(s => s.Name == "Abandoned Heart");
        Assert.Equal("Fatigue damage, Spirit damage, Area of Effect.", heart.Effect);
        Assert.DoesNotContain(Table().Spells, s => s.Effect is { } e && (e.Contains('&') || e.Contains(' ')));
    }

    [Theory]
    [InlineData("Fire damage,&nbsp;Single target.", "Fire damage, Single target.")]
    [InlineData("a &amp; b", "a & b")]
    [InlineData("<b>bold</b> text", "bold text")]
    [InlineData("&nbsp;", null)]
    public void CleanEffectTidiesWikiText(string raw, string? expected)
        => Assert.Equal(expected, SpellFeed.CleanEffect(new[] { raw }));

    [Fact]
    public void TheNoneAbbreviationAndTheSpellSuffixAreNormalised()
    {
        var t = Table();
        Assert.Null(t.Spells.Single(s => s.Name == "Aethrolysis").Abbrev);

        var burden = t.Spells.Single(s => s.PageTitle == "Burden (spell)");
        Assert.Equal("Burden", burden.Name);
        Assert.Equal("None", burden.Guild);
        Assert.Equal("Analogous Patterns", burden.ManaType);
        Assert.Equal("magic", burden.OffContest);
        Assert.Equal("fortitude", burden.DefContest);
    }

    [Fact]
    public void MultiValuedAndMissingFieldsSurvive()
    {
        var t = Table();
        Assert.Equal(new[] { "debilitation", "utility" }, t.Spells.Single(s => s.Name == "Albreda's Balm").Skills);
        Assert.Equal(new[] { "battle", "cyclic" }, t.Spells.Single(s => s.Name == "Cheetah Swiftness").CastTypes);

        var curing = t.Spells.Single(s => s.Name == "Adaptive Curing");   // metamagic
        Assert.Null(curing.Difficulty);
        Assert.Empty(curing.Skills);
        Assert.Null(curing.MinPrep);
        Assert.Null(curing.Cap);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"query\":{\"results\":[]}}")]
    [InlineData("{\"query\":{\"results\":{}}}")]
    public void AnythingButTheFeedIsRejected(string json)
        => Assert.Throws<FormatException>(() => SpellFeed.Parse(json));

    // ── Query language ───────────────────────────────────────────────────────

    private static SpellFilter ParseQuery(string text) =>
        SpellQuery.Parse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), out var err)
        ?? throw new Xunit.Sdk.XunitException(err);

    private static string? QueryError(string text)
    {
        Assert.Null(SpellQuery.Parse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), out var err));
        return err;
    }

    [Fact]
    public void MultiWordValuesAreJoined()
    {
        var f = ParseQuery("/guild warrior mage /skill targeted");
        Assert.Equal("warrior mage", f.Guild);
        Assert.Equal("targeted", f.Skill);
        Assert.Null(f.Mana);
    }

    [Fact]
    public void MalformedQueriesExplainThemselves()
    {
        Assert.Contains("must pass some parameters", QueryError(""));
        Assert.Contains("Invalid filter type: /colour", QueryError("/colour red"));
        Assert.Contains("comes before any filter", QueryError("fire /mana elemental"));
        Assert.Contains("/mana needs a value", QueryError("/mana /skill targeted"));
    }

    private static List<string> Names(SpellQueryResult r) => r.Matches.Select(s => s.Name).ToList();

    [Fact]
    public void FiltersStack()
    {
        var t = Table();

        var elemental = SpellQuery.Run(ParseQuery("/mana elemental"), t);
        Assert.Null(elemental.Error);
        Assert.Equal(9, elemental.Matches.Count);

        var stacked = SpellQuery.Run(ParseQuery("/mana elemental /difficulty intro /skill targeted"), t);
        Assert.Null(stacked.Error);
        Assert.Equal(new[] { "Air Lash", "Fire Shards", "Gar Zeng", "Stone Strike" }, Names(stacked));
        Assert.Equal(new[] { ("mana", "Elemental Magic"), ("difficulty", "intro"), ("skill", "targeted") },
                     stacked.Applied);

        var narrower = SpellQuery.Run(ParseQuery("/mana elemental /difficulty intro /skill targeted /guild bard"), t);
        Assert.Null(narrower.Error);
        Assert.Empty(narrower.Matches);
    }

    [Theory]
    [InlineData("/guild warrior mage", "Warrior Mage")]
    [InlineData("/guild WARRIOR", "Warrior Mage")]      // unique prefix
    [InlineData("/mana life", "Life Magic")]            // the Genie 4 spelling
    [InlineData("/mana analogous", "Analogous Patterns")]
    [InlineData("/difficulty inter", "intermediate")]
    [InlineData("/skill aug", "augmentation")]
    [InlineData("/skill tm", "targeted")]                // the plugin's own abbreviation
    public void ValuesResolveAgainstTheData(string query, string resolved)
    {
        var r = SpellQuery.Run(ParseQuery(query), Table());
        Assert.Null(r.Error);
        Assert.Equal(resolved, r.Applied.Single().Value);
        Assert.NotEmpty(r.Matches);
    }

    [Fact]
    public void AnAmbiguousOrUnknownValueListsTheChoices()
    {
        var t = Table();

        var ambiguous = SpellQuery.Run(ParseQuery("/difficulty in"), t);   // intro? intermediate?
        Assert.Contains("No difficulty matches 'in'", ambiguous.Error);
        Assert.Contains("intermediate", ambiguous.Error);
        Assert.Contains("intro", ambiguous.Error);

        var unknown = SpellQuery.Run(ParseQuery("/guild barbarian"), t);
        Assert.Contains("Choose one of: Bard, Empath, None, Ranger, Warrior Mage", unknown.Error);
    }

    [Fact]
    public void AMultiSkillSpellMatchesEitherSkill()
    {
        var t = Table();
        Assert.Contains("Albreda's Balm", Names(SpellQuery.Run(ParseQuery("/skill debilitation"), t)));
        Assert.Contains("Albreda's Balm", Names(SpellQuery.Run(ParseQuery("/skill utility"), t)));
    }

    // ── Command surface ──────────────────────────────────────────────────────

    private sealed class FakeHost : IExtensionHost
    {
        public readonly List<string> Echoed  = new();
        public readonly List<string> Hash    = new();
        public readonly List<bool>   Parsed  = new();
        public IDictionary<string, string> Globals { get; } = new Dictionary<string, string>();
        public string ConfigDir { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "genie-si-" + Guid.NewGuid().ToString("N"))).FullName;
        public string DataRoot => ConfigDir;
        public void Echo(string text) => EchoRouted(text, true, true);
        public void EchoRouted(string text, bool display, bool parse)
        {
            lock (Echoed) { Echoed.Add(text); Parsed.Add(parse); }
        }
        public void SendCommand(string command) => throw new InvalidOperationException("SpellInfo must never send to the game: " + command);
        public void SetWindow(string window, string content) { }
        public void Log(string message) { }
        public void RunHashCommand(string command) { lock (Echoed) Hash.Add(command); }

        public string AllEchoes { get { lock (Echoed) return string.Join("\n", Echoed); } }
    }

    /// <summary>Stands in for the blob host. Counts requests; can be told to fail,
    /// serve garbage, or hold the response until released.</summary>
    private sealed class FakeFeed : HttpMessageHandler
    {
        public int Requests;
        public Func<HttpResponseMessage>? Reply;
        public Exception? Throw;
        public TaskCompletionSource? Hold;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            Assert.Equal(SpellFeedCache.FeedUrl, request.RequestUri!.ToString());
            if (Hold is not null) await Hold.Task;
            if (Throw is not null) throw Throw;
            return Reply?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FixtureJson) };
        }
    }

    private sealed class Rig
    {
        public readonly FakeHost Host = new();
        public readonly FakeFeed Feed = new();
        public readonly SpellInfoExtension Ext;
        public DateTime Now = DateTime.UtcNow;
        public string CachePath => Path.Combine(Host.DataRoot, SpellInfoExtension.CacheFileName);

        public Rig(bool seededCache = false, TimeSpan? cacheAge = null)
        {
            if (seededCache)
            {
                File.WriteAllText(CachePath, FixtureJson);
                File.SetLastWriteTimeUtc(CachePath, Now - (cacheAge ?? TimeSpan.FromHours(1)));
            }
            Ext = new SpellInfoExtension
            {
                CacheFactory = dir => new SpellFeedCache(
                    Path.Combine(dir, SpellInfoExtension.CacheFileName), Feed, () => Now),
            };
            Ext.Initialize(Host);
        }

        public bool Run(string cmd) => Ext.OnSlashCommand(cmd);

        public async Task Settle()
        {
            for (int i = 0; i < 3 && Ext.PendingWork is { } w; i++)
            {
                await w.WaitAsync(TimeSpan.FromSeconds(10));
                if (ReferenceEquals(w, Ext.PendingWork)) break;
            }
        }
    }

    /// <summary>A #link line, split the way CommandEngine's #link handler splits it.</summary>
    private static (string Text, string Command) ParseLink(string hash)
    {
        Assert.StartsWith("#link ", hash);
        var parts = ArgumentParser.ParseArgs(hash[1..]);
        Assert.Equal("link", parts[0]);
        return (parts[1], string.Join(" ", parts.Skip(2)));
    }

    [Theory]
    [InlineData("/spellinfo")]
    [InlineData("/SpellInfo help")]
    public void BareOrHelpShowsTheCommandsWithoutTouchingTheData(string cmd)
    {
        var rig = new Rig();
        Assert.True(rig.Run(cmd));
        Assert.Contains("/spellinfo query (/guild <guild>)", rig.Host.AllEchoes);
        Assert.Equal(0, rig.Feed.Requests);
        Assert.False(File.Exists(rig.CachePath));
    }

    [Fact]
    public void OtherSlashCommandsAreLeftAlone()
    {
        var rig = new Rig();
        Assert.False(rig.Run("/spell"));
        Assert.False(rig.Run("/calc"));
        rig.Ext.Enabled = false;
        Assert.False(rig.Run("/spellinfo help"));
    }

    [Fact]
    public void QueryHelpMatchesThePluginsExamples()
    {
        var rig = new Rig();
        rig.Run("/spellinfo query help");
        Assert.Contains("/spellinfo query /mana elemental /difficulty intro /skill targeted", rig.Host.AllEchoes);
        Assert.Equal(0, rig.Feed.Requests);
    }

    [Fact]
    public void NameShowsTheDetailView()
    {
        var rig = new Rig(seededCache: true);
        rig.Run("/spellinfo name abandoned   heart");

        Assert.Equal(new[]
        {
            "Name: Abandoned Heart",
            "Abbr / Sig.: ABAN / signature",
            "Guild: Bard",
            "Mana / Book: Elemental Magic / Emotion Control",
            "Diff/Type: advanced cyclic spell.",
            "Skill: targeted",
            "Spell Type: area of effect",
            "Effect: Fatigue damage, Spirit damage, Area of Effect.",
            "Cast Range: 7 - 37",
            "Rank Range: 250 - 1000",
            "Slots: 3",
        }, rig.Host.Echoed);

        var wiki = ParseLink(Assert.Single(rig.Host.Hash));
        Assert.Equal(("Elanthipedia: Abandoned Heart", "#browser https://elanthipedia.play.net/Abandoned_Heart"), wiki);
        Assert.Equal(0, rig.Feed.Requests);   // a fresh cache is never re-fetched
    }

    [Fact]
    public void DetailsAreDisplayedButNotFedToTriggers()
    {
        var rig = new Rig(seededCache: true);
        rig.Run("/spellinfo name Fire Shards");
        Assert.Contains("Effect: Impact damage, Fire damage, Single target multi-strike.", rig.Host.Echoed);
        Assert.All(rig.Host.Parsed, p => Assert.False(p));
    }

    [Fact]
    public void ContestsAndThePageTitleFormWork()
    {
        var rig = new Rig(seededCache: true);
        rig.Run("/spellinfo name Burden (spell)");
        Assert.Contains("Name: Burden", rig.Host.Echoed);
        Assert.Contains(rig.Host.Echoed, e => e.StartsWith("Contest: magic v "));
    }

    [Fact]
    public void APartialNameWithSeveralHitsListsThem()
    {
        var rig = new Rig(seededCache: true);
        rig.Run("/spellinfo name ae");

        Assert.Contains("SpellInfo: 2 spells match 'ae':", rig.Host.Echoed);
        var links = rig.Host.Hash.Select(ParseLink).ToList();
        Assert.Equal(new[]
        {
            ("Aether Cloak (ac)", "/spellinfo name Aether Cloak"),
            ("Aethrolysis",       "/spellinfo name Aethrolysis"),   // no abbreviation shown for "none"
        }, links);
    }

    [Fact]
    public void AnUnknownNameSaysSo()
    {
        var rig = new Rig(seededCache: true);
        rig.Run("/spellinfo name Moonblade");
        Assert.Contains("no spell named 'Moonblade'", rig.Host.AllEchoes);
        Assert.Empty(rig.Host.Hash);
    }

    [Fact]
    public void AbbrIsCaseInsensitive()
    {
        var rig = new Rig(seededCache: true);
        rig.Run("/spellinfo abbr aban");
        Assert.Equal("Name: Abandoned Heart", rig.Host.Echoed[0]);

        rig.Host.Echoed.Clear();
        rig.Run("/spellinfo abbr none");   // the feed's placeholder is not an abbreviation
        Assert.Contains("no spell has the abbreviation 'none'", rig.Host.AllEchoes);
    }

    [Fact]
    public void GuildAndManaListClickableRowsThatDrillIn()
    {
        var rig = new Rig(seededCache: true);
        rig.Run("/spellinfo guild warrior mage");

        Assert.Contains("SpellInfo: 7 spells — guild Warrior Mage. Click one for details.", rig.Host.Echoed);
        var links = rig.Host.Hash.Select(ParseLink).ToList();
        Assert.Equal(7, links.Count);
        Assert.Contains(("Gar Zeng (gz)", "/spellinfo name Gar Zeng"), links);

        // Clicking a row runs its command through the same slash dispatch.
        rig.Host.Echoed.Clear();
        rig.Host.Hash.Clear();
        Assert.True(rig.Run(links.Single(l => l.Text.StartsWith("Gar Zeng")).Command));
        Assert.Equal("Name: Gar Zeng", rig.Host.Echoed[0]);

        rig.Host.Hash.Clear();
        rig.Run("/spellinfo mana life");
        Assert.Equal(new[] { "Adaptive Curing", "Cheetah Swiftness" },
                     rig.Host.Hash.Select(h => ParseLink(h).Command["/spellinfo name ".Length..]));
    }

    [Fact]
    public void AnApostropheNameSurvivesTheLinkRoundTrip()
    {
        var rig = new Rig(seededCache: true);
        rig.Run("/spellinfo query /guild bard /skill debilitation");
        var (_, command) = ParseLink(Assert.Single(rig.Host.Hash));
        Assert.Equal("/spellinfo name Albreda's Balm", command);

        rig.Host.Echoed.Clear();
        rig.Run(command);
        Assert.Equal("Name: Albreda's Balm", rig.Host.Echoed[0]);
    }

    [Fact]
    public void AStackedQueryListsItsCriteriaThenTheRows()
    {
        var rig = new Rig(seededCache: true);
        rig.Run("/spellinfo query /mana elemental /difficulty intro /skill tm");

        Assert.Equal("SpellInfo: 4 spells — mana Elemental Magic, difficulty intro, skill targeted. Click one for details.",
                     Assert.Single(rig.Host.Echoed));
        Assert.Equal(4, rig.Host.Hash.Count);
    }

    [Fact]
    public void QueryErrorsAreReportedAndNothingIsListed()
    {
        var rig = new Rig(seededCache: true);
        rig.Run("/spellinfo query /mana");
        rig.Run("/spellinfo query /guild barbarian");
        rig.Run("/spellinfo guild barbarian");

        Assert.Contains("SpellInfo: /mana needs a value.", rig.Host.Echoed);
        Assert.Contains(rig.Host.Echoed, e => e.StartsWith("SpellInfo: No guild matches 'barbarian'"));
        Assert.Empty(rig.Host.Hash);
    }

    [Fact]
    public void AMissingArgumentShowsUsage()
    {
        var rig = new Rig();
        rig.Run("/spellinfo name");
        rig.Run("/spellinfo foo");
        Assert.Contains("Usage: /spellinfo name <spell name>", rig.Host.Echoed);
        Assert.Contains("unknown option 'foo'", rig.Host.AllEchoes);
        Assert.Equal(0, rig.Feed.Requests);
    }

    [Theory]
    [InlineData("A}; #put x", "A #put x")]
    [InlineData("{a}\"b\\c", "abc")]
    public void LinkTextCannotEscapeItsGroupOrChainACommand(string raw, string safe)
        => Assert.Equal(safe, SpellInfoExtension.LinkSafe(raw));

    [Fact]
    public void AWikiLinkOffTheWikiHostIsNotBuilt()
    {
        var json = FixtureJson.Replace("https://elanthipedia.play.net/Air_Lash", "https://example.com/x");
        var rig = new Rig();
        File.WriteAllText(rig.CachePath, json);
        rig.Run("/spellinfo name Air Lash");
        Assert.Contains("Name: Air Lash", rig.Host.Echoed);
        Assert.Empty(rig.Host.Hash);
    }

    // ── Cache, rate limit, offline ───────────────────────────────────────────

    [Fact]
    public void NothingIsFetchedOnTheGameLinePath()
    {
        var rig = new Rig();
        rig.Ext.OnGameLine("You recognize the familiar mnemonics of the Air Lash spell.");
        rig.Ext.OnPrompt();
        rig.Ext.OnCommandSent("look");
        ((IGameExtension)rig.Ext).OnReset();

        Assert.Null(rig.Ext.PendingWork);
        Assert.Equal(0, rig.Feed.Requests);
        Assert.False(File.Exists(rig.CachePath));
    }

    [Fact]
    public async Task FirstUseDownloadsCachesAndAnswers()
    {
        var rig = new Rig();
        rig.Run("/spellinfo abbr ABAN");
        Assert.Equal("SpellInfo: downloading the spell list (first use)...", rig.Host.Echoed[0]);

        await rig.Settle();
        Assert.Equal(1, rig.Feed.Requests);
        Assert.Contains("Name: Abandoned Heart", rig.Host.Echoed);
        Assert.True(File.Exists(rig.CachePath));
        Assert.Equal(13, SpellFeed.Parse(File.ReadAllText(rig.CachePath)).Spells.Count);

        // …and the next command is answered from memory, no second request.
        rig.Run("/spellinfo abbr gz");
        Assert.Contains("Name: Gar Zeng", rig.Host.Echoed);
        Assert.Equal(1, rig.Feed.Requests);
    }

    [Fact]
    public async Task CommandsDuringADownloadShareIt()
    {
        var rig = new Rig();
        rig.Feed.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        rig.Run("/spellinfo abbr aban");
        var first = rig.Ext.PendingWork!;
        rig.Run("/spellinfo abbr gz");
        var second = rig.Ext.PendingWork!;

        rig.Feed.Hold.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, rig.Feed.Requests);
        Assert.Contains("Name: Abandoned Heart", rig.Host.Echoed);
        Assert.Contains("Name: Gar Zeng", rig.Host.Echoed);
    }

    [Fact]
    public async Task ADayOldCacheAnswersAtOnceAndRefreshesInTheBackground()
    {
        var rig = new Rig(seededCache: true, cacheAge: TimeSpan.FromHours(25));
        rig.Run("/spellinfo abbr aban");

        // Answered from the cache before any download completes.
        Assert.Equal("Name: Abandoned Heart", rig.Host.Echoed[0]);
        await rig.Settle();

        Assert.Equal(1, rig.Feed.Requests);
        Assert.Equal(rig.Now, File.GetLastWriteTimeUtc(rig.CachePath), TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("downloading", rig.Host.AllEchoes);
    }

    [Fact]
    public async Task OfflineWithACacheStillAnswers()
    {
        var rig = new Rig(seededCache: true, cacheAge: TimeSpan.FromDays(3));
        rig.Feed.Throw = new HttpRequestException("No such host is known.");

        rig.Run("/spellinfo name Air Lash");
        await rig.Settle();
        Assert.Contains("Name: Air Lash", rig.Host.Echoed);
        Assert.DoesNotContain("couldn't", rig.Host.AllEchoes);   // a quiet refresh fails quietly

        rig.Run("/spellinfo refresh");
        await rig.Settle();
        Assert.Contains("couldn't refresh the spell list — couldn't reach the feed (No such host is known.). "
                      + "Still using the cached copy, wiki data from 2026-09-27 06:00 UTC.", rig.Host.AllEchoes);
    }

    [Fact]
    public async Task OfflineWithNoCacheFailsSoft()
    {
        var rig = new Rig();
        rig.Feed.Throw = new HttpRequestException("No such host is known.");

        rig.Run("/spellinfo guild bard");
        await rig.Settle();

        Assert.Contains("SpellInfo: couldn't download the spell list — couldn't reach the feed", rig.Host.AllEchoes);
        Assert.Empty(rig.Host.Hash);
        Assert.False(File.Exists(rig.CachePath));
    }

    [Fact]
    public async Task ABadDownloadNeverReplacesAGoodCache()
    {
        var rig = new Rig(seededCache: true, cacheAge: TimeSpan.FromDays(2));
        rig.Feed.Reply = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>maintenance</html>") };

        rig.Run("/spellinfo refresh");
        await rig.Settle();

        Assert.Contains("couldn't refresh the spell list — The spell list isn't valid JSON.", rig.Host.AllEchoes);
        Assert.Equal(FixtureJson, File.ReadAllText(rig.CachePath));
    }

    [Fact]
    public async Task AnHttpErrorIsReported()
    {
        var rig = new Rig();
        rig.Feed.Reply = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        rig.Run("/spellinfo refresh");
        await rig.Settle();
        Assert.Contains("the feed answered 404", rig.Host.AllEchoes);
    }

    [Fact]
    public void RefreshIsRateLimitedToOnceADay()
    {
        var rig = new Rig(seededCache: true, cacheAge: TimeSpan.FromHours(23));
        rig.Run("/spellinfo refresh");

        Assert.Equal(0, rig.Feed.Requests);
        Assert.Null(rig.Ext.PendingWork);
        Assert.Contains("the spell list is up to date (13 spells", rig.Host.AllEchoes);
    }

    [Fact]
    public async Task RefreshFetchesAStaleCache()
    {
        var rig = new Rig(seededCache: true, cacheAge: TimeSpan.FromHours(24));
        rig.Run("/spellinfo refresh");
        await rig.Settle();

        Assert.Equal(1, rig.Feed.Requests);
        Assert.Contains("SpellInfo: loaded 13 spells, wiki data from 2026-09-27 06:00 UTC.", rig.Host.Echoed);
    }

    [Fact]
    public async Task ACorruptCacheCountsAsNone()
    {
        var rig = new Rig();
        File.WriteAllText(rig.CachePath, "{ torn");
        rig.Run("/spellinfo abbr aban");
        await rig.Settle();

        Assert.Equal(1, rig.Feed.Requests);
        Assert.Contains("Name: Abandoned Heart", rig.Host.Echoed);
        Assert.Equal(FixtureJson, File.ReadAllText(rig.CachePath));
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheScriptEngineRegistersSpellInfo()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "genie-si-eng-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var engine = new ScriptEngine(dir, new TypeAheadSession(), sendCommand: _ => { }, echo: _ => { });
            Assert.Contains(engine.Extensions.Extensions, e => e is SpellInfoExtension);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
