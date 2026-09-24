using System;
using System.IO;
using System.Linq;
using Genie.App.ViewModels;
using Genie.App.Views;
using Genie.Core.Dialogs;
using Genie.Core.Layout;
using Genie.Core.Profiles;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #156 — Configuration → Layout → Server Dialogs. The grid edits the
/// per-profile <c>dialogmappings.json</c>; offline (or for a profile that is
/// not connected) it works on a draft read from and written back to that
/// profile's own file.
/// </summary>
public class ServerDialogsConfigTests : IDisposable
{
    private readonly string       _root;
    private readonly ProfileStore _store = new();

    public ServerDialogsConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_dlgcfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ProfileDir(string name) =>
        Genie.Core.Config.GenieConfig.ProfileDirFor(_root, name, "ACCT");

    private ConfigurationViewModel Build(params string[] names)
    {
        foreach (var n in names) _store.Add(n, "host", 11024, "ACCT", "", characterName: n);
        return new ConfigurationViewModel(
            core:             null,
            configRoot:       Path.Combine(_root, "Config"),
            profiles:         _store,
            connectedProfile: null,
            windowSettings:   new WindowSettingsStore());
    }

    private void Seed(string name, params ServerDialogMapping[] maps)
    {
        var m = new ServerDialogMappings();
        foreach (var x in maps) m.Set(x);
        Assert.True(m.Save(Path.Combine(ProfileDir(name), ServerDialogMappings.FileName)));
    }

    [Fact]
    public void The_draft_reads_the_selected_profiles_file()
    {
        Seed("Renucci", new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.Ignore, Title = "Bank" });
        var vm = Build("Renucci");

        var maps = vm.DialogMappings!.All();
        Assert.Equal("bank_debt", Assert.Single(maps).Id);
    }

    [Fact]
    public void An_edit_saves_back_to_that_profiles_file()
    {
        Seed("Renucci", new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.Ignore });
        var vm = Build("Renucci");

        var m = vm.DialogMappings!.Find("bank_debt")!;
        m.Mode = ServerDialogMode.NewWindow;
        m.AutoOpen = false;
        vm.DialogMappings.Set(m);
        vm.OnDialogMappingsChanged();

        var reread = new ServerDialogMappings();
        Assert.True(reread.Load(Path.Combine(ProfileDir("Renucci"), ServerDialogMappings.FileName)));
        var saved = reread.Find("bank_debt")!;
        Assert.Equal(ServerDialogMode.NewWindow, saved.Mode);
        Assert.False(saved.AutoOpen);
    }

    /// <summary>Switching the profile picker must not show (or save) the
    /// previous profile's answers.</summary>
    [Fact]
    public void Switching_profiles_switches_tables()
    {
        Seed("Renucci", new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.Ignore });
        var vm = Build("Renucci", "Naper");

        vm.SelectedProfile = vm.Profiles.Single(p => p.Name == "Naper");

        Assert.Empty(vm.DialogMappings!.All());
    }

    [Fact]
    public void Rows_read_in_the_choosers_words()
    {
        var own   = ServerDialogsPanel.ToRow(new ServerDialogMapping { Id = "a", Mode = ServerDialogMode.NewWindow, Title = "Bank" });
        var never = ServerDialogsPanel.ToRow(new ServerDialogMapping { Id = "b", Mode = ServerDialogMode.Ignore });
        var shut  = ServerDialogsPanel.ToRow(new ServerDialogMapping { Id = "c", Mode = ServerDialogMode.WhereDrProposes, AutoOpen = false });

        Assert.Equal(("Bank", "Its own window", "✓"), (own.Title, own.Where, own.AutoOpen));
        Assert.Equal(("b", "Never show it", "—"), (never.Title, never.Where, never.AutoOpen));   // untitled falls back to id
        Assert.Equal(("Where DR suggests", "✗"), (shut.Where, shut.AutoOpen));
    }
}
