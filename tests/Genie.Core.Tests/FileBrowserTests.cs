using System;
using System.IO;
using System.Linq;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <see cref="FileBrowser"/> — regression coverage for the
/// <c>ProcessStartInfo.ArgumentList</c> spaces fix (paths under macOS
/// <c>~/Library/Application Support/Genie5</c> must never be tokenized) plus
/// the platform-<c>FileName</c> and <c>createIfMissing</c>/reveal contracts.
/// Only <c>Build*Info</c> and filesystem side effects are asserted; every
/// <see cref="FileBrowser.OpenDirectory"/> call passes a no-op <c>launch</c>
/// so no test ever actually opens Finder/Explorer/xdg-open.
/// </summary>
public class FileBrowserTests : IDisposable
{
    private static readonly string RootDir =
        Path.Combine(Path.GetTempPath(), "Genie5FileBrowserTests");

    private static string SpacesPath(params string[] segments)
        => Path.Combine(RootDir, Path.Combine(
            new[] { "Application Support", "Genie5" }.Concat(segments).ToArray()));

    // OpenDirectory(createIfMissing: true) actually creates directories on
    // disk (under RootDir); remove them so repeated test runs don't pile up
    // empty folders in the OS temp dir.
    public void Dispose()
    {
        if (Directory.Exists(RootDir))
            Directory.Delete(RootDir, recursive: true);
    }

    [Fact]
    public void BuildOpenDirectoryInfo_keeps_spaces_path_as_single_argument()
    {
        var dir = SpacesPath("Config");

        var psi = FileBrowser.BuildOpenDirectoryInfo(dir);

        Assert.Single(psi.ArgumentList);
        Assert.Equal(Path.GetFullPath(dir), psi.ArgumentList[0]);
        Assert.Equal(string.Empty, psi.Arguments);
    }

    [Fact]
    public void BuildOpenDirectoryInfo_uses_platform_file_manager()
    {
        var psi = FileBrowser.BuildOpenDirectoryInfo(Path.GetTempPath());

        var expected = OperatingSystem.IsWindows() ? "explorer.exe"
                     : OperatingSystem.IsMacOS()   ? "open"
                     : "xdg-open";
        Assert.Equal(expected, psi.FileName);
    }

    [Fact]
    public void OpenDirectory_createIfMissing_true_creates_directory_first()
    {
        var dir = SpacesPath("CreateIfMissing", Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(dir));
        var launched = 0;

        // launch: no-op so this never actually opens Finder/Explorer/xdg-open.
        FileBrowser.OpenDirectory(dir, createIfMissing: true, launch: _ => launched++);

        Assert.True(Directory.Exists(dir));
        Assert.Equal(1, launched);
    }

    [Fact]
    public void OpenDirectory_createIfMissing_false_on_missing_dir_is_a_noop()
    {
        var dir = SpacesPath("Reveal", Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(dir));
        var launched = 0;

        var ex = Record.Exception(() =>
            FileBrowser.OpenDirectory(dir, createIfMissing: false, launch: _ => launched++));

        Assert.Null(ex);
        Assert.False(Directory.Exists(dir));
        Assert.Equal(0, launched);
    }

    [Fact]
    public void BuildOpenInDefaultTextEditorInfo_keeps_spaces_path_as_single_argument()
    {
        var file = SpacesPath("scripts", "my script.cmd");

        var psi = FileBrowser.BuildOpenInDefaultTextEditorInfo(file);

        var expectedPath = Path.GetFullPath(file);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(2, psi.ArgumentList.Count);
            Assert.Equal("-t", psi.ArgumentList[0]);
            Assert.Equal(expectedPath, psi.ArgumentList[1]);
        }
        else
        {
            Assert.Single(psi.ArgumentList);
            Assert.Equal(expectedPath, psi.ArgumentList[0]);
        }
        Assert.Equal(string.Empty, psi.Arguments);
    }

    [Fact]
    public void BuildOpenInDefaultTextEditorInfo_uses_platform_default_editor()
    {
        var psi = FileBrowser.BuildOpenInDefaultTextEditorInfo(Path.Combine(Path.GetTempPath(), "file.cmd"));

        var expected = OperatingSystem.IsWindows() ? "notepad.exe"
                     : OperatingSystem.IsMacOS()   ? "open"
                     : "xdg-open";
        Assert.Equal(expected, psi.FileName);
    }
}
