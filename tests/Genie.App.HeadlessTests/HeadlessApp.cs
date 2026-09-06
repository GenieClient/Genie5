using System;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Themes.Fluent;
using Genie.App.HeadlessTests;

[assembly: AvaloniaTestApplication(typeof(HeadlessAppBuilder))]

namespace Genie.App.HeadlessTests;

/// <summary>
/// Entry point the Avalonia.Headless.XUnit runner uses to boot a UI thread for
/// <c>[AvaloniaFact]</c> tests. Loads the same theme stack the shipping app
/// declares in App.axaml (FluentTheme + DockFluentTheme) so Dock's real control
/// templates — e.g. the ToolChromeControl float title bar #181 collapses —
/// materialize exactly as in production.
/// </summary>
public class HeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class HeadlessApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new DockFluentTheme());
        // App.axaml also pulls in the DataGrid + ColorPicker control themes;
        // without them the config-panel grids never template and selection
        // behavior diverges from production.
        Styles.Add(new StyleInclude(new Uri("avares://Genie.App"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });
        Styles.Add(new StyleInclude(new Uri("avares://Genie.App"))
        {
            Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml")
        });
        // The app's own dock-chrome rules. Without these the headless tree renders
        // with stock Dock styling only, so anything App.axaml styles into place
        // (#302 / #320's banner marker) would silently not apply here and a test
        // could "pass" against behaviour production never has.
        Styles.Add(new StyleInclude(new Uri("avares://Genie.App"))
        {
            Source = new Uri("avares://Genie5/Themes/BannerChromeStyles.axaml")
        });
    }
}
