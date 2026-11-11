using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.Controls;
using Genie.App.Settings;
using Genie.App.Theming;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Presets;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #406 (part 4): Genie 4's <c>ui.*</c> presets colour the surfaces Genie 4
/// used them for: the menu bar and its menus (<c>ui.menu</c>, hover
/// <c>ui.menu.highlight</c>, tick <c>ui.menu.checked</c>), the command input
/// (<c>ui.textbox</c>), the status strip (<c>ui.status</c>), and the AutoMapper
/// Settings window and its buttons (<c>ui.window</c>, <c>ui.button</c>). The
/// stock value is Default (the theme's look); a colour paints over the theme.
/// </summary>
[Collection("highlight-statics")]
public class UiPresetsHeadlessTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "genie_uipresets_" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly PresetColorStore? _prevStore = PresetChrome.Store;
    private readonly PresetColorStore _store;

    public UiPresetsHeadlessTests()
    {
        _store = new PresetColorStore(new PresetEngine(), _dir, () => null);
        PresetChrome.Store = _store;
    }

    public void Dispose()
    {
        PresetChrome.Store = _prevStore;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Color? C(IBrush? b) => (b as ISolidColorBrush)?.Color;

    private void Set(string id, string fg, string bg = "")
    {
        _store.Set(id, fg, bg);
        Dispatcher.UIThread.RunJobs();
    }

    private static T Shown<T>(T content) where T : Control
    {
        new Window { Content = content }.Show();
        Dispatcher.UIThread.RunJobs();
        return content;
    }

    // ── Stock ──────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Stock_ui_presets_are_Default_so_the_theme_colours_the_chrome()
    {
        var e = new PresetEngine();
        Assert.All(UiPresets.All, id =>
        {
            Assert.Equal("Default", e.GetForeground(id));
            Assert.Equal("",        e.GetBackground(id));
        });
    }

    // ── Menu ───────────────────────────────────────────────────────────────

    private static (Menu Menu, MenuItem Top, MenuItem Tick) NewMenu()
    {
        var tick = new MenuItem { Header = "Tick", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
        var top  = new MenuItem { Header = "_File", ItemsSource = new[] { tick } };
        var menu = new Menu { ItemsSource = new[] { top } };
        // The same style MainWindow.axaml puts on its menu for ui.menu.checked.
        menu.Styles.Add(new Style(x => x.OfType<MenuItem>().Template().OfType<ContentControl>().Name("PART_ToggleIconPresenter"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(PresetChrome.MenuCheckedKey)) },
        });
        PresetChrome.SetSurface(menu, "menu");
        Shown(menu);
        return (menu, top, tick);
    }

    [AvaloniaFact]
    public void Ui_menu_colours_the_menu_bar_and_its_menus()
    {
        var (menu, top, tick) = NewMenu();

        Set(UiPresets.Menu, "#102030", "#C0C0C0");
        top.Open();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Color.Parse("#C0C0C0"), C(menu.Background));
        Assert.Equal(Color.Parse("#102030"), C(top.Foreground));
        Assert.Equal(Color.Parse("#102030"), C(tick.Foreground));
        Assert.Equal(Color.Parse("#C0C0C0"), C(menu.Resources["MenuFlyoutPresenterBackground"] as IBrush));
    }

    [AvaloniaFact]
    public void Ui_menu_highlight_is_the_hover_colour_and_checked_the_box_behind_a_tick()
    {
        var (menu, top, tick) = NewMenu();

        Set(UiPresets.MenuHighlight, "LightBlue");
        Set(UiPresets.MenuChecked, "#ADD8E6");
        top.Open();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Colors.LightBlue, C(menu.Resources["MenuFlyoutItemBackgroundPointerOver"] as IBrush));
        var box = tick.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "PART_ToggleIconPresenter");
        Assert.Equal(Color.Parse("#ADD8E6"), C(box.Background));
    }

    [AvaloniaFact]
    public void Default_gives_the_menu_back_to_the_theme()
    {
        var (menu, top, _) = NewMenu();
        var plain = Shown(new Menu { ItemsSource = new[] { new MenuItem { Header = "_File" } } });
        var plainTop = (MenuItem)plain.ItemsSource!.Cast<object>().Single();

        Set(UiPresets.Menu, "Red", "Blue");
        Set(UiPresets.Menu, "Default", "");

        Assert.Equal(C(plain.Background), C(menu.Background));
        Assert.Equal(C(plainTop.Foreground), C(top.Foreground));
        Assert.False(menu.Resources.ContainsKey("MenuFlyoutItemForeground"));
    }

    // ── Command input ──────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Ui_textbox_colours_the_command_input_and_Default_gives_it_back()
    {
        var box = new TextBox();
        PresetChrome.SetSurface(box, "textbox");
        Shown(box);
        var plain = Shown(new TextBox());

        Set(UiPresets.TextBox, "#000000", "#FFFFFF");
        Assert.Equal(Colors.Black, C(box.Foreground));
        Assert.Equal(Colors.White, C(box.Background));
        Assert.Equal(Colors.White, C(box.Resources["TextControlBackgroundFocused"] as IBrush));

        Set(UiPresets.TextBox, "Default", "");
        Assert.Equal(C(plain.Foreground), C(box.Foreground));
        Assert.Equal(C(plain.Background), C(box.Background));
    }

    // ── Status strip ───────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Ui_status_colours_the_status_strip_over_the_themes_strip_colour()
    {
        var app = Application.Current!;
        var had = app.Resources.TryGetResource("Theme.StripBg", null, out var prev);
        app.Resources["Theme.StripBg"] = new SolidColorBrush(Color.Parse("#222222"));
        try
        {
            var text  = new TextBlock { Text = "slot" };
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("Theme.TextPrimary"));
            var strip = new Border { Child = text };
            strip.Bind(Border.BackgroundProperty, strip.GetResourceObservable("Theme.StripBg"));
            PresetChrome.SetSurface(strip, "status");
            Shown(strip);
            Assert.Equal(Color.Parse("#222222"), C(strip.Background));     // the theme's

            Set(UiPresets.Status, "Black", "#EEEEEE");
            Assert.Equal(Color.Parse("#EEEEEE"), C(strip.Background));
            Assert.Equal(Colors.Black, C(text.Foreground));

            Set(UiPresets.Status, "Default", "");
            Assert.Equal(Color.Parse("#222222"), C(strip.Background));     // back to the theme's
        }
        finally
        {
            if (had) app.Resources["Theme.StripBg"] = prev; else app.Resources.Remove("Theme.StripBg");
        }
    }

    // ── AutoMapper Settings window ─────────────────────────────────────────

    [AvaloniaFact]
    public void Ui_window_and_ui_button_colour_the_AutoMapper_Settings_window()
    {
        var button = new Button { Content = "Close" };
        var window = new Window { Content = button };
        PresetChrome.SetSurface(window, "window");
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Set(UiPresets.Window, "Black", "#EEEEEE");
        Set(UiPresets.Button, "Black", "Silver");

        Assert.Equal(Color.Parse("#EEEEEE"), C(window.Background));
        Assert.Equal(Colors.Black,  C(window.Foreground));
        Assert.Equal(Colors.Silver, C(button.Background));
        Assert.Equal(Colors.Black,  C(button.Foreground));
    }

    [AvaloniaFact]
    public void The_real_markup_puts_each_surface_where_Genie_4_had_it()
    {
        var main = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.axaml"));
        Assert.Contains("<Menu DockPanel.Dock=\"Top\" controls:PresetChrome.Surface=\"menu\">", main);
        Assert.Contains("MenuItem /template/ ContentControl#PART_ToggleIconPresenter", main);
        Assert.Contains(PresetChrome.MenuCheckedKey, main);
        Assert.Matches("x:Name=\"CommandInput\"\\s+controls:PresetChrome.Surface=\"textbox\"", main);
        Assert.Matches("controls:PresetChrome.Surface=\"status\"\\s+Background=\"\\{DynamicResource Theme.StripBg\\}\"", main);

        var dialog = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MapperSettingsDialog.axaml"));
        Assert.Contains("controls:PresetChrome.Surface=\"window\"", dialog);
    }

    // ── Themes ─────────────────────────────────────────────────────────────

    private ThemeService Themes() =>
        new(Path.Combine(_dir, "Themes"), new DisplaySettings()) { Presets = _store };

    [AvaloniaFact]
    public void Picking_a_theme_sets_the_ui_presets_back_to_Default()
    {
        Set(UiPresets.Menu,   "Black", "#EEEEEE");
        Set(UiPresets.Status, "Black", "#EEEEEE");

        Themes().Apply(BuiltInThemes.Light);

        Assert.All(UiPresets.All, id =>
        {
            Assert.Equal("Default", _store.GetForeground(id));
            Assert.Equal("",        _store.GetBackground(id));
        });
    }

    [AvaloniaFact]
    public void A_theme_preview_leaves_the_ui_presets_alone()
    {
        Set(UiPresets.Menu, "Black", "#EEEEEE");
        Themes().Preview(BuiltInThemes.Light);
        Assert.Equal("#EEEEEE", _store.GetBackground(UiPresets.Menu));
    }

    [AvaloniaFact]
    public void Cancelling_Display_Settings_after_trying_a_theme_puts_the_ui_presets_back()
    {
        Set(UiPresets.TextBox, "Black", "White");
        var themes = Themes();
        var vm = new DisplaySettingsViewModel(new DisplaySettings(), themes, _store);

        vm.SelectedThemeName = BuiltInThemes.Light.Name;    // tries the theme: ui.* → Default
        Assert.Equal("Default", _store.GetForeground(UiPresets.TextBox));

        vm.RestoreOriginalTheme();

        Assert.Equal("Black", _store.GetForeground(UiPresets.TextBox));
        Assert.Equal("White", _store.GetBackground(UiPresets.TextBox));
    }
}
