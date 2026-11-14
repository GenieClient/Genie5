using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #406 — every local echo line says what produced it, so the game window
/// colours it with the matching Genie 4 preset: a typed command <c>inputuser</c>,
/// a script-sent command <c>inputother</c>, script output and Genie messages
/// <c>scriptecho</c>. The untyped EchoLine / ScriptOutputLine events still fire.
/// </summary>
public class ClassifiedEchoTests : IAsyncLifetime
{
    private string    _dir  = "";
    private GenieCore _core = null!;
    private readonly List<(string Text, EchoKind Kind)> _lines = new();

    public Task InitializeAsync()
    {
        _dir  = Path.Combine(Path.GetTempPath(), "genie_echokind_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
        _core.ClassifiedEchoLine += (t, k) => _lines.Add((t, k));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _core.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Typed_command_is_a_Command()
    {
        _core.Commands.ProcessInput("look");
        Assert.Contains(("look", EchoKind.Command), _lines);
    }

    [Fact]
    public void Hash_echo_is_a_Message()
    {
        _core.Commands.ProcessInput("#echo hello there");
        Assert.Contains(("hello there", EchoKind.Message), _lines);
        Assert.DoesNotContain(_lines, l => l.Kind == EchoKind.Command);
    }

    [Fact]
    public void Script_sends_are_ScriptCommand_and_script_output_is_ScriptEcho()
    {
        var file = Path.Combine(_dir, "echokind_probe.cmd");
        File.WriteAllText(file, "put look\necho PROBE-OUT\nexit\n");
        Assert.True(_core.Scripts.TryStartFile(file));
        for (int i = 0; i < 50; i++) _core.Scripts.Tick();

        Assert.Contains(("look", EchoKind.ScriptCommand), _lines);
        Assert.Contains(_lines, l => l.Text.Contains("PROBE-OUT") && l.Kind == EchoKind.ScriptEcho);
    }

    [Fact]
    public void Untyped_events_still_fire_once_per_line()
    {
        var echo = new List<string>();
        _core.EchoLine += echo.Add;
        _core.Commands.ProcessInput("look");
        Assert.Single(echo, "look");
    }

    [Theory]
    [InlineData(EchoKind.Command,       "inputuser")]
    [InlineData(EchoKind.ScriptCommand, "inputother")]
    [InlineData(EchoKind.ScriptEcho,    "scriptecho")]
    [InlineData(EchoKind.Message,       "scriptecho")]
    public void Each_kind_maps_to_its_Genie4_preset(EchoKind kind, string preset)
        => Assert.Equal(preset, kind.PresetId());
}
