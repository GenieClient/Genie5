using System;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// #293 scroll-hold regression probe: the synchronous trim compensation in
/// <see cref="AutoScrollState"/> must keep the viewport visually still while
/// the scrollback cap trims lines off the top — both when the user has rolled
/// back (scrolled up) and when Pause Scrolling is on. Mirrors the shipping
/// game-text template shape (ScrollViewer whose Content IS the ItemsControl,
/// non-virtualized StackPanel) and the GameTextViewModel write pattern
/// (Lines.Add(...) immediately followed by RemoveAt(0) in the same job).
/// </summary>
public class AutoScrollTrimHoldHeadlessTests
{
    private const double LineHeight = 20;

    private sealed class Harness : IDisposable
    {
        public ObservableCollection<TextLine> Lines { get; } = new();
        public ScrollViewer  Sv  { get; }
        public ItemsControl  Ic  { get; }
        public Window        Win { get; }
        private int _seq;

        public Harness(int initialLines)
        {
            for (var i = 0; i < initialLines; i++)
                Lines.Add(NewLine());

            Ic = new ItemsControl
            {
                ItemsSource  = Lines,
                ItemTemplate = new FuncDataTemplate<TextLine>((l, _) =>
                    new Border { Height = LineHeight, Child = new TextBlock { Text = l?.Text } }),
            };
            Sv = new ScrollViewer { Content = Ic, Width = 300, Height = 200 };
            AutoScrollBehavior.SetItemsSource(Sv, Lines);

            Win = new Window { Width = 400, Height = 300, Content = Sv };
            Win.Show();
            Pump();
        }

        public TextLine NewLine() => new($"line {_seq++}", StreamColor.Main);

        /// <summary>One add-then-trim cycle — the exact GameTextViewModel.AddLine
        /// order at the scrollback cap.</summary>
        public void AddAndTrim()
        {
            Lines.Add(NewLine());
            Lines.RemoveAt(0);
        }

        public void Pump()
        {
            Win.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Win.UpdateLayout();
        }

        public AutoScrollState State => Assert.IsType<AutoScrollState>(Sv.Tag);

        /// <summary>Viewport-relative Y of the line's container — what the user
        /// actually sees. Must not move while the hold is active.</summary>
        public double ScreenY(TextLine line)
        {
            var idx = Lines.IndexOf(line);
            Assert.True(idx >= 0, "anchor line was trimmed out of the buffer");
            var container = Ic.ContainerFromIndex(idx);
            Assert.True(container is not null, $"no container for line index {idx}");
            return container!.Bounds.Y - Sv.Offset.Y;
        }

        public void Dispose()
        {
            try { Win.Close(); } catch { /* teardown */ }
        }
    }

    [AvaloniaFact]
    public void RolledBack_view_holds_still_across_trims_at_the_cap()
    {
        using var h = new Harness(100);

        // Roll back mid-buffer: line 40 sits at the top of the viewport.
        h.Sv.Offset = h.Sv.Offset.WithY(40 * LineHeight);
        h.Pump();
        var anchor  = h.Lines[40];
        var before  = h.ScreenY(anchor);
        Assert.True(h.State.IsScrolledUp, "expected the rolled-back state to register");

        // 30 lines arrive at the cap — one layout pass per line (live pacing).
        for (var i = 0; i < 30; i++)
        {
            h.AddAndTrim();
            h.Pump();
        }

        Assert.Equal(before, h.ScreenY(anchor), 1);
    }

    [AvaloniaFact]
    public void Paused_view_holds_still_across_trims_at_the_cap()
    {
        using var h = new Harness(100);

        // Pause while reading the tail — the shipped menu path sets the
        // attached property, which flows into AutoScrollState.Paused.
        AutoScrollBehavior.SetPaused(h.Sv, true);
        h.Pump();
        var anchor = h.Lines[90];                  // top of the final viewport
        var before = h.ScreenY(anchor);

        for (var i = 0; i < 30; i++)
        {
            h.AddAndTrim();
            h.Pump();
        }

        Assert.Equal(before, h.ScreenY(anchor), 1);
    }

    [AvaloniaFact]
    public void Burst_of_trims_in_one_tick_still_holds()
    {
        using var h = new Harness(100);

        h.Sv.Offset = h.Sv.Offset.WithY(50 * LineHeight);
        h.Pump();
        var anchor = h.Lines[50];
        var before = h.ScreenY(anchor);

        // A combat burst: several lines land between frames, so multiple
        // add+trim pairs hit the collection before any layout pass runs —
        // the _trimComp stale-bounds accumulation path.
        for (var burst = 0; burst < 4; burst++)
        {
            for (var i = 0; i < 5; i++)
                h.AddAndTrim();
            h.Pump();
        }

        Assert.Equal(before, h.ScreenY(anchor), 1);
    }

    [AvaloniaFact]
    public void Following_the_tail_still_reaches_the_newest_line()
    {
        using var h = new Harness(100);
        // Sanity guard: the compensation must never fire while pinned to the
        // bottom — the tail keeps auto-following at the cap.
        for (var i = 0; i < 10; i++)
        {
            h.AddAndTrim();
            h.Pump();
        }
        var max = h.Sv.Extent.Height - h.Sv.Viewport.Height;
        Assert.True(h.Sv.Offset.Y >= max - 1,
            $"expected to stay pinned at the bottom (offset {h.Sv.Offset.Y} vs max {max})");
    }
}
