using System;
using System.IO;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #419: whatever sends quit/exit (typed, or a script's <c>put quit</c>),
/// the drop that follows must not auto-reconnect. The view-model notes every
/// command written to the game (<c>GenieCore.SentCommands</c>). As in Genie 4
/// (<c>m_bManualDisconnect</c>), a quit holds until the next connect: another
/// script still sending after it must not re-arm reconnect.
/// </summary>
public class ScriptQuitReconnectTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "genie_quit_" + Guid.NewGuid().ToString("N"));

    public ScriptQuitReconnectTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void A_sent_quit_marks_the_exit_as_intentional_until_the_next_connect()
    {
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        Assert.False(vm.IntentionalGameExit);

        vm.NoteCommandSentToGame("look");
        Assert.False(vm.IntentionalGameExit);

        vm.NoteCommandSentToGame("quit");
        Assert.True(vm.IntentionalGameExit);

        vm.NoteCommandSentToGame("exp");           // another script still running
        Assert.True(vm.IntentionalGameExit);
    }

    [Fact]
    public void Genie_4_prefix_rule_counts_abbreviations()
    {
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        vm.NoteCommandSentToGame("EXI");
        Assert.True(vm.IntentionalGameExit);
    }
}
