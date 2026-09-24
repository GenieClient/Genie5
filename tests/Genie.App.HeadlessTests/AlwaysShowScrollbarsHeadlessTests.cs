using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #365 — "Always show scrollbars". Materializes the real Fluent templates
/// because the obvious implementation silently misses most of the app: a
/// <c>ScrollViewer</c> style reaches bare ScrollViewers only, while ListBox and
/// TextBox (and the other scrolling controls) TemplateBind their inner
/// ScrollViewer's <c>AllowAutoHide</c> to the owning control — a binding that
/// outranks a style setter. Measured on the shipping Avalonia version.
/// </summary>
public class AlwaysShowScrollbarsHeadlessTests
{
    private sealed class Surfaces : IDisposable
    {
        public ScrollViewer Viewer { get; } = new() { Height = 60, Content = new Border { Height = 600 } };
        public ListBox      List   { get; } = new() { Height = 60, ItemsSource = Enumerable.Range(0, 100).Select(i => "row " + i).ToList() };
        public TextBox      Text   { get; } = new() { Height = 60, AcceptsReturn = true, Text = string.Join("\n", Enumerable.Range(0, 100)) };
        private readonly Window _window;

        public Surfaces()
        {
            _window = new Window { Width = 300, Height = 400, Content = new StackPanel { Children = { Viewer, List, Text } } };
            _window.Show();
            Settle();
        }

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            _window.UpdateLayout();
        }

        /// <summary>AllowAutoHide of every ScrollBar the three surfaces render.</summary>
        public bool[] BarsAutoHide() =>
            new Control[] { Viewer, List, Text }
                .SelectMany(c => c.GetVisualDescendants().OfType<ScrollBar>())
                .Select(b => b.AllowAutoHide)
                .ToArray();

        public void Dispose()
        {
            ScrollBarAutoHide.SetAlwaysVisible(false);   // leave the shared Application clean
            _window.Close();
        }
    }

    [AvaloniaFact]
    public void Stock_scrollbars_auto_hide()
    {
        using var s = new Surfaces();

        var bars = s.BarsAutoHide();
        Assert.NotEmpty(bars);
        Assert.All(bars, Assert.True);
    }

    [AvaloniaFact]
    public void The_setting_reaches_bare_viewers_list_boxes_and_text_boxes_and_turns_back_off()
    {
        using var s = new Surfaces();
        var display = new DisplaySettings();
        display.Apply();

        display.AlwaysShowScrollbars = true;
        s.Settle();
        var on = s.BarsAutoHide();
        Assert.Equal(6, on.Length);                  // two bars per surface
        Assert.All(on, Assert.False);

        display.AlwaysShowScrollbars = false;
        s.Settle();
        Assert.All(s.BarsAutoHide(), Assert.True);
    }

    [AvaloniaFact]
    public void A_persisted_on_applies_at_startup()
    {
        using var s = new Surfaces();
        var display = new DisplaySettings { AlwaysShowScrollbars = true };

        display.Apply();
        s.Settle();

        Assert.All(s.BarsAutoHide(), Assert.False);
    }

    [AvaloniaFact]
    public void The_setting_round_trips_through_display_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "genie_scrollbars_io_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "display.json");
            new DisplaySettings { AlwaysShowScrollbars = true }.Save(path);

            Assert.True(DisplaySettings.Load(path).AlwaysShowScrollbars);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [AvaloniaFact]
    public void The_display_settings_dialog_commits_and_resets_it()
    {
        var live = new DisplaySettings();
        var vm = new DisplaySettingsViewModel(live, null) { AlwaysShowScrollbars = true };

        vm.OkCommand.Execute().Subscribe();
        Assert.True(live.AlwaysShowScrollbars);

        vm.ResetCommand.Execute().Subscribe();
        Assert.False(vm.AlwaysShowScrollbars);
    }
}
