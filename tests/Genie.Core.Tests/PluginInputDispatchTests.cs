using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Genie.Plugins;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Plugin input dispatch (<see cref="IGeniePlugin.OnInput"/> — Genie 4
/// ParseInput parity): user-typed input must flow through the loaded plugins
/// BEFORE the command engine sees it, so a command-driven plugin (e.g.
/// InventoryView's <c>/iv</c>) can swallow its own commands or rewrite the
/// line. Regression tests for the field report where <c>/iv open</c> never
/// reached the plugin — <c>PluginManager.DispatchInput</c> existed but no
/// caller wired it into <c>GenieCore.ProcessInput</c>, so plugin commands
/// leaked to the game as literal sends.
/// </summary>
public class PluginInputDispatchTests
{
    /// <summary>Minimal observe/transform plugin: records every OnInput line
    /// and applies an optional per-test transform (null = swallow).</summary>
    private sealed class FakePlugin : IGeniePlugin
    {
        public string Id             => "test.inputdispatch";
        public string Name           => "Input Dispatch Test";
        public string Version        => "1.0";
        public string Author         => "test";
        public string Description    => "records OnInput calls";
        public string MinHostVersion => "";
        public bool   Enabled { get; set; } = true;

        public readonly List<string> SeenInputs = new();
        public Func<string, string?>? Transform;

        public void Initialize(IPluginHost host) { }
        public void Shutdown() { }

        public string? OnGameText(string text, string stream) => text;
        public string? OnInput(string input)
        {
            SeenInputs.Add(input);
            return Transform is null ? input : Transform(input);
        }
        public void OnXml(string xml) { }
        public void OnCommandSent(string command) { }
        public void OnPrompt() { }
        public void OnVariableChanged(string name, string value) { }
    }

    /// <summary>Run one ProcessInput call against a fresh offline core with the
    /// fake plugin registered. Returns the plugin plus every top-level command
    /// the command engine observed (empty = the line never reached it).</summary>
    private static async Task<(FakePlugin plugin, List<string> observed)> RunAsync(
        string input, Func<string, string?>? transform = null, bool enabled = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_plugininput_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
            var plugin = new FakePlugin { Transform = transform, Enabled = enabled };
            Assert.True(core.Plugins.Register(plugin));

            var observed = new List<string>();
            core.Commands.CommandObserved = cmd => observed.Add(cmd);

            core.ProcessInput(input);
            return (plugin, observed);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Plugin_sees_typed_input_before_the_command_engine()
    {
        var (plugin, observed) = await RunAsync("look");

        Assert.Contains("look", plugin.SeenInputs);
        Assert.Contains("look", observed);   // pass-through still reaches the engine
    }

    [Fact]
    public async Task Swallowed_input_never_reaches_the_command_engine()
    {
        // The command-driven-plugin case (originally InventoryView's "/iv open",
        // now a neutral name — /iv belongs to the builtin extension since the
        // absorption): OnInput returns null and the line must go nowhere else.
        var (plugin, observed) = await RunAsync("/fakeplug open", transform: _ => null);

        Assert.Contains("/fakeplug open", plugin.SeenInputs);
        Assert.Empty(observed);
    }

    [Fact]
    public async Task Transformed_input_is_what_the_command_engine_receives()
    {
        var (_, observed) = await RunAsync("hello", transform: _ => "goodbye");

        Assert.Contains("goodbye", observed);
        Assert.DoesNotContain("hello", observed);
    }

    [Fact]
    public async Task Disabled_plugin_is_skipped_and_input_flows_through()
    {
        var (plugin, observed) = await RunAsync("/fakeplug open", transform: _ => null, enabled: false);

        Assert.Empty(plugin.SeenInputs);              // disabled → no callback
        Assert.Contains("/fakeplug open", observed);  // line proceeds untouched
    }
}
