using System;
using System.IO;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// <see cref="MainWindowViewModel"/> has no <c>dataDirectoryOverride</c> seam
/// today (unlike <see cref="Genie.Core.GenieCore"/>, which already has one —
/// see MapperConfigSyncTests.Harness), so every test that constructs it would
/// otherwise touch the real per-user Genie5 AppData folder: Profiles.Load,
/// Display.Load, and a one-time Maps-folder migration all run in the
/// constructor. This confirms the override actually confines that I/O to an
/// isolated directory instead.
/// </summary>
public class DataDirectoryOverrideTests
{
    [Fact]
    public void DataDirectoryOverride_confines_construction_file_io_to_the_given_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "genie_app_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            _ = new MainWindowViewModel(startup: null, dataDirectoryOverride: dir);

            Assert.True(Directory.Exists(Path.Combine(dir, "Config")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
