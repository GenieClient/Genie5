using Avalonia;
using Avalonia.Headless;
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
    }
}
