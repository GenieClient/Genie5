using System.Linq;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Genie.App.Controls;
using Genie.App.Highlighting;
using Genie.App.ViewModels;
using Genie.Core.Events;
using Genie.Core.Layout;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #362 — <c>{display:command}</c> inline links reach every output surface
/// (Game window game text and echoes, stream windows, script/plugin windows) and
/// render as clickable spans in both Game-window renderers.
/// </summary>
public class InlineClickMarkupHeadlessTests
{
    private const string Menu = "Menu: {Look:look} or {Inventory:inventory}";

    private static string Linked(TextLine l, int i) => l.Text.Substring(l.Links![i].Start, l.Links[i].Length);

    private static int LinkInlines(TextLine l) => l.Inlines.OfType<InlineUIContainer>().Count();

    [AvaloniaFact]
    public void An_echo_line_collapses_the_markup_and_renders_two_clickable_links()
    {
        var vm = new GameTextViewModel();
        vm.AddSystemLine(Menu);

        var line = vm.Lines.Single();
        Assert.True(line.IsEcho);
        Assert.Equal("Menu: Look or Inventory", line.Text);
        Assert.Equal(new[] { "look", "inventory" }, line.Links!.Select(l => l.Command));
        Assert.Equal(("Look", "Inventory"), (Linked(line, 0), Linked(line, 1)));
        Assert.Equal(2, LinkInlines(line));
    }

    [AvaloniaFact]
    public void A_styled_echo_keeps_its_colour_between_the_links()
    {
        var vm = new GameTextViewModel();
        vm.AddEcho(Menu, "Yellow", mono: false);

        var line = vm.Lines.Single();
        Assert.Equal(2, LinkInlines(line));
        var runs = line.Inlines.OfType<Run>().ToList();
        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.NotNull(r.Foreground));   // the echo colour, not the default
    }

    [AvaloniaFact]
    public void Timestamped_lines_shift_the_links_with_the_prefix()
    {
        var vm = new GameTextViewModel { Settings = new WindowSettings { Timestamp = true } };
        vm.AddSystemLine(Menu);
        vm.AddEcho(Menu, "Yellow", mono: false);

        foreach (var line in vm.Lines)
            Assert.Equal(("Look", "Inventory"), (Linked(line, 0), Linked(line, 1)));
    }

    [AvaloniaFact]
    public void Game_text_keeps_its_parser_spans_aligned_after_the_collapse()
    {
        var vm = new GameTextViewModel();
        // "orc" is bold at 24..27 in the raw text.
        vm.EchoStreamToMain("{Attack:attack orc} the orc", bolds: new[] { new BoldSpan(24, 3) });

        var line = vm.Lines.Single();
        Assert.Equal("Attack the orc", line.Text);
        Assert.Equal("orc", line.Text.Substring(line.BoldSpans![0].Start, line.BoldSpans[0].Length));
        Assert.Equal("attack orc", line.Links!.Single().Command);
    }

    [AvaloniaFact]
    public void Script_windows_and_stream_windows_get_the_links_too()
    {
        var panel = new PluginWindowViewModel("Menu");
        panel.AppendLine(Menu);
        panel.SetContent("Header\n" + Menu);
        Assert.All(new[] { panel.Lines.Last() }, l => Assert.Equal(2, l.Links!.Count));
        Assert.Equal("Menu: Look or Inventory", panel.Lines.Last().Text);

        var stream = new StreamBuffer("Talk");
        stream.Add(Menu);
        Assert.Equal(new[] { "look", "inventory" }, stream.Lines.Single().Links!.Select(l => l.Command));
    }

    [AvaloniaFact]
    public void The_editor_renderer_offers_echo_links_to_its_link_generator()
    {
        var vm = new GameTextViewModel();
        vm.AddSystemLine(Menu);
        var entry = new GameLineEntry(vm.Lines.Single());

        Assert.Equal(new[] { "look", "inventory" }, entry.Links.Select(l => l.Command));
    }

    [AvaloniaFact]
    public void With_links_switched_off_an_echo_renders_as_plain_text()
    {
        var saved = DefaultHighlights.LinksEnabled;
        try
        {
            DefaultHighlights.LinksEnabled = false;
            var vm = new GameTextViewModel();
            vm.AddSystemLine(Menu);

            var line = vm.Lines.Single();
            Assert.Equal("Menu: Look or Inventory", line.Text);   // still collapsed, as in Genie 4
            Assert.Equal(0, LinkInlines(line));
            Assert.Empty(new GameLineEntry(line).Links);
        }
        finally { DefaultHighlights.LinksEnabled = saved; }
    }

    [AvaloniaFact]
    public void An_ordinary_echo_is_unchanged()
    {
        var vm = new GameTextViewModel();
        vm.AddSystemLine("[script] t started");

        var line = vm.Lines.Single();
        Assert.Null(line.Links);
        Assert.Single(line.Inlines);
    }
}
