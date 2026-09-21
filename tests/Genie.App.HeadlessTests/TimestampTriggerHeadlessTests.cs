using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Genie.App.ViewModels;
using Genie.Core;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #244 — carried over from Genie 4 #168: with window timestamps on, the
/// timestamp prefix became part of the line as far as trigger patterns were
/// concerned, so anchored patterns (<c>^You …</c>) stopped firing.
///
/// <para>The issue asked for validation first, and the answer is that Genie 5's
/// architecture already prevents it: timestamps are applied in the DISPLAY
/// layer, at append time in <c>GameTextViewModel</c>, while triggers run in
/// Core against the parsed line. The two never meet.</para>
///
/// <para>That is worth a test rather than a note, because it is an ordering
/// invariant nothing else states: the day someone moves stamping upstream of
/// <c>Triggers.ProcessLine</c> to save a pass, every anchored trigger in every
/// user's config silently stops firing — and silently is the whole problem,
/// since a trigger that does not fire reports nothing.</para>
/// </summary>
public class TimestampTriggerHeadlessTests : IAsyncLifetime
{
    private string    _dir  = "";
    private GenieCore _core = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_ts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _core.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>An anchored trigger fires on a game line while the game window
    /// is stamping — i.e. the trigger saw text with no prefix on the front.</summary>
    [AvaloniaFact]
    public void An_anchored_trigger_still_fires_while_the_window_is_stamping()
    {
        var sent = new List<string>();
        _core.ScriptOutputLine += sent.Add;
        _core.EchoLine         += sent.Add;

        // The game window's own timestamp toggle, on.
        var gameText = new GameTextViewModel();
        gameText.Settings = new Genie.Core.Layout.WindowSettings { Timestamp = true };
        gameText.Attach(_core);

        _core.Triggers.AddTrigger("^You feel fully rested", "#echo RESTED");

        _core.InjectParsedLine("You feel fully rested.");

        Assert.Contains(sent, l => l.Contains("RESTED", StringComparison.Ordinal));
    }

    /// <summary>…and the same trigger would NOT have fired against a stamped
    /// line, which is what makes the test above meaningful rather than
    /// tautological.</summary>
    [AvaloniaFact]
    public void The_same_pattern_would_miss_a_stamped_line()
    {
        // The prefix WindowTimestamp produces — a fixed 24-hour [HH:mm:ss] stamp.
        // Written out rather than called because the helper is internal to the
        // app; the format is what matters, not where it comes from.
        var stamped = $"[{DateTime.Now:HH:mm:ss}] You feel fully rested.";
        var rule    = new Genie.Core.Triggers.TriggerRule("^You feel fully rested", "#echo RESTED");

        Assert.Null(rule.SafeMatch(stamped));
        Assert.NotNull(rule.SafeMatch("You feel fully rested."));
    }
}
