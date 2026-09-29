using System;
using System.IO;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The startup crash logger resolves its path before any other startup work,
/// so it can't use AppPaths.Discover's full side-effecting flow — but it must
/// still honor portable mode, or a portable copy appends its "[Startup]"
/// lines to the installed Genie's per-user crash log.
/// </summary>
public class CrashLogPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "genie_crashlog_" + Guid.NewGuid().ToString("N"));
    private readonly string _exeDir;
    private readonly string _userDir;
    private readonly string _freshDir;

    public CrashLogPathTests()
    {
        _exeDir = Path.Combine(_root, "exe");
        _userDir = Path.Combine(_root, "user");
        _freshDir = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(_exeDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Portable_marker_puts_the_crash_log_beside_the_exe()
    {
        File.WriteAllText(Path.Combine(_exeDir, AppPaths.PortableMarkerFileName), string.Empty);

        var path = Program.ComputeCrashLogPath(_exeDir, _userDir, _freshDir);

        Assert.Equal(Path.Combine(Path.GetFullPath(_exeDir), "Config", "genie_crash.log"), path);
        Assert.False(Directory.Exists(_userDir));
    }

    [Fact]
    public void Without_a_marker_an_existing_user_folder_gets_the_crash_log()
    {
        Directory.CreateDirectory(Path.Combine(_userDir, "Config"));

        var path = Program.ComputeCrashLogPath(_exeDir, _userDir, _freshDir);

        Assert.Equal(Path.Combine(_userDir, "Config", "genie_crash.log"), path);
        Assert.False(Directory.Exists(Path.Combine(_exeDir, "Config")));
    }

    [Fact]
    public void Fresh_machine_logs_to_the_holding_folder_and_creates_no_Config()
    {
        var path = Program.ComputeCrashLogPath(_exeDir, _userDir, _freshDir);

        Assert.Equal(Path.Combine(_freshDir, "genie_crash.log"), path);
        // A Config folder in either location is what AppPaths.HasData reads as
        // "already set up" — creating one would suppress the first-run prompt.
        Assert.False(AppPaths.HasData(_exeDir));
        Assert.False(AppPaths.HasData(_userDir));
    }

    [Fact]
    public void Velopack_current_folder_logs_to_the_install_root()
    {
        var current = Path.Combine(_exeDir, "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(_exeDir, AppPaths.VelopackPortableMarkerFileName), string.Empty);

        var path = Program.ComputeCrashLogPath(current, _userDir, _freshDir);

        Assert.Equal(Path.Combine(Path.GetFullPath(_exeDir), "Config", "genie_crash.log"), path);
    }
}
