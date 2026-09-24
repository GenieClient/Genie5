using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// 2026-08-31 stability review — a loop that registers <c>match</c>/<c>matchre</c>
/// every pass but never reaches <c>matchwait</c> grew the pending-match list for
/// the script's lifetime. It is now capped (oldest dropped, one warning per run),
/// and a matchwait that finally arms still sees the newest registrations.
/// </summary>
public class PendingMatchCapTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _echoed = new();
    private readonly ScriptEngine _engine;

    public PendingMatchCapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gc_matchcap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _engine = new ScriptEngine(_dir, new TypeAheadSession(), sendCommand: _ => { }, echo: _echoed.Add);
    }

    public void Dispose()
    {
        _engine.StopAll();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void Pump(int ticks = 50)
    {
        for (int i = 0; i < ticks; i++) _engine.Tick();
    }

    private const int Registrations = ScriptEngine.PendingMatchCap + 150;

    private void StartLoop(string tail) =>
        StartScript(
            "var i 0\n" +
            ":top\n" +
            "match nowhere never-going-to-appear\n" +
            "matchre nowhere ^never$\n" +
            "math i add 1\n" +
            $"if %i < {Registrations / 2} then goto top\n" +
            tail);

    private void StartScript(string body)
    {
        File.WriteAllText(Path.Combine(_dir, "t.cmd"), body);
        Assert.True(_engine.TryStart("t", Array.Empty<string>()));
    }

    [Fact]
    public void Registrations_without_a_matchwait_are_capped_and_warn_once()
    {
        StartLoop("pause 30\n");   // park with the list still full
        Pump();

        var inst = Assert.Single(_engine.Instances);
        Assert.Equal(ScriptEngine.PendingMatchCap, inst.PendingMatches.Count);
        Assert.Single(_echoed, l => l.Contains("match patterns registered without a matchwait"));
    }

    [Fact]
    public void A_matchwait_after_the_cap_still_sees_the_newest_registration()
    {
        StartLoop(
            "match done FOUND IT\n" +
            "matchwait\n" +
            "echo NO-MATCH\n" +
            "exit\n" +
            ":done\n" +
            "echo DONE\n");
        Pump();

        _engine.OnGameLine("FOUND IT");
        Pump();

        Assert.Contains("DONE", _echoed);
        Assert.DoesNotContain("NO-MATCH", _echoed);
    }

    [Fact]
    public void Ordinary_registration_counts_are_untouched()
    {
        StartScript(
            "match a one\n" +
            "match b two\n" +
            "matchre c ^three$\n" +
            "pause 30\n");
        Pump();

        Assert.Equal(3, Assert.Single(_engine.Instances).PendingMatches.Count);
        Assert.DoesNotContain(_echoed, l => l.Contains("without a matchwait"));
    }
}
