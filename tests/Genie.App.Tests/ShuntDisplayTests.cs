using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Events;
using Genie.Core.Layout;
using Genie.Core.Shunts;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #248 — <c>#shunt</c> in the main-window display pipeline. Drives the
/// real <see cref="GameTextViewModel.Attach"/> subscription with a real
/// <see cref="GenieCore"/>, and a <see cref="GameTextViewModel.DeliverShunt"/>
/// built the way <c>MainWindowViewModel.DeliverShunt</c> builds it for stream
/// targets: <see cref="ShuntRouter"/> over the real
/// <see cref="StreamTabsViewModel"/> buffers and a settings store, with a
/// mutable open-panel set standing in for the dock. (Named script windows need
/// the dock itself; their routing is covered by ShuntRouterTests in Core.)
/// </summary>
public class ShuntDisplayTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public GenieCore Core { get; }
        public GameTextViewModel Main { get; } = new();
        public StreamTabsViewModel Tabs { get; } = new();
        public WindowSettingsStore Store { get; } = new();
        public List<(string Window, string Text)> Delivered { get; } = new();

        private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_shunt_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
            foreach (var id in new[] { "atmospherics", "talk", "log", "ooc" })
                Store.Register(id, id);

            Main.DeliverShunt = (window, line) =>
            {
                var d = ShuntRouter.Resolve(window,
                    isStream:   id => Tabs.TryGetBuffer(id) is not null,
                    isReserved: n => n.Equals("mapper", StringComparison.OrdinalIgnoreCase),
                    isOpen:     n => _open.Contains(n),
                    store:      Store);
                if (d.Kind != ShuntSinkKind.Stream || Tabs.TryGetBuffer(d.Target!) is not { } buf)
                    return false;
                buf.Add(line.Text, line.Bolds, line.Links, line.Presets);
                Delivered.Add((d.Target!, line.Text));
                return true;
            };
            Main.Attach(Core);
        }

        public void Open(string id) => _open.Add(id);
        public void Line(string text) => Core.PublishGameEventForTests(new TextEvent("main", text));

        public async ValueTask DisposeAsync()
        {
            await Core.DisposeAsync();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private const string Kitten = "A kitten pounces on a ball of yarn.";

    [Fact]
    public async Task A_move_shunt_takes_the_line_out_of_main_and_into_the_window()
    {
        await using var h = new Harness();
        h.Open("atmospherics");
        h.Core.Shunts.AddRule("^A kitten", "Atmospherics");

        h.Line(Kitten);
        h.Line("You feel fully rested.");

        Assert.Equal(Kitten, Assert.Single(h.Tabs.Atmospherics.Lines).Text);
        Assert.Equal("You feel fully rested.", Assert.Single(h.Main.Lines).Text);
    }

    [Fact]
    public async Task A_copy_shunt_shows_the_line_in_both()
    {
        await using var h = new Harness();
        h.Open("atmospherics");
        h.Core.Shunts.AddRule("^A kitten", "Atmospherics", copy: true);

        h.Line(Kitten);

        Assert.Equal(Kitten, Assert.Single(h.Tabs.Atmospherics.Lines).Text);
        Assert.Equal(Kitten, Assert.Single(h.Main.Lines).Text);
    }

    [Fact]
    public async Task A_moved_line_stays_in_main_when_the_target_is_closed()
    {
        await using var h = new Harness();
        // Atmospherics left closed; its default IfClosed (null) resolves to Main.
        h.Core.Shunts.AddRule("^A kitten", "Atmospherics");

        h.Line(Kitten);

        Assert.Empty(h.Tabs.Atmospherics.Lines);
        Assert.Equal(Kitten, Assert.Single(h.Main.Lines).Text);
    }

    [Fact]
    public async Task A_closed_target_follows_its_IfClosed_redirect()
    {
        await using var h = new Harness();
        h.Store.Get("atmospherics").IfClosed = "log";
        h.Open("log");
        h.Core.Shunts.AddRule("^A kitten", "Atmospherics");

        h.Line(Kitten);

        Assert.Equal(("log", Kitten), Assert.Single(h.Delivered));
        Assert.Empty(h.Main.Lines);
    }

    [Fact]
    public async Task A_closed_target_whose_IfClosed_drops_still_does_not_lose_the_line()
    {
        await using var h = new Harness();
        h.Store.Get("ooc").IfClosed = "";          // "disabled" — DR's own ooc default
        h.Core.Shunts.AddRule("^A kitten", "ooc");

        h.Line(Kitten);

        Assert.Equal(Kitten, Assert.Single(h.Main.Lines).Text);
    }

    [Fact]
    public async Task A_gagged_line_is_never_shunted()
    {
        await using var h = new Harness();
        h.Open("atmospherics");
        h.Core.Gags.AddRule("kitten");
        h.Core.Shunts.AddRule("^A kitten", "Atmospherics", copy: true);

        h.Line(Kitten);

        Assert.Empty(h.Tabs.Atmospherics.Lines);
        Assert.Empty(h.Main.Lines);
    }

    [Fact]
    public async Task The_shunt_pattern_sees_the_substituted_text()
    {
        await using var h = new Harness();
        h.Open("atmospherics");
        h.Core.Substitutes.AddRule("^A kitten", "A cat");
        h.Core.Shunts.AddRule("^A cat", "Atmospherics");

        h.Line(Kitten);

        Assert.Equal("A cat pounces on a ball of yarn.", Assert.Single(h.Tabs.Atmospherics.Lines).Text);
        Assert.Empty(h.Main.Lines);
    }

    [Fact]
    public async Task A_non_text_panel_target_keeps_the_line_in_main()
    {
        await using var h = new Harness();
        h.Core.Shunts.AddRule("^A kitten", "Mapper");

        h.Line(Kitten);

        Assert.Equal(Kitten, Assert.Single(h.Main.Lines).Text);
    }

    [Fact]
    public async Task An_inactive_class_turns_the_shunt_off()
    {
        await using var h = new Harness();
        h.Open("atmospherics");
        h.Core.Shunts.AddRule("^A kitten", "Atmospherics", className: "pets");
        h.Core.Classes.Set("pets", false);

        h.Line(Kitten);

        Assert.Empty(h.Tabs.Atmospherics.Lines);
        Assert.Equal(Kitten, Assert.Single(h.Main.Lines).Text);
    }

    [Fact]
    public async Task Hiding_game_text_in_main_does_not_starve_a_shunt_window()
    {
        await using var h = new Harness();
        h.Main.DisplaySettings = new Genie.App.Settings.DisplaySettings { ShowGameText = false };
        h.Open("atmospherics");
        h.Core.Shunts.AddRule("^A kitten", "Atmospherics", copy: true);

        h.Line(Kitten);

        Assert.Single(h.Tabs.Atmospherics.Lines);
        Assert.Empty(h.Main.Lines);
    }
}
