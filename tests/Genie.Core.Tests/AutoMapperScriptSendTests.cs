using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Genie.Core;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// The automapper attributes each room block to the LAST command sent, and a
/// refused move ("You can't go there.") emits no room block, so only a later
/// send can supersede it. GenieCore has two send sinks: <c>SendToGame</c>
/// (typed, macros, triggers, aliases) and the delegate the script engine gets.
/// Only the first fed the mapper.
///
/// <para>Live, 2026-09-26, on the Crossing Temple's cobblestone path: a typed
/// <c>up</c> was refused, then <c>#goto</c> handed a one-move plan to
/// <c>automapper.cmd</c>, whose <c>climb massive stairway</c> went through the
/// script sink. The arrival at the Grand Stairway was attributed to the stale
/// <c>up</c>, and auto-create wrote an <c>up</c> arc onto the path and a
/// <c>down</c> arc onto the stairway — a phantom exit pair that would have
/// gone to disk on the next Save. These pin that every sink updates the
/// pending move.</para>
/// </summary>
public class AutoMapperScriptSendTests : IAsyncLifetime
{
    private string    _dir  = "";
    private GenieCore _core = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_mapsend_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _core.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void RunScript(string name, string body)
    {
        Directory.CreateDirectory(_core.Config.ScriptDir);
        File.WriteAllText(Path.Combine(_core.Config.ScriptDir, name + ".cmd"), body);
        Assert.True(_core.Scripts.TryStart(name, Array.Empty<string>()), $"script {name} did not start");
    }

    /// <summary>Tick the engine until the mapper's pending move reads
    /// <paramref name="expected"/>, or give up after five seconds.</summary>
    private void WaitForPending(string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_core.AutoMapper.PendingMove == expected) return;
            _core.Scripts.Tick();
            Thread.Sleep(10);
        }
        Assert.Equal(expected, _core.AutoMapper.PendingMove);
    }

    [Fact]
    public void A_typed_move_is_the_pending_move()
    {
        _core.Commands.ProcessInput("up");

        Assert.Equal("up", _core.AutoMapper.PendingMove);
    }

    [Fact]
    public void A_script_send_supersedes_a_refused_typed_move()
    {
        // The live sequence: `up` typed and refused (no room block, so nothing
        // clears it) …
        _core.Commands.ProcessInput("up");
        Assert.Equal("up", _core.AutoMapper.PendingMove);

        // … then a script moves the player. The arrival must be attributed to
        // the script's move, not the stale `up`.
        RunScript("climb", "put climb massive stairway\n");

        WaitForPending("climb massive stairway");
    }

    [Fact]
    public void A_script_compass_move_is_the_pending_direction()
    {
        RunScript("walk", "put northeast\n");

        WaitForPending("northeast");
    }

    [Fact]
    public void A_script_non_move_clears_a_stale_typed_move()
    {
        // Same shape as a typed `look` after a refused move: any later send
        // supersedes the pending one, movement or not, so a room block that
        // follows a script `look` is not attributed to the old `up`.
        _core.Commands.ProcessInput("up");
        RunScript("peek", "put look\n");

        WaitForPending("look");
    }
}
