using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Search;
using Genie.App.Controls;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Find on an editor-backed window opens AvaloniaEdit's search panel through
/// <see cref="FindInWindowModel.Open"/>, usually while focus sits outside the
/// editor (the command bar, a menu). The panel's close (X) button is bound to an
/// AvaloniaEdit RoutedCommand whose CanExecute routes from the last element to
/// take focus, asked once as the panel attaches and never again. Opened with
/// focus outside the editor, the X came up disabled for the panel's whole life,
/// and only Escape could close it.
/// </summary>
public class EditorSearchPanelCloseHeadlessTests
{
    private static void Pump(Window win)
    {
        for (var i = 0; i < 3; i++)
        {
            win.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

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
        public FindInWindowModel? Find { get; }
        public bool EnableColorizing => false;
        public bool EnableLinks => false;

        public StubHost() => Find = new FindInWindowModel(() => Lines.Select(l => l.Text).ToList());
    }

    [AvaloniaFact]
    public void Close_button_closes_a_panel_opened_with_focus_outside_the_editor()
    {
        var host = new StubHost();
        for (var i = 0; i < 20; i++) host.Lines.Add(new TextLine($"line {i}", StreamColor.Main));

        var editor     = new GameTextEditor { DataContext = host };
        var commandBar = new TextBox();                       // stands in for the command bar
        DockPanel.SetDock(commandBar, Avalonia.Controls.Dock.Bottom);
        var win = new Window
        {
            Width = 600, Height = 400,
            Content = new DockPanel { Children = { commandBar, editor } },
        };
        win.Show();
        Pump(win);

        commandBar.Focus();
        Pump(win);

        host.Find!.Open();                                    // what Ctrl+F / the menu call
        Pump(win);

        Assert.False(host.Find.IsOpen);                       // the renderer took it, not the bar
        var panel = editor.GetVisualDescendants().OfType<SearchPanel>().Single();
        Assert.False(panel.IsClosed);

        var close = panel.GetVisualDescendants().OfType<Button>()
                         .Single(b => b.Command == SearchCommands.CloseSearchPanel);
        Assert.True(close.IsEffectivelyEnabled);

        var centre = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), win)!.Value;
        win.MouseDown(centre, MouseButton.Left);
        win.MouseUp(centre, MouseButton.Left);
        Pump(win);

        Assert.True(panel.IsClosed);
    }
}
