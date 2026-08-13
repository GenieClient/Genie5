using System;
using System.IO;
using System.Threading;
using Genie.Core.Persistence;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Pins <see cref="RuleFileWatcher"/> — the live-reload watcher behind
/// "hand-edit a rule .json while the app is running and it applies without a
/// reconnect". Debounced FS events for the seven watched rule files raise
/// <c>RuleFileChanged</c> with the bare file name; the app's own saves
/// (marked via <see cref="RuleFileWatcher.MarkAppWrite"/>) and non-rule .json
/// files must stay silent.
/// </summary>
public class RuleFileWatcherTests : IDisposable
{
    private readonly string _dir;

    public RuleFileWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_rulewatch_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ExternalEditRaisesEventWithFileName()
    {
        using var watcher = new RuleFileWatcher(debounceMs: 100);
        using var hit = new ManualResetEventSlim();
        string? got = null;
        watcher.RuleFileChanged += name => { got = name; hit.Set(); };
        watcher.Rescope(_dir);

        File.WriteAllText(Path.Combine(_dir, "triggers.json"), "[]");

        Assert.True(hit.Wait(TimeSpan.FromSeconds(10)), "expected RuleFileChanged for triggers.json");
        Assert.Equal("triggers.json", got);
    }

    [Fact]
    public void EventBurstDebouncesToOneCallback()
    {
        using var watcher = new RuleFileWatcher(debounceMs: 200);
        var count = 0;
        using var hit = new ManualResetEventSlim();
        watcher.RuleFileChanged += _ => { Interlocked.Increment(ref count); hit.Set(); };
        watcher.Rescope(_dir);

        var path = Path.Combine(_dir, "aliases.json");
        for (var i = 0; i < 5; i++)
            File.WriteAllText(path, "[]");   // several writes well inside one debounce window

        Assert.True(hit.Wait(TimeSpan.FromSeconds(10)), "expected a debounced RuleFileChanged");
        // Let any (wrong) extra firings land before asserting the count.
        Thread.Sleep(400);
        Assert.Equal(1, count);
    }

    [Fact]
    public void AppMarkedWriteIsSuppressed()
    {
        using var watcher = new RuleFileWatcher(debounceMs: 100);
        using var hit = new ManualResetEventSlim();
        watcher.RuleFileChanged += _ => hit.Set();
        watcher.Rescope(_dir);

        var path = Path.Combine(_dir, "gags.json");
        RuleFileWatcher.MarkAppWrite(path);
        File.WriteAllText(path, "[]");

        Assert.False(hit.Wait(TimeSpan.FromSeconds(1)), "app's own save must not raise RuleFileChanged");
    }

    [Fact]
    public void NonRuleJsonIsIgnored()
    {
        using var watcher = new RuleFileWatcher(debounceMs: 100);
        using var hit = new ManualResetEventSlim();
        watcher.RuleFileChanged += _ => hit.Set();
        watcher.Rescope(_dir);

        File.WriteAllText(Path.Combine(_dir, "display.json"), "{}");

        Assert.False(hit.Wait(TimeSpan.FromSeconds(1)), "unwatched .json must not raise RuleFileChanged");
    }

    [Fact]
    public void RescopeCreatesMissingDirectoryAndWatchesIt()
    {
        var sub = Path.Combine(_dir, "Profiles", "Renucci-ACCT");
        using var watcher = new RuleFileWatcher(debounceMs: 100);
        using var hit = new ManualResetEventSlim();
        watcher.RuleFileChanged += _ => hit.Set();
        watcher.Rescope(sub);   // dir does not exist yet — Rescope must create it

        Assert.True(Directory.Exists(sub));
        File.WriteAllText(Path.Combine(sub, "variables.json"), "[]");

        Assert.True(hit.Wait(TimeSpan.FromSeconds(10)), "expected RuleFileChanged from the created dir");
    }
}
