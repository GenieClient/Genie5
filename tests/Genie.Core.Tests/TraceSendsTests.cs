using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Genie.Core;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #306 — <c>#config tracesends</c>, the diagnostic for commands the
/// user never typed.
///
/// <para>The reported failure mode is four phantom commands after
/// <c>ask clerk for help</c>, each answered with "Please rephrase that
/// command." and <b>none of them echoed locally</b>. That last part is what
/// makes the class of bug undiagnosable from the user's side: the game window
/// shows DR's complaints with nothing above them to explain the send. #256
/// (the OOC verb triple-send) had the same shape and would have been
/// self-diagnosing with this on.</para>
///
/// <para>The origin matters as much as the text. A phantom that reports
/// <c>trigger</c> or <c>alias</c> names the subsystem to look in; one that
/// reports <c>typed</c> means the command pipeline is multiplying a real
/// keystroke, which is a different bug entirely.</para>
/// </summary>
public class TraceSendsTests : IAsyncLifetime
{
    private string    _dir  = "";
    private GenieCore _core = null!;
    private readonly List<string> _echoed = new();

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_trace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
        _core.EchoLine += _echoed.Add;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _core.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private IEnumerable<string> Traces =>
        _echoed.Where(l => l.StartsWith("[send:", StringComparison.Ordinal));

    [Fact]
    public void Off_by_default_so_nobody_gets_a_second_copy_of_every_command()
    {
        Assert.False(_core.Config.TraceSends);

        _core.Commands.ProcessInput("look");

        Assert.Empty(Traces);
    }

    [Fact]
    public void Typed_input_is_attributed_to_the_user()
    {
        _core.Config.SetSetting("tracesends", "true", showException: false);

        _core.Commands.ProcessInput("look");

        Assert.Contains("[send:typed] look", Traces);
    }

    /// <summary>The case the report needs: a command nobody typed, named.</summary>
    [Fact]
    public void A_trigger_action_is_attributed_to_the_trigger()
    {
        _core.Config.SetSetting("tracesends", "true", showException: false);
        _core.Triggers.AddTrigger("^a clerk asks", "bow clerk");

        _core.Triggers.ProcessLine("a clerk asks you something");

        Assert.Contains("[send:trigger] bow clerk", Traces);
    }

    [Fact]
    public void An_alias_expansion_is_attributed_to_the_alias()
    {
        _core.Config.SetSetting("tracesends", "true", showException: false);
        _core.Aliases.AddAlias("gp", "get my pack");

        _core.Commands.ProcessInput("gp");

        Assert.Contains("[send:alias] get my pack", Traces);
    }

    /// <summary>The origin is restored after a dispatch — alias expansion
    /// recurses back into ProcessInput, so a latched origin would mislabel
    /// every later command as coming from whatever ran last.</summary>
    [Fact]
    public void The_origin_does_not_latch_after_a_rule_fires()
    {
        _core.Config.SetSetting("tracesends", "true", showException: false);
        _core.Aliases.AddAlias("gp", "get my pack");

        _core.Commands.ProcessInput("gp");
        _echoed.Clear();
        _core.Commands.ProcessInput("look");

        Assert.Contains("[send:typed] look", Traces);
        Assert.DoesNotContain(Traces, t => t.Contains("[send:alias] look", StringComparison.Ordinal));
    }
}
