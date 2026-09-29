using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.ReactiveUI;

namespace Genie.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // ── Install bottom-most exception handlers BEFORE anything else can
        // throw. These catch failures that escape every other handler — the
        // ones responsible for "app ends without notice" exits.
        InstallCrashLogger();

        // ── Velopack startup hook ────────────────────────────────────────────
        // MUST be called before any other startup work. Velopack's installer
        // pipeline spawns the running Genie5.exe with --veloapp-* arguments
        // during install / uninstall / first-run / update; VelopackApp.Run()
        // intercepts those and exits cleanly without ever booting Avalonia.
        // On a normal launch it's a no-op that returns immediately.
        try
        {
            Velopack.VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            // Velopack failures must not block the app launching — log and
            // continue. The Updates dialog will surface the install state
            // via CoreAppUpdater.IsInstalled.
            WriteCrash("VelopackApp.Run", ex);
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrash("Main.UnhandledTopLevel", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI()
            .LogToTrace();

    // ── Last-resort crash logging ────────────────────────────────────────────

    private static string CrashLogPath { get; } = ComputeCrashLogPath();

    private static string ComputeCrashLogPath()
    {
        try
        {
            // Follow the same portable-first data root AppPaths.Discover uses,
            // so a portable copy logs beside itself instead of into the user's
            // installed Genie's %APPDATA% Config. Any failure here (including
            // Genie.Core failing to load) drops to the plain user-folder path.
            return ComputeCrashLogPath(AppContext.BaseDirectory, ResolveUserDataDirectory());
        }
        catch
        {
            return ComputeLegacyCrashLogPath();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ResolveUserDataDirectory()
        => Genie.Core.Runtime.AppPaths.GetUserDataDirectory("Genie5");

    /// <summary>
    /// Crash-log location for an exe in <paramref name="exeDirectory"/>: the
    /// portable root's <c>Config</c> when that root already holds Genie data
    /// (a portable marker or a <c>Config</c> folder) and is writable — the
    /// same test <c>AppPaths.Discover</c> applies first — else the per-user
    /// <paramref name="userDataDirectory"/>. Deliberately does NOT take
    /// Discover's fresh-install "local if writable" branch: creating a local
    /// <c>Config</c> here would pre-empt the first-run location prompt.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string ComputeCrashLogPath(string exeDirectory, string userDataDirectory)
    {
        var localDir = Genie.Core.Runtime.AppPaths.ResolvePortableRoot(exeDirectory);
        var root = Genie.Core.Runtime.AppPaths.HasData(localDir)
                   && Genie.Core.Runtime.AppPaths.IsDirectoryWritable(localDir)
            ? localDir
            : userDataDirectory;

        Directory.CreateDirectory(Path.Combine(root, "Config"));
        return Path.Combine(root, "Config", "genie_crash.log");
    }

    private static string ComputeLegacyCrashLogPath()
    {
        // Resolve a writable user-data dir without depending on any of our
        // own classes (those might be the thing that's failing).
        try
        {
            string root;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Genie5");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                    "Library", "Application Support", "Genie5");
            else
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                    ".local", "share", "Genie5");

            Directory.CreateDirectory(Path.Combine(root, "Config"));
            return Path.Combine(root, "Config", "genie_crash.log");
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "genie_crash.log");
        }
    }

    private static void InstallCrashLogger()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                WriteCrash("AppDomain.UnhandledException", ex);
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // Marker so we can confirm the logger is wired even on a clean run.
        WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Startup] Process started; crash logger installed at {CrashLogPath}");
    }

    private static void WriteCrash(string source, Exception ex)
    {
        WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex.GetType().FullName}: {ex.Message}\n{ex}\n");
    }

    private static void WriteLine(string text)
    {
        try { File.AppendAllText(CrashLogPath, text + Environment.NewLine); } catch { }
    }
}
