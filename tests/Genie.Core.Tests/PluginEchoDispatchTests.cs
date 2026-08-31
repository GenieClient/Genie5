using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Genie.Plugins;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Plugin echo dispatch (<see cref="IGeniePlugin.OnEcho"/>) — a deliberate
/// Genie 5 extension beyond Genie 4 (which never ran echoes through
/// <c>ParseText</c>): every echoed display line (<c>#echo</c> plain / styled /
/// directed, script <c>echo</c>, host messages) flows through the plugin chain
/// before its display event fires, and the transform is honored — a plugin can
/// rewrite the line or gag it entirely. Plugin-emitted echoes from inside
/// <c>OnEcho</c> must not re-dispatch (feedback-loop guard).
/// </summary>
public class PluginEchoDispatchTests
{
    private sealed class FakePlugin : IGeniePlugin
    {
        public string Id             => "test.echodispatch";
        public string Name           => "Echo Dispatch Test";
        public string Version        => "1.0";
        public string Author         => "test";
        public string Description    => "records OnEcho calls";
        public string MinHostVersion => "";
        public bool   Enabled { get; set; } = true;

        public readonly List<(string Text, string Window)> SeenEchoes = new();
        public Func<string, string?>? Transform;
        public IPluginHost? Host;
        public bool EchoFromInsideHook;

        public void Initialize(IPluginHost host) => Host = host;
        public void Shutdown() { }

        public string? OnGameText(string text, string stream) => text;
        public string? OnInput(string input) => input;
        public string? OnEcho(string text, string window)
        {
            SeenEchoes.Add((text, window));
            if (EchoFromInsideHook && Host is not null)
                Host.Echo("nested echo");   // must NOT re-enter the chain
            return Transform is null ? text : Transform(text);
        }
        public void OnXml(string xml) { }
        public void OnCommandSent(string command) { }
        public void OnPrompt() { }
        public void OnVariableChanged(string name, string value) { }
    }

    /// <summary>Fresh offline core with the fake plugin registered and all echo
    /// surfaces captured.</summary>
    private static async Task<T> WithCoreAsync<T>(Func<GenieCore, FakePlugin, List<string>, Task<T>> body,
                                                  FakePlugin? plugin = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_pluginecho_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
            plugin ??= new FakePlugin();
            Assert.True(core.Plugins.Register(plugin));

            var displayed = new List<string>();
            core.EchoLine         += t => displayed.Add(t);
            core.ScriptOutputLine += t => displayed.Add("[script] " + t);
            core.EchoToWindow     += (t, w, _) => displayed.Add($"[>{w}] {t}");

            return await body(core, plugin, displayed);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Echo_command_flows_through_the_plugin_before_display()
    {
        await WithCoreAsync<object?>(async (core, plugin, displayed) =>
        {
            core.ProcessInput("#echo hello there");

            Assert.Contains(plugin.SeenEchoes, e => e.Text.Contains("hello there") && e.Window == "main");
            Assert.Contains(displayed, l => l.Contains("hello there"));
            return await Task.FromResult<object?>(null);
        });
    }

    [Fact]
    public async Task Plugin_can_rewrite_an_echo()
    {
        var plugin = new FakePlugin { Transform = t => t.Replace("hello", "howdy") };
        await WithCoreAsync<object?>(async (core, p, displayed) =>
        {
            core.ProcessInput("#echo hello there");

            Assert.Contains(displayed, l => l.Contains("howdy there"));
            Assert.DoesNotContain(displayed, l => l.Contains("hello there"));
            return await Task.FromResult<object?>(null);
        }, plugin);
    }

    [Fact]
    public async Task Plugin_can_gag_an_echo()
    {
        var plugin = new FakePlugin { Transform = t => t.Contains("secret") ? null : t };
        await WithCoreAsync<object?>(async (core, p, displayed) =>
        {
            core.ProcessInput("#echo the secret line");
            core.ProcessInput("#echo the public line");

            Assert.DoesNotContain(displayed, l => l.Contains("secret"));
            Assert.Contains(displayed, l => l.Contains("public line"));
            return await Task.FromResult<object?>(null);
        }, plugin);
    }

    [Fact]
    public async Task Directed_echo_reports_its_target_window()
    {
        await WithCoreAsync<object?>(async (core, plugin, displayed) =>
        {
            core.ProcessInput("#echo >thoughts routed line");

            Assert.Contains(plugin.SeenEchoes,
                            e => e.Text.Contains("routed line")
                                 && e.Window.Equals("thoughts", StringComparison.OrdinalIgnoreCase));
            return await Task.FromResult<object?>(null);
        });
    }

    [Fact]
    public async Task Plugin_echo_from_inside_the_hook_does_not_loop()
    {
        var plugin = new FakePlugin { EchoFromInsideHook = true };
        await WithCoreAsync<object?>(async (core, p, displayed) =>
        {
            core.ProcessInput("#echo trigger it");

            // The nested host.Echo displays but is NOT re-dispatched into OnEcho —
            // exactly one OnEcho call for the original line.
            Assert.Single(p.SeenEchoes, e => e.Text.Contains("trigger it"));
            Assert.DoesNotContain(p.SeenEchoes, e => e.Text.Contains("nested echo"));
            Assert.Contains(displayed, l => l.Contains("nested echo"));
            return await Task.FromResult<object?>(null);
        }, plugin);
    }

    [Fact]
    public async Task Disabled_plugin_sees_nothing_and_text_passes_through()
    {
        var plugin = new FakePlugin { Transform = _ => null, Enabled = false };
        await WithCoreAsync<object?>(async (core, p, displayed) =>
        {
            core.ProcessInput("#echo passes through");

            Assert.Empty(p.SeenEchoes);
            Assert.Contains(displayed, l => l.Contains("passes through"));
            return await Task.FromResult<object?>(null);
        }, plugin);
    }
}
