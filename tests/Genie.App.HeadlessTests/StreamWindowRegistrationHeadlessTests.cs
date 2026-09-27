using System;
using System.IO;
using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #260 — the stream windows DR declares with <c>ifClosed=''</c> (OOC,
/// Conversation, Group) are registered dock tools: hidden in a fresh layout,
/// opened by the Window-menu toggle into the stream group, and present in the
/// windowed-mode (MDI) registry too.
/// </summary>
public sealed class StreamWindowRegistrationHeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "genie_streamreg_" + Guid.NewGuid().ToString("N"));

    public StreamWindowRegistrationHeadlessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [AvaloniaTheory]
    [InlineData("ooc")]
    [InlineData("conversation")]
    [InlineData("group")]
    public void Registered_hidden_by_default_and_opened_by_its_toggle(string id)
    {
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        var f  = (GenieDockFactory)vm.DockFactory!;

        Assert.Contains(id, f.ToolIds);
        Assert.False(f.IsToolVisible(id));

        var toggle = id switch
        {
            "ooc"          => vm.ToggleOocCommand,
            "conversation" => vm.ToggleConversationCommand,
            _              => vm.ToggleGroupCommand,
        };
        toggle.Execute().Subscribe();

        Assert.True(f.IsToolVisible(id));
        var visible = id switch
        {
            "ooc"          => vm.OocVisible,
            "conversation" => vm.ConversationVisible,
            _              => vm.GroupVisible,
        };
        Assert.True(visible);
    }

    [AvaloniaTheory]
    [InlineData("conversation")]
    [InlineData("group")]
    public void Windowed_mode_registers_them_too(string id)
    {
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        var f  = (GenieDockFactory)vm.DockFactory!;

        f.BuildMdiLayout();

        Assert.Contains(id, f.ToolIds);
        Assert.False(f.IsToolVisible(id));
    }
}
