using System;
using System.IO;
using System.Linq;
using Genie.App.ViewModels;
using Genie.Core.Layout;
using Genie.Core.Profiles;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #350 — while no profile is connected, the Configuration dialog edits
/// a DRAFT copy of the rules rather than the live engines, and nothing in the
/// dialog said so.
///
/// <para>That matters because of what people actually do first: open Settings,
/// add a couple of triggers and macros, and try them. Offline that produced
/// zero feedback and zero effect, which is indistinguishable from the feature
/// being broken — and is exactly how it was reported. Nothing is lost (draft
/// edits are saved and load at connect), so this is a visibility fix, not a
/// behaviour change.</para>
///
/// <para>These tests cover the label and banner the dialog now shows, and —
/// more importantly — that both are driven by the SAME predicate the engine
/// accessors use, so the badge cannot disagree with where edits really go.</para>
/// </summary>
public class ConfigurationDraftModeTests : IDisposable
{
    private readonly string       _root;
    private readonly ProfileStore _store = new();

    public ConfigurationDraftModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_cfgdraft_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Build the dialog VM over <paramref name="names"/> as saved
    /// profiles, with <paramref name="connectedIndex"/> connected (-1 = offline,
    /// which is how every panel starts before the first connection).</summary>
    private ConfigurationViewModel Build(int connectedIndex, params string[] names)
    {
        var made = names
            .Select(n => _store.Add(n, "host", 11024, "ACCT", "", characterName: n))
            .ToList();

        return new ConfigurationViewModel(
            core:             null,
            configRoot:       Path.Combine(_root, "Config"),
            profiles:         _store,
            connectedProfile: connectedIndex >= 0 ? made[connectedIndex] : null,
            windowSettings:   new WindowSettingsStore());
    }

    private ConnectionProfile Pick(ConfigurationViewModel vm, string name) =>
        vm.Profiles.Single(p => p.Name == name);

    // ── Offline: every panel is in draft mode, and now says so ───────────────

    [Fact]
    public void With_nothing_connected_the_label_says_draft()
    {
        var vm = Build(connectedIndex: -1, "Renucci");

        Assert.False(vm.IsEditingConnectedProfile);
        Assert.Equal("Editing: Renucci (draft)", vm.EditingLabel);
    }

    /// <summary>The banner names the CONSEQUENCE, not the state. "Draft" on its
    /// own means nothing to someone about to press the key they just bound.</summary>
    [Fact]
    public void The_draft_banner_says_what_will_actually_happen()
    {
        var vm = Build(connectedIndex: -1, "Renucci");

        Assert.Contains("NOT active in this session",   vm.DraftNotice);
        Assert.Contains("next time you connect",        vm.DraftNotice);
        Assert.Contains("Triggers, macros and aliases", vm.DraftNotice);
    }

    // ── Connected, on the connected profile: live, and no banner ─────────────

    [Fact]
    public void Editing_the_connected_profile_is_live_and_shows_no_banner()
    {
        var vm = Build(connectedIndex: 0, "Renucci");

        Assert.True(vm.IsEditingConnectedProfile);
        Assert.Equal("Editing: Renucci (live)", vm.EditingLabel);
        Assert.Equal("", vm.DraftNotice);
    }

    // ── The sharp edge in the other direction ────────────────────────────────

    /// <summary>Selecting a different profile WHILE connected silently switches
    /// from live editing to draft mid-session. That direction had the same
    /// absence of signal and is covered by the same banner.</summary>
    [Fact]
    public void Switching_to_another_profile_while_connected_shows_the_draft_banner()
    {
        var vm = Build(connectedIndex: 0, "Renucci", "Naper");
        Assert.True(vm.IsEditingConnectedProfile);

        vm.SelectedProfile = Pick(vm, "Naper");

        Assert.False(vm.IsEditingConnectedProfile);
        Assert.Equal("Editing: Naper (draft)", vm.EditingLabel);
        Assert.NotEqual("", vm.DraftNotice);
    }

    /// <summary>…and switching back clears it, so the banner tracks the live
    /// state rather than latching on the first draft selection.</summary>
    [Fact]
    public void Switching_back_to_the_connected_profile_clears_the_banner()
    {
        var vm = Build(connectedIndex: 0, "Renucci", "Naper");

        vm.SelectedProfile = Pick(vm, "Naper");
        vm.SelectedProfile = Pick(vm, "Renucci");

        Assert.True(vm.IsEditingConnectedProfile);
        Assert.Equal("", vm.DraftNotice);
    }
}
