using System;
using System.IO;
using Genie.App.Diagnostics;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The AutoLog session writer's failure and flush policy. Opening the file used
/// to happen unguarded inside the connect, so a second instance logging the
/// same character on the same day (the file already open) threw out of
/// ConnectAsync and aborted the login. It also flushed every line
/// synchronously on the UI thread. Now a failed open leaves logging off and
/// reports why, and lines are flushed on a bounded interval plus at Stop.
/// </summary>
public class SessionTextLoggerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "genie_autolog_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private string ExpectedFile(string ch, string game) =>
        Path.Combine(_dir, $"{ch}{game}_{DateTime.Now:yyyy-MM-dd}.log");

    [Fact]
    public void An_open_failure_does_not_throw_and_leaves_logging_off()
    {
        using var logger = new SessionTextLogger(_dir);
        // Another instance holding the file exclusively — the real-world case.
        using var holder = new FileStream(ExpectedFile("Renucci", "DR"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var started = true;
        var ex = Record.Exception(() =>
            started = logger.Start(new GameTextViewModel(), "Renucci", "DR"));

        Assert.Null(ex);
        Assert.False(started);
        Assert.False(logger.IsLogging);
        Assert.Null(logger.CurrentFile);
        Assert.False(string.IsNullOrEmpty(logger.LastError));
    }

    [Fact]
    public void A_later_successful_start_clears_the_error()
    {
        using var logger = new SessionTextLogger(_dir);
        using (new FileStream(ExpectedFile("Renucci", "DR"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.False(logger.Start(new GameTextViewModel(), "Renucci", "DR"));

        Assert.True(logger.Start(new GameTextViewModel(), "Renucci", "DR"));
        Assert.True(logger.IsLogging);
        Assert.Null(logger.LastError);
    }

    [Fact]
    public void Lines_reach_the_file_on_flush_and_on_stop()
    {
        var text = new GameTextViewModel();
        using var logger = new SessionTextLogger(_dir);
        Assert.True(logger.Start(text, "Renucci", "DR"));

        text.AddSystemLine("first line");
        logger.FlushIfDirty();
        Assert.Contains("first line", ReadShared(logger.CurrentFile!));

        text.AddSystemLine("tail line");
        var file = logger.CurrentFile!;
        logger.Stop();
        var lines = File.ReadAllLines(file);
        Assert.Contains(lines, l => l.Contains("first line"));
        Assert.Contains(lines, l => l.Contains("tail line"));
    }

    [Fact]
    public void The_flush_timer_lands_a_line_without_a_stop()
    {
        var text = new GameTextViewModel();
        using var logger = new SessionTextLogger(_dir);
        Assert.True(logger.Start(text, "Renucci", "DR"));

        text.AddSystemLine("timer flushed");
        var deadline = DateTime.UtcNow + SessionTextLogger.FlushInterval + TimeSpan.FromSeconds(10);
        while (!ReadShared(logger.CurrentFile!).Contains("timer flushed") && DateTime.UtcNow < deadline)
            System.Threading.Thread.Sleep(50);

        Assert.Contains("timer flushed", ReadShared(logger.CurrentFile!));
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r  = new StreamReader(fs);
        return r.ReadToEnd();
    }
}
