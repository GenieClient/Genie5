using System.Collections.Generic;
using System.Linq;
using Genie.App.ViewModels;
using Genie.Core.Events;
using Xunit;

namespace Genie.App.Tests;

/// <summary>Public #336 — DR container contents routed to a window of their own.</summary>
public class ContainerWindowRouterTests
{
    private readonly Dictionary<string, PluginWindowViewModel> _windows = new();

    private ContainerWindowRouter Router() => new(
        name => _windows.TryGetValue(name, out var w) ? w : _windows[name] = new PluginWindowViewModel(name),
        name => _windows.GetValueOrDefault(name));

    private static TextEvent Item(string text, string stream = "container:stow") => new(stream, text);

    [Fact]
    public void Items_land_in_a_window_named_by_the_container_title()
    {
        var r = Router();
        r.OnContainer(new ContainerEvent("stow", "My Backpack", "#1"));

        Assert.True(r.OnText(Item("In the backpack:")));
        Assert.True(r.OnText(Item(" a coil of rope")));

        var w = Assert.Single(_windows).Value;
        Assert.Equal("My Backpack", w.Title);
        Assert.Equal(new[] { "In the backpack:", " a coil of rope" }, w.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Without_a_title_the_window_is_named_by_the_id()
    {
        var r = Router();
        r.OnText(Item("a rock", "container:pouch"));

        Assert.Equal("pouch", Assert.Single(_windows).Key);
    }

    [Fact]
    public void Without_a_title_the_header_line_names_the_window_as_on_DR_Prime()
    {
        // Prime sends no <container title=…>; the burst's own header names it.
        var r = Router();
        r.OnText(Item("In the backpack:"));
        r.OnText(Item(" a coil of rope"));

        var w = Assert.Single(_windows);
        Assert.Equal("Backpack", w.Key);
        Assert.Equal(2, w.Value.Lines.Count);
    }

    [Fact]
    public void A_server_title_wins_over_the_header()
    {
        var r = Router();
        r.OnContainer(new ContainerEvent("stow", "My Backpack", "#1"));
        r.OnText(Item("In the backpack:"));

        Assert.Equal("My Backpack", Assert.Single(_windows).Key);
    }

    [Fact]
    public void A_clear_empties_the_window_so_a_refill_replaces_the_contents()
    {
        var r = Router();
        r.OnContainer(new ContainerEvent("stow", "My Backpack", "#1"));
        r.OnText(Item("a coil of rope"));

        r.OnClear(new ContainerClearEvent("stow"));
        r.OnText(Item("a chamois cloth"));

        Assert.Equal(new[] { "a chamois cloth" }, _windows["My Backpack"].Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_clear_for_a_container_never_seen_creates_nothing()
    {
        Router().OnClear(new ContainerClearEvent("stow"));

        Assert.Empty(_windows);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("inv")]
    [InlineData("talk")]
    public void Other_streams_are_not_the_routers(string stream)
    {
        Assert.False(Router().OnText(Item("x", stream)));
        Assert.Empty(_windows);
    }
}
