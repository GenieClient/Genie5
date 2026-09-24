using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Config;
using Genie.Core.Dialogs;
using Genie.Core.Layout;
using Genie.Core.Persistence;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #156, the settings half: the mapping change notifications the
/// settings grid relies on, the <c>serverdialogs</c> master toggle, and
/// per-dialog window settings surviving a session in which the dialog never
/// opened.
/// </summary>
public class ServerDialogSettingsTests
{
    // ── Mapping change notifications ─────────────────────────────────────────

    [Fact]
    public void SetRaisesChangedWithTheDialogId()
    {
        var m = new ServerDialogMappings();
        var seen = new List<string>();
        m.Changed += seen.Add;

        m.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.Ignore });

        Assert.Equal(new[] { "bank_debt" }, seen);
    }

    /// <summary>"Ask me later" defers for the session; it is not a decision,
    /// so there is nothing for a window to re-apply.</summary>
    [Fact]
    public void AskLaterIsNotAChange()
    {
        var m = new ServerDialogMappings();
        var seen = new List<string>();
        m.Changed += seen.Add;

        m.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.AskLater });

        Assert.Empty(seen);
    }

    [Fact]
    public void RemoveRaisesChangedOnlyWhenSomethingWasRemoved()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.NewWindow });
        var seen = new List<string>();
        m.Changed += seen.Add;

        Assert.False(m.Remove("never-mapped"));
        Assert.True(m.Remove("bank_debt"));

        Assert.Equal(new[] { "bank_debt" }, seen);
    }

    [Fact]
    public void LoadRaisesReloadedNotChanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"g5-dlgmap-{Guid.NewGuid():N}.json");
        try
        {
            var src = new ServerDialogMappings();
            src.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.Ignore });
            Assert.True(src.Save(path));

            var m = new ServerDialogMappings();
            var changed = 0; var reloaded = 0;
            m.Changed  += _ => changed++;
            m.Reloaded += () => reloaded++;

            Assert.True(m.Load(path));
            Assert.Equal(0, changed);
            Assert.Equal(1, reloaded);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Reconnecting as a character with no dialogmappings.json used to
    /// keep the previous character's table — which the next answer then saved
    /// into the new profile.</summary>
    [Fact]
    public void LoadingAMissingFileClearsThePreviousProfilesTable()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.Ignore });
        var reloaded = 0;
        m.Reloaded += () => reloaded++;

        Assert.False(m.Load(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json")));

        Assert.Empty(m.All());
        Assert.Equal(1, reloaded);
    }

    // ── serverdialogs master toggle ──────────────────────────────────────────

    private static GenieConfig NewConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "genie_dlgcfg_" + Guid.NewGuid().ToString("N"));
        var lds  = new Genie.Core.Runtime.LocalDirectoryService("GenieDlgCfgTest", root);
        lds.UseExplicitRoot(root);
        return new GenieConfig(lds);
    }

    [Fact]
    public void TheMasterToggleDefaultsOn()
    {
        Assert.True(NewConfig().ServerDialogs);
    }

    [Fact]
    public void ConfigServerDialogsOffNotifiesTheHost()
    {
        var cfg = NewConfig();
        var fields = new List<ConfigFieldUpdated>();
        cfg.ConfigChanged += fields.Add;

        cfg.SetSetting("serverdialogs", "off", showException: false);

        Assert.False(cfg.ServerDialogs);
        Assert.Contains(ConfigFieldUpdated.ServerDialogs, fields);
    }

    [Fact]
    public void TheMasterToggleIsSavedAndListed()
    {
        var cfg = NewConfig();
        cfg.ServerDialogs = false;
        var pair = cfg.ToConfigPairs().Single(p => p.Key == "serverdialogs");
        Assert.Equal("False", pair.Value);
        Assert.Contains(GenieConfig.ConfigCategories,
            c => c.Category == "Master Toggles" && c.Keys.Contains("serverdialogs"));
    }

    // ── Per-dialog window settings (fonts) ───────────────────────────────────

    /// <summary>A dialog registers when DR first sends it — after windows.json
    /// has loaded. Its persisted row is held and applied at registration
    /// instead of being dropped, which is what made dialog fonts session-only.</summary>
    [Fact]
    public void ARowForANotYetRegisteredWindowIsAppliedWhenItRegisters()
    {
        var store = new WindowSettingsStore();
        store.Apply(new WindowSettingsPersistenceModel
        {
            Id = "serverdlg:bank_debt", FontFamily = "Georgia", FontSize = 18,
        });

        var s = store.Register("serverdlg:bank_debt", "Bank", "Inter", 14);

        Assert.Equal("Georgia", s.FontFamily);
        Assert.Equal(18, s.FontSize);
        Assert.Empty(store.Held);
    }

    [Fact]
    public void RegisterWithoutAHeldRowUsesTheWindowSpecificDefault()
    {
        var s = new WindowSettingsStore().Register("serverdlg:bank_debt", "Bank", "Inter", 14);

        Assert.Equal("Inter", s.FontFamily);
        Assert.Equal(14, s.FontSize);
    }

    /// <summary>A session in which the dialog never opens must not erase its
    /// row when something else saves windows.json.</summary>
    [Fact]
    public void AHeldRowSurvivesASaveFromASessionWhereTheWindowNeverOpened()
    {
        var path = Path.Combine(Path.GetTempPath(), $"g5-winheld-{Guid.NewGuid():N}.json");
        try
        {
            var p = new PersistenceService();
            var store = new WindowSettingsStore();
            store.Register("talk", "Talk");
            store.Apply(new WindowSettingsPersistenceModel
            {
                Id = "serverdlg:bank_debt", FontFamily = "Georgia", FontSize = 18,
            });
            p.SaveWindowSettings(path, store);

            var rows = p.LoadWindowSettings(path);
            Assert.Contains(rows, r => r.Id == "talk");
            var held = Assert.Single(rows, r => r.Id == "serverdlg:bank_debt");
            Assert.Equal("Georgia", held.FontFamily);
            Assert.Equal(18, held.FontSize);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Profile windows.json loads after the global one; the later row
    /// wins for a held id exactly as it would for a registered one.</summary>
    [Fact]
    public void TheLaterLoadLayerWinsForAHeldRow()
    {
        var store = new WindowSettingsStore();
        store.Apply(new WindowSettingsPersistenceModel { Id = "serverdlg:x", FontFamily = "Global", FontSize = 12 });
        store.Apply(new WindowSettingsPersistenceModel { Id = "serverdlg:x", FontFamily = "Profile", FontSize = 16 });

        var s = store.Register("serverdlg:x", "X", "Inter", 14);

        Assert.Equal("Profile", s.FontFamily);
    }
}
