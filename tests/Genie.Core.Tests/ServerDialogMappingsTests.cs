using System.IO;
using System.Linq;
using Genie.Core.Dialogs;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #156 Phase 1 — the per-profile Dialog Layout Mapping and the session
/// bookkeeping behind the first-seen prompt.
/// </summary>
public class ServerDialogMappingsTests
{
    // ── Resolution ───────────────────────────────────────────────────────────

    [Fact]
    public void AnUnmappedDialogBuffersAndAsks()
    {
        var d = new ServerDialogMappings().Resolve("bank_debt");

        Assert.True(d.NeedsPrompt);
        Assert.False(d.ShouldRender);        // buffer until the user answers
        Assert.Equal(ServerDialogMode.AskLater, d.Mode);
    }

    [Fact]
    public void AMappedDialogRendersWithoutAsking()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.NewWindow });

        var d = m.Resolve("bank_debt");
        Assert.False(d.NeedsPrompt);
        Assert.True(d.ShouldRender);
        Assert.True(d.AutoOpen);             // default for a fresh decision
    }

    [Fact]
    public void AnIgnoredDialogNeitherRendersNorAsks()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "spellChoose", Mode = ServerDialogMode.Ignore });

        var d = m.Resolve("spellChoose");
        Assert.False(d.ShouldRender);
        Assert.False(d.NeedsPrompt);
    }

    [Fact]
    public void AnExistingWindowMappingCarriesItsTarget()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping
        {
            Id = "bank_debt", Mode = ServerDialogMode.ExistingWindow, Target = "Bank",
        });

        var d = m.Resolve("bank_debt");
        Assert.True(d.ShouldRender);
        Assert.Equal("Bank", d.Target);
    }

    [Fact]
    public void AutoOpenOffStillRenders()
    {
        // Off means "populate silently, open from the Window menu" — not "hide".
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping
        {
            Id = "tdpPlanWindow", Mode = ServerDialogMode.NewWindow, AutoOpen = false,
        });

        var d = m.Resolve("tdpPlanWindow");
        Assert.True(d.ShouldRender);
        Assert.False(d.AutoOpen);
    }

    // ── The quick-bar default ────────────────────────────────────────────────

    [Fact]
    public void QuickBarDialogsAreIgnoredWithoutAsking()
    {
        // DR declares four of these in the login block. Without this default a
        // brand-new user meets four choosers before they have played a minute.
        var m = new ServerDialogMappings();

        foreach (var id in new[] { "quick-simu", "quick-char", "quick-blank", "quick-tip" })
        {
            var d = m.Resolve(id, location: "quickBar");
            Assert.False(d.NeedsPrompt);
            Assert.False(d.ShouldRender);
        }
    }

    [Fact]
    public void TheQuickBarDefaultIsOnlyADefault()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "quick-char", Mode = ServerDialogMode.NewWindow });

        Assert.True(m.Resolve("quick-char", location: "quickBar").ShouldRender);
    }

    [Fact]
    public void TheQuickBarDefaultDoesNotLeakToOtherLocations()
    {
        var m = new ServerDialogMappings();

        Assert.True(m.Resolve("bank_debt", location: "force-center").NeedsPrompt);
        Assert.True(m.Resolve("befriend",  location: "right").NeedsPrompt);
    }

    // ── Another character's injuries (public #263) ───────────────────────────

    /// <summary>The Healing window carries this dialog now, so it fills in
    /// hidden by default: rendered (its content reaches the Healing window),
    /// never auto-opened, never shown on first sight, and never asked about.</summary>
    [Theory]
    [InlineData("injuries-123")]
    [InlineData("injuries-10224090")]
    [InlineData("INJURIES-1")]
    public void Other_character_injuries_fill_in_quietly_without_asking(string id)
    {
        var m = new ServerDialogMappings();

        var d = m.Resolve(id, location: "center");

        Assert.False(d.NeedsPrompt);
        Assert.True(d.ShouldRender);
        Assert.False(d.AutoOpen);
        Assert.True(d.CreateHidden);
        Assert.False(m.Resolve(id).NeedsPrompt);      // still no prompt on the next delta
    }

    [Fact]
    public void A_stored_injuries_mapping_still_wins()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "injuries-123", Mode = ServerDialogMode.WhereDrProposes, AutoOpen = true });

        var d = m.Resolve("injuries-123", location: "center");

        Assert.Equal(ServerDialogMode.WhereDrProposes, d.Mode);
        Assert.True(d.AutoOpen);
        Assert.False(d.CreateHidden);
    }

    /// <summary>The quiet default is only the numbered form: the player's own
    /// <c>injuries</c> (the Injuries panel's feed) and look-alike ids are
    /// untouched.</summary>
    [Theory]
    [InlineData("injuries")]
    [InlineData("injuries-")]
    [InlineData("injuries-abc")]
    [InlineData("injuries-12x")]
    public void Only_the_numbered_injuries_dialog_is_quiet(string id)
    {
        Assert.False(ServerDialogMappings.OpensQuietlyByDefault(id));
        Assert.False(new ServerDialogMappings().Resolve(id).CreateHidden);
    }

    // ── The first-seen prompt ────────────────────────────────────────────────

    [Fact]
    public void APromptIsClaimedAtMostOncePerSession()
    {
        // dialogData arrives in bursts; a stack of identical choosers would be
        // worse than no prompt at all.
        var m = new ServerDialogMappings();

        Assert.True(m.TryClaimPrompt("bank_debt"));
        Assert.False(m.TryClaimPrompt("bank_debt"));
        Assert.False(m.TryClaimPrompt("bank_debt"));
    }

    [Fact]
    public void AClaimedPromptStopsResolveFromAskingAgain()
    {
        var m = new ServerDialogMappings();
        Assert.True(m.Resolve("bank_debt").NeedsPrompt);

        m.TryClaimPrompt("bank_debt");

        var d = m.Resolve("bank_debt");
        Assert.False(d.NeedsPrompt);         // the chooser is already up
        Assert.False(d.ShouldRender);        // still buffering
    }

    [Fact]
    public void AnAnsweredDialogIsNeverPromptedAgain()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.NewWindow });

        Assert.False(m.TryClaimPrompt("bank_debt"));
    }

    [Fact]
    public void AskLaterDefersForTheSessionInsteadOfPersisting()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "featRemove", Mode = ServerDialogMode.AskLater });

        var d = m.Resolve("featRemove");
        Assert.False(d.NeedsPrompt);
        Assert.False(d.ShouldRender);
        Assert.Null(m.Find("featRemove"));   // nothing recorded
        Assert.Empty(m.All());
    }

    [Fact]
    public void ANewSessionAsksAgainAboutADeferredDialog()
    {
        var m = new ServerDialogMappings();
        m.DeferForSession("featRemove");
        m.TryClaimPrompt("featRemove");

        m.ResetSession();

        Assert.True(m.Resolve("featRemove").NeedsPrompt);
        Assert.True(m.TryClaimPrompt("featRemove"));
    }

    [Fact]
    public void ResetSessionKeepsRealDecisions()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.Ignore });

        m.ResetSession();

        Assert.False(m.Resolve("bank_debt").NeedsPrompt);
        Assert.Single(m.All());
    }

    [Fact]
    public void DecidingClearsAnEarlierDeferral()
    {
        var m = new ServerDialogMappings();
        m.DeferForSession("bank_debt");
        m.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.NewWindow });

        Assert.True(m.Resolve("bank_debt").ShouldRender);
    }

    [Fact]
    public void RemovingAMappingMakesTheDialogAskAgain()
    {
        // The settings grid's re-prompt action.
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.Ignore });

        Assert.True(m.Remove("bank_debt"));

        Assert.True(m.Resolve("bank_debt").NeedsPrompt);
        Assert.True(m.TryClaimPrompt("bank_debt"));
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [Fact]
    public void MappingsSurviveARoundTrip()
    {
        var path = TempPath();
        try
        {
            var saved = new ServerDialogMappings();
            saved.Set(new ServerDialogMapping
            {
                Id = "bank_debt", Mode = ServerDialogMode.ExistingWindow,
                Target = "Bank", AutoOpen = false, Title = "Provincial Debt",
            });
            saved.Set(new ServerDialogMapping { Id = "spellChoose", Mode = ServerDialogMode.Ignore });
            Assert.True(saved.Save(path));

            var loaded = new ServerDialogMappings();
            Assert.True(loaded.Load(path));

            var bank = loaded.Find("bank_debt")!;
            Assert.Equal(ServerDialogMode.ExistingWindow, bank.Mode);
            Assert.Equal("Bank", bank.Target);
            Assert.False(bank.AutoOpen);
            Assert.Equal("Provincial Debt", bank.Title);
            Assert.Equal(ServerDialogMode.Ignore, loaded.Find("spellChoose")!.Mode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ModesArePersistedByNameNotOrdinal()
    {
        // Reordering the enum must not silently turn every Ignore into a window.
        var path = TempPath();
        try
        {
            var m = new ServerDialogMappings();
            m.Set(new ServerDialogMapping { Id = "d", Mode = ServerDialogMode.Ignore });
            m.Save(path);

            Assert.Contains("Ignore", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DeferralsAreNotPersisted()
    {
        var path = TempPath();
        try
        {
            var m = new ServerDialogMappings();
            m.DeferForSession("featRemove");
            m.Save(path);

            var loaded = new ServerDialogMappings();
            loaded.Load(path);
            Assert.True(loaded.Resolve("featRemove").NeedsPrompt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ATornFileDoesNotWipeTheUsersDecisions()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not valid json");

            var m = new ServerDialogMappings();
            m.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.Ignore });

            Assert.False(m.Load(path));
            Assert.Single(m.All());
            Assert.Equal(ServerDialogMode.Ignore, m.Find("bank_debt")!.Mode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadingAMissingFileIsNotAnError()
    {
        var m = new ServerDialogMappings();
        Assert.False(m.Load(Path.Combine(Path.GetTempPath(), "no-such-dialogmappings.json")));
        Assert.Empty(m.All());
    }

    [Fact]
    public void SavingCreatesTheProfileDirectory()
    {
        var dir  = Path.Combine(Path.GetTempPath(), "genie5-dlg-" + Path.GetRandomFileName());
        var path = Path.Combine(dir, ServerDialogMappings.FileName);
        try
        {
            var m = new ServerDialogMappings();
            m.Set(new ServerDialogMapping { Id = "d", Mode = ServerDialogMode.NewWindow });

            Assert.True(m.Save(path));
            Assert.True(File.Exists(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EntriesWithoutAnIdAreDropped()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """[{ "Id": "", "Mode": "Ignore" }, { "Id": "real", "Mode": "Ignore" }]""");

            var m = new ServerDialogMappings();
            Assert.True(m.Load(path));
            Assert.Equal("real", Assert.Single(m.All()).Id);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AllIsOrderedById()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "zeta",  Mode = ServerDialogMode.Ignore });
        m.Set(new ServerDialogMapping { Id = "alpha", Mode = ServerDialogMode.Ignore });

        Assert.Equal(new[] { "alpha", "zeta" }, m.All().Select(x => x.Id));
    }

    [Fact]
    public void AReturnedMappingIsACopy()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "d", Mode = ServerDialogMode.NewWindow });

        m.Find("d")!.Mode = ServerDialogMode.Ignore;

        Assert.Equal(ServerDialogMode.NewWindow, m.Find("d")!.Mode);
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "genie5-dlgmap-" + Path.GetRandomFileName() + ".json");
}
