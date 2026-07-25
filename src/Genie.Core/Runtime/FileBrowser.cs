using System.Diagnostics;

namespace Genie.Core.Runtime;

/// <summary>
/// Builds and launches OS file-manager / default-text-editor processes for a
/// given path. Always uses <see cref="ProcessStartInfo.ArgumentList"/> rather
/// than the string-<c>Arguments</c> overload: on Unix, <c>Process.Start(fileName,
/// arguments)</c> tokenizes the argument string on whitespace, so a path like
/// macOS <c>~/Library/Application Support/Genie5</c> arrives at the file
/// manager as two fragments and Finder reports them as missing paths.
/// <c>ArgumentList</c> keeps the whole path as one argv entry regardless of
/// spaces.
/// </summary>
public static class FileBrowser
{
    /// <summary>
    /// The OS file-manager executable for the current platform: <c>explorer.exe</c>
    /// on Windows, <c>open</c> on macOS, <c>xdg-open</c> elsewhere.
    /// </summary>
    public static string FileManagerExecutable
        => OperatingSystem.IsWindows() ? "explorer.exe"
         : OperatingSystem.IsMacOS()   ? "open"
         : "xdg-open";

    /// <summary>
    /// The OS default plain-text-editor executable for the current platform:
    /// <c>notepad.exe</c> on Windows, <c>open</c> on macOS (paired with the
    /// <c>-t</c> flag in <see cref="BuildOpenInDefaultTextEditorInfo"/> to force
    /// the text-editor handler regardless of extension), <c>xdg-open</c>
    /// elsewhere (routes to the text/plain handler since Linux doesn't treat
    /// <c>.cmd</c> as executable).
    /// </summary>
    public static string DefaultTextEditorExecutable
        => OperatingSystem.IsWindows() ? "notepad.exe"
         : OperatingSystem.IsMacOS()   ? "open"
         : "xdg-open";

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> that opens <paramref name="directory"/>
    /// in the OS file browser. Does not touch the filesystem or start a process —
    /// pure construction so it can be unit-tested without launching Finder/Explorer.
    /// </summary>
    public static ProcessStartInfo BuildOpenDirectoryInfo(string directory)
    {
        var nativePath = Path.GetFullPath(directory);
        return new ProcessStartInfo(FileManagerExecutable)
        {
            ArgumentList = { nativePath },
        };
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> that opens <paramref name="file"/>
    /// in the OS default plain-text editor. Pure construction, no process launch.
    /// </summary>
    public static ProcessStartInfo BuildOpenInDefaultTextEditorInfo(string file)
    {
        var nativePath = Path.GetFullPath(file);
        var psi = new ProcessStartInfo(DefaultTextEditorExecutable);
        if (OperatingSystem.IsMacOS())
            // -t opens in the default TEXT editor regardless of extension.
            psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(nativePath);
        return psi;
    }

    /// <summary>
    /// Opens <paramref name="directory"/> in the OS file browser.
    /// When <paramref name="createIfMissing"/> is true (menu "Open …" commands),
    /// a missing directory is created first. When false (e.g. Script Manager's
    /// "reveal containing folder"), a missing directory is left alone and this
    /// returns without launching anything — there is nothing to reveal.
    /// No-ops on a null/whitespace <paramref name="directory"/>.
    /// </summary>
    /// <param name="launch">
    /// Test seam: how to launch the built <see cref="ProcessStartInfo"/>. Defaults
    /// to <see cref="Process.Start(ProcessStartInfo)"/>; unit tests pass a no-op
    /// here so running the create/reveal contract never actually opens Finder/
    /// Explorer/xdg-open on the developer's or CI's machine.
    /// </param>
    public static void OpenDirectory(string directory, bool createIfMissing = true, Action<ProcessStartInfo>? launch = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        if (!Directory.Exists(directory))
        {
            if (!createIfMissing) return;
            Directory.CreateDirectory(directory);
        }

        (launch ?? (psi => Process.Start(psi)))(BuildOpenDirectoryInfo(directory));
    }
}
