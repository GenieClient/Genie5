using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #367 — a plugin's <c>EchoToWindow</c> and a script's <c>#echo &gt;name</c>
/// raise the same <see cref="GenieCore.EchoToWindow"/> event, so the host could not
/// tell their windows apart. The plugin path now announces itself first.
/// </summary>
public class PluginWindowOriginTests
{
    private static async Task WithCore(Func<GenieCore, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "genie_winorigin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
            await body(core);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public Task Plugin_echo_announces_the_window_before_the_echo_arrives() => WithCore(core =>
    {
        var order = new List<string>();
        core.PluginWindowWritten += w => order.Add("owner:" + w);
        core.EchoToWindow += (_, w, _) => order.Add("echo:" + w);

        ((Genie.Plugins.IPluginHost)core).EchoToWindow("Spider", "a line");

        Assert.Equal(new[] { "owner:Spider", "echo:Spider" }, order);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Script_echo_to_a_window_does_not_claim_it_for_a_plugin() => WithCore(core =>
    {
        var owners = new List<string>();
        var echoes = new List<string?>();
        core.PluginWindowWritten += owners.Add;
        core.EchoToWindow += (_, w, _) => echoes.Add(w);

        core.Commands.ProcessInput("#echo >ScriptMenu hello");

        Assert.Contains("ScriptMenu", echoes);
        Assert.Empty(owners);
        return Task.CompletedTask;
    });
}
