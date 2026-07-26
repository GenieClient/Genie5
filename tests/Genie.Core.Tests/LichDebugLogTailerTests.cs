using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Genie.Core.Connection;
using Xunit;

namespace Genie.Core.Tests;

public class LichDebugLogTailerTests
{
    [Fact]
    public void ResolveTempDirectory_defaults_to_lich_sibling_temp()
    {
        var lich = Path.Combine(Path.GetTempPath(), "lich-home", "lich.rbw");
        var dir = LichDebugLogTailer.ResolveTempDirectory(lich, null);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "lich-home", "temp"), dir);
    }

    [Fact]
    public void ResolveTempDirectory_prefers_temp_equals_arg()
    {
        var custom = Path.Combine(Path.GetTempPath(), "custom-lich-temp");
        var dir = LichDebugLogTailer.ResolveTempDirectory(
            "/unused/lich.rbw",
            $"--login Char --temp={custom}");
        Assert.Equal(custom.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), dir);
    }

    [Fact]
    public void ResolveTempDirectory_accepts_temp_space_form_and_temp_dir()
    {
        var a = Path.Combine(Path.GetTempPath(), "temp-a");
        var b = Path.Combine(Path.GetTempPath(), "temp-b");
        Assert.Equal(a, LichDebugLogTailer.ResolveTempDirectory("x", $"--temp {a}"));
        Assert.Equal(b, LichDebugLogTailer.ResolveTempDirectory("x", $"--temp-dir={b}"));
    }

    [Fact]
    public void ResolveTempDirectory_handles_a_quoted_temp_path_with_spaces()
    {
        var spaced = Path.Combine(Path.GetTempPath(), "Application Support", "lich", "temp");

        Assert.Equal(spaced, LichDebugLogTailer.ResolveTempDirectory(
            "/unused/lich.rbw", $"--login Char --temp \"{spaced}\""));
        Assert.Equal(spaced, LichDebugLogTailer.ResolveTempDirectory(
            "/unused/lich.rbw", $"--temp=\"{spaced}\" --without-frontend"));
        Assert.Equal(spaced, LichDebugLogTailer.ResolveTempDirectory(
            "/unused/lich.rbw", $"--temp-dir '{spaced}'"));
    }

    [Fact]
    public void ResolveTempDirectory_throws_rather_than_falling_back_on_a_bad_quote()
    {
        // Silently falling back to {lichdir}/temp would tail the wrong directory
        // and read as "Lich just isn't logging".
        Assert.Throws<FormatException>(() =>
            LichDebugLogTailer.ResolveTempDirectory("/unused/lich.rbw", "--temp \"/a b/temp"));
    }

    [Fact]
    public void TryFindLatestDebugLog_ignores_files_written_before_notBefore()
    {
        var tempDir = Directory.CreateTempSubdirectory("lich-debug-old-").FullName;
        try
        {
            var oldPath = Path.Combine(tempDir, "debug-old.log");
            File.WriteAllText(oldPath, "old\n");
            var oldWrite = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(oldPath, oldWrite);

            var notBefore = DateTime.UtcNow.AddMinutes(-1);
            Assert.Null(LichDebugLogTailer.TryFindLatestDebugLog(tempDir, notBefore));

            var newPath = Path.Combine(tempDir, "debug-new.log");
            File.WriteAllText(newPath, "new\n");
            File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow);

            var found = LichDebugLogTailer.TryFindLatestDebugLog(tempDir, notBefore);
            Assert.Equal(newPath, found);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void TryFindLatestDebugLog_picks_newest_eligible()
    {
        var tempDir = Directory.CreateTempSubdirectory("lich-debug-pick-").FullName;
        try
        {
            var notBefore = DateTime.UtcNow.AddMinutes(-1);
            var older = Path.Combine(tempDir, "debug-a.log");
            var newer = Path.Combine(tempDir, "debug-b.log");
            File.WriteAllText(older, "a\n");
            File.WriteAllText(newer, "b\n");
            File.SetLastWriteTimeUtc(older, notBefore.AddSeconds(10));
            File.SetLastWriteTimeUtc(newer, notBefore.AddSeconds(20));

            Assert.Equal(newer, LichDebugLogTailer.TryFindLatestDebugLog(tempDir, notBefore));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Tailer_emits_appended_lines()
    {
        var tempDir = Directory.CreateTempSubdirectory("lich-debug-tail-").FullName;
        try
        {
            var notBefore = DateTime.UtcNow.AddSeconds(-2);
            var logPath = Path.Combine(tempDir, "debug-session.log");
            await File.WriteAllTextAsync(logPath, "first\n");
            File.SetLastWriteTimeUtc(logPath, DateTime.UtcNow);

            var lines = new ConcurrentQueue<string>();
            var bound = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gotSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var tailer = new LichDebugLogTailer();
            tailer.Start(
                tempDir,
                notBefore,
                onLine: line =>
                {
                    lines.Enqueue(line);
                    if (line == "second") gotSecond.TrySetResult();
                },
                onFileBound: path => bound.TrySetResult(path));

            var boundPath = await bound.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(logPath, boundPath);

            // Wait until the initial "first" line is drained, then append.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!lines.Contains("first") && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.Contains("first", lines);

            await File.AppendAllTextAsync(logPath, "second\n");
            await gotSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("second", lines);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }
}
