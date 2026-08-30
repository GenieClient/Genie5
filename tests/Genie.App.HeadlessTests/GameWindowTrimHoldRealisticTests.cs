using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// #293 regression probe, production-faithful: drives the REAL
/// <see cref="GameTextViewModel"/> write path (AddStreamLine → AddLine →
/// TrimScrollback in one synchronous call) into a control tree mirroring the
/// App.axaml game-text template — ScrollViewer whose Content is the
/// ItemsControl, SelectableTextBlock lines with TextWrapping.Wrap (variable
/// heights), LineSelection.Enabled, and the XAML attach order (the ScrollViewer
/// behavior subscribes to the buffer before the ItemsControl does).
/// </summary>
public class GameWindowTrimHoldRealisticTests
{
    private sealed class Harness : IDisposable
    {
        public GameTextViewModel Vm { get; } = new();
        public ScrollViewer  Sv  { get; }
        public ItemsControl  Ic  { get; }
        public Window        Win { get; }
        private int _seq;

        public Harness(int cap)
        {
            typeof(GameTextViewModel)
                .GetField("_maxLines", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(Vm, cap);

            Sv = new ScrollViewer { Width = 300, Height = 200 };
            // XAML order: the ScrollViewer's attached ItemsSource binds (and
            // subscribes) before the child ItemsControl's own ItemsSource.
            AutoScrollBehavior.SetItemsSource(Sv, Vm.Lines);

            Ic = new ItemsControl
            {
                FontSize     = 13,
                ItemTemplate = new FuncDataTemplate<TextLine>((l, _) =>
                    new SelectableTextBlock
                    {
                        Text         = l?.Text,
                        TextWrapping = TextWrapping.Wrap,
                    }),
            };
            LineSelection.SetEnabled(Ic, true);
            Ic.ItemsSource = Vm.Lines;
            Sv.Content     = Ic;

            Win = new Window { Width = 400, Height = 300, Content = Sv };
            Win.Show();
            Pump();
        }

        /// <summary>Real production write path; varying lengths so wrapped
        /// containers have different heights, like live game text.</summary>
        public void AddGameLine()
        {
            var i = _seq++;
            var text = (i % 3) switch
            {
                0 => $"line {i} — a short one.",
                1 => $"line {i} — medium length line of ordinary room text as DR sends it.",
                _ => $"line {i} — " + string.Join(' ',
                         Enumerable.Repeat("wrapped combat spam with linky words and long clauses", 3)),
            };
            Vm.AddStreamLine("test", text);
        }

        public void Pump()
        {
            Win.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Win.UpdateLayout();
        }

        /// <summary>Viewport-relative Y of a line's container.</summary>
        public double ScreenY(TextLine line)
        {
            var idx = Vm.Lines.IndexOf(line);
            Assert.True(idx >= 0, "anchor line was trimmed out of the buffer");
            var c = Ic.ContainerFromIndex(idx);
            Assert.True(c is not null, $"no container for line index {idx}");
            return c!.Bounds.Y - Sv.Offset.Y;
        }

        /// <summary>First line whose container starts at or below the viewport
        /// top — what the reader sees at the top of the held view.</summary>
        public TextLine TopVisibleLine()
        {
            foreach (var (item, idx) in Vm.Lines.Select((l, i) => (l, i)))
            {
                var c = Ic.ContainerFromIndex(idx);
                if (c is not null && c.Bounds.Y + c.Bounds.Height > Sv.Offset.Y + 1)
                    return item;
            }
            throw new InvalidOperationException("no visible line found");
        }

        public void Dispose()
        {
            try { Win.Close(); } catch { /* teardown */ }
        }
    }

    [AvaloniaFact]
    public void RolledBack_holds_once_the_scrollback_is_filled()
    {
        using var h = new Harness(cap: 100);

        // Fill PAST the cap first — trims already happening — then roll back.
        for (var i = 0; i < 130; i++) { h.AddGameLine(); h.Pump(); }

        h.Sv.Offset = h.Sv.Offset.WithY((h.Sv.Extent.Height - h.Sv.Viewport.Height) / 2);
        h.Pump();
        var anchor = h.TopVisibleLine();
        var before = h.ScreenY(anchor);

        for (var i = 0; i < 40; i++) { h.AddGameLine(); h.Pump(); }

        Assert.Equal(before, h.ScreenY(anchor), 1);
    }

    [AvaloniaFact]
    public void Paused_holds_once_the_scrollback_is_filled()
    {
        using var h = new Harness(cap: 100);

        for (var i = 0; i < 130; i++) { h.AddGameLine(); h.Pump(); }

        AutoScrollBehavior.SetPaused(h.Sv, true);
        h.Pump();
        var anchor = h.TopVisibleLine();
        var before = h.ScreenY(anchor);

        for (var i = 0; i < 40; i++) { h.AddGameLine(); h.Pump(); }

        Assert.Equal(before, h.ScreenY(anchor), 1);
    }

    [AvaloniaFact]
    public void RolledBack_holds_through_combat_bursts_at_the_cap()
    {
        using var h = new Harness(cap: 100);

        for (var i = 0; i < 130; i++) { h.AddGameLine(); h.Pump(); }

        h.Sv.Offset = h.Sv.Offset.WithY((h.Sv.Extent.Height - h.Sv.Viewport.Height) / 2);
        h.Pump();
        var anchor = h.TopVisibleLine();
        var before = h.ScreenY(anchor);

        // Several lines per frame — no layout between them.
        for (var burst = 0; burst < 8; burst++)
        {
            for (var i = 0; i < 5; i++) h.AddGameLine();
            h.Pump();
        }

        Assert.Equal(before, h.ScreenY(anchor), 1);
    }
}
