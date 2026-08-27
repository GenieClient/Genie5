using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #297: an action-dispatched <c>goto</c> firing while the script is
/// blocked (pause / matchwait / waitfor / waiteval) moved the program counter
/// but never cleared the blocking state, so the step gate kept the script
/// parked forever. Genie 4 parity (Script.cs:2299-2303, the action-dispatch
/// path): an ACTION goto abandons the block — including match patterns armed
/// before the matchwait — and resumes at the target label; a NORMAL goto
/// clears nothing, which the register-matches-then-goto-to-a-shared-matchwait
/// idiom depends on. The player's own pause (UserPaused) always survives.
/// </summary>
public class ActionGotoUnblockTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _echoed = new();
    private readonly List<string> _sent   = new();
    private readonly ScriptEngine _engine;

    public ActionGotoUnblockTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gc_actiongoto_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _engine = new ScriptEngine(_dir, new TypeAheadSession(),
                                   sendCommand: c => _sent.Add(c), echo: l => _echoed.Add(l));
    }

    public void Dispose()
    {
        _engine.StopAll();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void Start(string name, string body)
    {
        File.WriteAllText(Path.Combine(_dir, name + ".cmd"), body);
        Assert.True(_engine.TryStart(name, Array.Empty<string>()));
    }

    private void Pump(int ticks = 50)
    {
        for (int i = 0; i < ticks; i++) _engine.Tick();
    }

    private bool Echoed(string fragment) =>
        _echoed.Any(l => l.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public void Action_goto_during_pause_resumes_at_label()
    {
        // The issue's exact repro shape: `action goto stats when ^report`,
        // script parked in a long pause, "report" arrives.
        Start("s",
            "action goto stats when ^report\n" +
            ":TOP\n" +
            "pause 1000\n" +
            "goto TOP\n" +
            ":stats\n" +
            "echo STATS-REACHED\n");
        Pump();
        Assert.False(Echoed("STATS-REACHED"));

        _engine.OnGameLine("report");
        Pump();

        Assert.True(Echoed("STATS-REACHED"));
    }

    [Fact]
    public void Action_goto_during_matchwait_resumes_and_clears_armed_patterns()
    {
        Start("s",
            "action goto rescue when ^EMERGENCY\n" +
            ":TOP\n" +
            "match wrong JACKPOT\n" +
            "matchwait 300\n" +
            ":rescue\n" +
            "echo RESCUED\n" +
            "match right OTHERTEXT\n" +
            "matchwait 300\n" +
            ":wrong\n" +
            "echo STALE-PATTERN-FIRED\n" +
            "pause 300\n" +
            ":right\n" +
            "echo FRESH-PATTERN-FIRED\n");
        Pump();

        _engine.OnGameLine("EMERGENCY");
        Pump();
        Assert.True(Echoed("RESCUED"));

        // The pre-goto pattern must be gone: JACKPOT may not jump to :wrong
        // out of the SECOND matchwait...
        _engine.OnGameLine("JACKPOT");
        Pump();
        Assert.False(Echoed("STALE-PATTERN-FIRED"));

        // ...while the freshly armed pattern still works.
        _engine.OnGameLine("OTHERTEXT");
        Pump();
        Assert.True(Echoed("FRESH-PATTERN-FIRED"));
    }

    [Fact]
    public void Action_goto_during_waitfor_resumes_at_label()
    {
        Start("s",
            "action goto out when ^breakout\n" +
            ":TOP\n" +
            "waitfor NEVERCOMES\n" +
            ":out\n" +
            "echo OUT-REACHED\n");
        Pump();
        Assert.False(Echoed("OUT-REACHED"));

        _engine.OnGameLine("breakout");
        Pump();
        Assert.True(Echoed("OUT-REACHED"));
    }

    [Fact]
    public void Action_goto_during_waiteval_resumes_at_label()
    {
        Start("s",
            "action goto out when ^breakout\n" +
            ":TOP\n" +
            "waiteval 1 = 2\n" +
            ":out\n" +
            "echo OUT-REACHED\n");
        Pump();
        Assert.False(Echoed("OUT-REACHED"));

        _engine.OnGameLine("breakout");
        Pump();
        Assert.True(Echoed("OUT-REACHED"));
    }

    [Fact]
    public void User_pause_survives_an_action_goto()
    {
        Start("s",
            "action goto stats when ^report\n" +
            ":TOP\n" +
            "pause 1000\n" +
            "goto TOP\n" +
            ":stats\n" +
            "echo STATS-REACHED\n");
        Pump();

        _engine.PauseScript("s");
        _engine.OnGameLine("report");
        Pump();
        // The action fired and cleared the script's own pause, but the
        // player's pause still gates stepping.
        Assert.False(Echoed("STATS-REACHED"));

        _engine.ResumeScript("s");
        Pump();
        Assert.True(Echoed("STATS-REACHED"));
    }

    [Fact]
    public void Normal_goto_does_not_clear_accumulated_match_patterns()
    {
        // The register-matches-then-goto-to-a-shared-matchwait idiom: patterns
        // added before a NORMAL goto must survive the jump and arm the
        // matchwait at the target.
        Start("s",
            ":TOP\n" +
            "match win JACKPOT\n" +
            "goto ARM\n" +
            ":ARM\n" +
            "matchwait 300\n" +
            ":win\n" +
            "echo WON\n");
        Pump();

        _engine.OnGameLine("JACKPOT");
        Pump();
        Assert.True(Echoed("WON"));
    }
}
