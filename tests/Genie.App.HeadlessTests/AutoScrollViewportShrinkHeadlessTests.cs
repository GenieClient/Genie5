using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Genie.App.Controls;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Starting a script shows the Script Bar, which shrinks every docked game
/// window by the strip's height under an unchanged scroll offset. That used to
/// read as "the user scrolled up": auto-follow stopped and the reader had to
/// click ↓ Bottom to catch up. A layout-only viewport change must never take a
/// following view off the tail — only an upward scroll may. Covers both
/// renderers: the legacy ScrollViewer (<see cref="AutoScrollState"/>) and the
/// AvaloniaEdit <see cref="GameTextEditor"/>.
/// </summary>
public class AutoScrollViewportShrinkHeadlessTests
{
    private const double LineHeight = 20;

    private static void Pump(Window win)
    {
        for (var i = 0; i < 3; i++)
        {
            win.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ── Legacy ScrollViewer renderer ──────────────────────────────────────────

    private sealed class LegacyHarness
    {
        public ObservableCollection<TextLine> Lines { get; } = new();
        public ScrollViewer Sv  { get; }
        public Window       Win { get; }
        private int _seq;

        public LegacyHarness(int lines)
        {
            for (var i = 0; i < lines; i++) Lines.Add(NewLine());
            var ic = new ItemsControl
            {
                ItemsSource  = Lines,
                ItemTemplate = new FuncDataTemplate<TextLine>((l, _) =>
                    new Border { Height = LineHeight, Child = new TextBlock { Text = l?.Text } }),
            };
            Sv = new ScrollViewer { Content = ic, Width = 300, Height = 200 };
            AutoScrollBehavior.SetItemsSource(Sv, Lines);
            Win = new Window { Width = 400, Height = 300, Content = Sv };
            Win.Show();
            Pump(Win);
        }

        public TextLine NewLine() => new($"line {_seq++}", StreamColor.Main);
        public AutoScrollState State => Assert.IsType<AutoScrollState>(Sv.Tag);
        public double Max => Sv.Extent.Height - Sv.Viewport.Height;
    }

    [AvaloniaFact]
    public void Legacy_viewport_shrink_keeps_following_the_tail()
    {
        var h = new LegacyHarness(100);
        Assert.False(h.State.IsScrolledUp);

        h.Sv.Height = 170;              // the Script Bar appears
        Pump(h.Win);
        Assert.False(h.State.IsScrolledUp);

        h.Lines.Add(h.NewLine());
        Pump(h.Win);
        Assert.False(h.State.IsScrolledUp);
        Assert.Equal(h.Max, h.Sv.Offset.Y, 1);
    }

    [AvaloniaFact]
    public void Legacy_a_real_scroll_up_still_leaves_the_tail()
    {
        var h = new LegacyHarness(100);
        h.Sv.Offset = h.Sv.Offset.WithY(h.Max - 200);
        Pump(h.Win);
        Assert.True(h.State.IsScrolledUp);

        h.Sv.Height = 170;              // a resize while rolled back stays rolled back
        Pump(h.Win);
        Assert.True(h.State.IsScrolledUp);
    }

    // ── AvaloniaEdit renderer ─────────────────────────────────────────────────

    private sealed class StubHost : ITextEditorHost
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public ObservableCollection<TextLine> Lines { get; } = new();
        public FontFamily ToolFontFamily => FontFamily.Default;
        public double ToolFontSize => 12;
        public IBrush? ToolForeground => null;
        public TextWrapping ToolTextWrapping => TextWrapping.NoWrap;
        public ScrollBarVisibility ToolHScroll => ScrollBarVisibility.Disabled;
        public bool IsScrollPaused { get; set; }
        public FindInWindowModel? Find => null;
        public bool EnableColorizing => false;
        public bool EnableLinks => false;
    }

    [AvaloniaFact]
    public void Editor_viewport_shrink_keeps_following_the_tail()
    {
        var host = new StubHost();
        for (var i = 0; i < 200; i++) host.Lines.Add(new TextLine($"line {i}", StreamColor.Main));
        var editor = new GameTextEditor { DataContext = host, Width = 300, Height = 200 };
        var win = new Window { Width = 400, Height = 300, Content = editor };
        win.Show();
        Pump(win);
        Assert.False(editor.IsScrolledUp);

        editor.Height = 170;            // the Script Bar appears
        Pump(win);
        Assert.False(editor.IsScrolledUp);

        host.Lines.Add(new TextLine("new", StreamColor.Main));
        Pump(win);
        Assert.False(editor.IsScrolledUp);
    }
}
