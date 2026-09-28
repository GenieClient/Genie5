using System;
using System.IO;
using System.Linq;
using Genie.Core.Layout;
using Genie.Core.Persistence;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #260 — registering the Conversation (and Group) window would bring
/// persisted <c>IfClosed</c> values that named it back to life. Before the
/// window existed those values were dead and <see cref="IfClosedResolver"/>
/// sent the text to Main; profiles in the wild carry talk / whispers
/// <c>IfClosed='conversation'</c> (DR's and Genie 4's own default), so the
/// upgrade would quietly move their speech into a panel nobody opened.
///
/// <para>The rewrite runs once per row: a saved row is stamped with
/// <see cref="WindowSettingsStore.IfClosedRevision"/>, and a row at that
/// revision is taken as written — so choosing Conversation after upgrading
/// sticks.</para>
/// </summary>
public class IfClosedDeadTargetMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "genie_ifclosed_mig_" + Guid.NewGuid().ToString("N"));
    private string WindowsJson => Path.Combine(_dir, "windows.json");

    public IfClosedDeadTargetMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static WindowSettingsStore NewStore()
    {
        var s = new WindowSettingsStore();
        foreach (var id in new[] { "talk", "whispers", "combat", "log", "conversation", "group" })
            s.Register(id, id);
        return s;
    }

    private static WindowSettingsStore Load(string path)
    {
        var store = NewStore();
        foreach (var m in new PersistenceService().LoadWindowSettings(path)) store.Apply(m);
        return store;
    }

    /// <summary>A windows.json as a pre-#260 build wrote it: no revision field.</summary>
    private void WriteLegacyFile(params (string Id, string? IfClosed)[] rows)
    {
        var json = "[" + string.Join(",", rows.Select(r =>
            $"{{\"Id\":\"{r.Id}\",\"EchoToMain\":false,\"HasIfClosed\":true,\"IfClosed\":" +
            (r.IfClosed is null ? "null" : $"\"{r.IfClosed}\"") + "}")) + "]";
        File.WriteAllText(WindowsJson, json);
    }

    [Fact]
    public void Legacy_conversation_target_keeps_what_it_did_before_main()
    {
        // Not "log": with EchoToMain off, the dead target's Main fallback was the
        // ONLY place a closed Talk panel's speech showed (DR's bare main re-send
        // is display-suppressed as DuplicateEcho). Log would move it out of Game.
        WriteLegacyFile(("talk", "conversation"), ("whispers", "conversation"));

        var store = Load(WindowsJson);

        Assert.Null(store.Get("talk").IfClosed);       // null = Main
        Assert.Null(store.Get("whispers").IfClosed);
    }

    [Fact]
    public void Legacy_group_target_keeps_what_it_did_before_main()
    {
        WriteLegacyFile(("combat", "group"));

        Assert.Null(Load(WindowsJson).Get("combat").IfClosed);   // null = Main
    }

    [Fact]
    public void Other_legacy_targets_are_untouched()
    {
        WriteLegacyFile(("combat", "log"), ("talk", ""), ("whispers", null));

        var store = Load(WindowsJson);

        Assert.Equal("log", store.Get("combat").IfClosed);
        Assert.Equal("",    store.Get("talk").IfClosed);
        Assert.Null(store.Get("whispers").IfClosed);
    }

    [Fact]
    public void Migration_is_idempotent_across_repeated_loads_without_a_save()
    {
        WriteLegacyFile(("talk", "conversation"));

        Assert.Null(Load(WindowsJson).Get("talk").IfClosed);
        Assert.Null(Load(WindowsJson).Get("talk").IfClosed);
    }

    [Fact]
    public void Saved_rows_are_stamped_and_the_rewrite_does_not_run_again()
    {
        WriteLegacyFile(("talk", "conversation"));
        var store = Load(WindowsJson);
        new PersistenceService().SaveWindowSettings(WindowsJson, store);

        var rows = new PersistenceService().LoadWindowSettings(WindowsJson);
        Assert.All(rows, r => Assert.Equal(WindowSettingsStore.IfClosedRevision, r.IfClosedRevision));
        var talk = rows.Single(r => r.Id == "talk");
        Assert.True(talk.HasIfClosed);
        Assert.Null(talk.IfClosed);
        Assert.Null(Load(WindowsJson).Get("talk").IfClosed);   // reload: still Main, not the default log
    }

    [Fact]
    public void Conversation_chosen_after_the_upgrade_is_kept()
    {
        // One-time: migrate, save, then the user deliberately picks Conversation.
        WriteLegacyFile(("talk", "conversation"));
        var store = Load(WindowsJson);
        new PersistenceService().SaveWindowSettings(WindowsJson, store);

        store.Get("talk").IfClosed = "conversation";
        new PersistenceService().SaveWindowSettings(WindowsJson, store);

        Assert.Equal("conversation", Load(WindowsJson).Get("talk").IfClosed);
        Assert.Equal("conversation", Load(WindowsJson).Get("talk").IfClosed);
    }

    [Fact]
    public void A_row_held_for_a_late_window_migrates_when_it_registers()
    {
        // Held rows are written back verbatim (still un-stamped), so the
        // rewrite must also apply when the window registers later.
        var store = new WindowSettingsStore();
        store.Apply(new WindowSettingsPersistenceModel { Id = "talk", HasIfClosed = true, IfClosed = "conversation" });
        store.Register("talk", "Talk");

        Assert.Null(store.Get("talk").IfClosed);
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("group")]
    public void New_windows_ship_with_DRs_declared_defaults(string id)
    {
        var s = new WindowSettingsStore().Register(id, id);

        Assert.Equal("", s.IfClosed);    // drop when closed
        Assert.False(s.EchoToMain);
    }

    [Fact]
    public void Migrated_talk_resolves_to_main_not_to_the_new_window()
    {
        WriteLegacyFile(("talk", "conversation"));
        var store = Load(WindowsJson);

        // Talk closed; Log and Conversation both open. Before the upgrade the
        // dead target sent it to Main, and it must still go there.
        var d = IfClosedResolver.Resolve("talk", store, id => id is "log" or "conversation");

        Assert.Equal(IfClosedSinkKind.Main, d.Kind);
    }
}
