using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Genie.App.Views;
using Genie.App.ViewModels;
using Genie.Core.Runtime;

namespace Genie.App;

public class App : Application
{
    private const string AppName = "Genie5";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Hold the app open across the first-run prompt: there's a window-
            // less gap between closing that dialog and showing the main window,
            // and the default OnLastWindowClose would shut us down in it.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Parse launch flags (--host/--port/--profile/--mode) so an external
            // launcher (e.g. Lich Launcher) can start Genie pre-connected.
            var startup = StartupOptions.Parse(desktop.Args);

            // Portable-first storage: if Genie has no data beside the exe nor in
            // the user folder, ask where to create it before anything resolves a
            // data path. The choice is materialized here so the deterministic
            // AppPaths.Discover (in the VM and every GenieCore) lands on it.
            await EnsureStorageLocationChosenAsync();
            // On a fresh machine the crash logger started in a temp holding
            // location (it must not create Config before the prompt); move it
            // to the data root that was just chosen.
            Program.RefreshCrashLogPath();

            var window = new MainWindow { DataContext = new MainWindowViewModel(startup) };
            desktop.MainWindow = window;
            window.Show();

            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// First-run storage-location choice. No-op once Genie data exists in either
    /// location (the choice persists as a created <c>Config</c> folder, so we
    /// never re-prompt). Defaults to portable-first if the local option is
    /// chosen but turns out not to be writable.
    /// </summary>
    private static async Task EnsureStorageLocationChosenAsync()
    {
        // ResolvePortableRoot, not AppContext.BaseDirectory: under a Velopack
        // install the exe runs from a wiped-on-update `current\` subfolder, so
        // the portable root (where data belongs) is its parent. Must match what
        // AppPaths.Discover uses or the prompt would materialize Config in the
        // wrong place.
        var localDir = AppPaths.ResolvePortableRoot(AppContext.BaseDirectory);
        var userDir  = AppPaths.GetUserDataDirectory(AppName);

        if (AppPaths.HasData(localDir) || AppPaths.HasData(userDir))
            return;

        var dialog = new FirstRunLocationDialog(localDir, userDir);
        dialog.Show();
        var portable = await dialog.Completion;

        // Materialize the chosen location by creating its Config folder, which
        // is exactly what AppPaths.HasData looks for. Fall back to the user
        // folder if a portable (local) write isn't possible.
        if (portable && AppPaths.IsDirectoryWritable(localDir))
        {
            Directory.CreateDirectory(Path.Combine(localDir, "Config"));
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(userDir, "Config"));
        }
    }
}
