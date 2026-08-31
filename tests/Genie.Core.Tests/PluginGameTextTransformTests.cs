using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Genie.Core.Events;
using Genie.Plugins;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// The <see cref="IGeniePlugin.OnGameText"/> transform is now honored end-to-end
/// (the long-deferred Phase 2): plugins dispatch FIRST in the per-line pipeline
/// (Genie 4 order — its ParsePluginText ran before TriggerParse), and what they
/// return is what scripts, user triggers, and the UI-facing GameEvents relay
/// see; null gags the line for all of them. Same contract on the <c>#parse</c>
/// injection path (a Genie 5 divergence — Genie 4 fed #parse to plugins
/// observe-only and last). Game state stays driven by raw parser events, and a
/// plugin-injected #parse from inside the hook is not re-dispatched.
/// </summary>
public class PluginGameTextTransformTests
{
    private sealed class FakePlugin : IGeniePlugin
    {
        public string Id             => "test.gametexttransform";
        public string Name           => "GameText Transform Test";
        public string Version        => "1.0";
        public string Author         => "test";
        public string Description    => "records OnGameText calls";
        public string MinHostVersion => "";
        public bool   Enabled { get; set; } = true;

        public readonly List<(string Text, string Stream)> Seen = new();
        public Func<string, string?>? Transform;
        public IPluginHost? Host;
        public bool ParseFromInsideHook;

        public void Initialize(IPluginHost host) => Host = host;
        public void Shutdown() { }

        public string? OnGameText(string text, string stream)
        {
            Seen.Add((text, stream));
            if (ParseFromInsideHook && Host is not null)
                Host.SendCommand("#parse nested line");   // must NOT re-enter the chain
            return Transform is null ? text : Transform(text);
        }
        public string? OnInput(string input) => input;
        public void OnXml(string xml) { }
        public void OnCommandSent(string command) { }
        public void OnPrompt() { }
        public void OnVariableChanged(string name, string value) { }
    }

    private static async Task RunAsync(Func<GenieCore, FakePlugin, List<TextEvent>, List<string>, Task> body,
                                       FakePlugin? plugin = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_textxform_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
            plugin ??= new FakePlugin();
            Assert.True(core.Plugins.Register(plugin));

            var relayed  = new List<TextEvent>();
            var commands = new List<string>();
            using var sub = core.GameEvents.Subscribe(new Collector(relayed));
            core.Commands.CommandObserved = c => commands.Add(c);

            await body(core, plugin, relayed, commands);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    private sealed class Collector : IObserver<GameEvent>
    {
        private readonly List<TextEvent> _sink;
        public Collector(List<TextEvent> sink) => _sink = sink;
        public void OnNext(GameEvent e) { if (e is TextEvent te) _sink.Add(te); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    // ── Live TextEvent pipeline (ProcessGameTextEvent) ────────────────────────

    [Fact]
    public async Task Unchanged_text_relays_the_same_event_with_spans_intact()
    {
        await RunAsync((core, plugin, relayed, _) =>
        {
            var te = new TextEvent("main", "a shadowy figure arrives.",
                                   BoldSpans: new[] { new BoldSpan(2, 14) });
            core.ProcessGameTextEvent(te);

            Assert.Contains(plugin.Seen, s => s.Text == te.Text && s.Stream == "main");
            var outEvent = Assert.Single(relayed);
            Assert.Same(te, outEvent);                       // untouched instance
            Assert.NotNull(outEvent.BoldSpans);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Rewrite_reaches_the_relay_and_drops_stale_spans()
    {
        var plugin = new FakePlugin { Transform = t => t.Replace("shadowy", "sneaky") };
        await RunAsync((core, p, relayed, _) =>
        {
            core.ProcessGameTextEvent(new TextEvent("main", "a shadowy figure arrives.",
                                                    BoldSpans: new[] { new BoldSpan(2, 14) }));

            var outEvent = Assert.Single(relayed);
            Assert.Equal("a sneaky figure arrives.", outEvent.Text);
            Assert.Null(outEvent.BoldSpans);                 // offsets no longer apply
            return Task.CompletedTask;
        }, plugin);
    }

    [Fact]
    public async Task Gag_suppresses_the_relay_event_and_triggers()
    {
        var plugin = new FakePlugin { Transform = t => t.Contains("secret") ? null : t };
        await RunAsync((core, p, relayed, commands) =>
        {
            core.Triggers.AddTrigger("secret", "look");

            core.ProcessGameTextEvent(new TextEvent("main", "the secret word"));
            core.ProcessGameTextEvent(new TextEvent("main", "an ordinary line"));

            var outEvent = Assert.Single(relayed);           // only the ordinary line
            Assert.Equal("an ordinary line", outEvent.Text);
            Assert.DoesNotContain(commands, c => c.Contains("look"));   // trigger never fired
            return Task.CompletedTask;
        }, plugin);
    }

    [Fact]
    public async Task Rewrite_is_what_triggers_match_against()
    {
        var plugin = new FakePlugin { Transform = t => t.Replace("hello", "howdy") };
        await RunAsync((core, p, relayed, commands) =>
        {
            core.Triggers.AddTrigger("howdy", "wave");

            core.ProcessGameTextEvent(new TextEvent("main", "hello stranger"));

            Assert.Contains(commands, c => c.Contains("wave"));   // matched the REWRITTEN text
            return Task.CompletedTask;
        }, plugin);
    }

    // ── #parse injection path (InjectParsedLine) ──────────────────────────────

    [Fact]
    public async Task Parse_dispatches_plugins_first_on_main()
    {
        await RunAsync((core, plugin, _, _) =>
        {
            core.InjectParsedLine("synthetic line");
            Assert.Contains(plugin.Seen, s => s.Text == "synthetic line" && s.Stream == "main");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Parse_gag_stops_the_injection_before_triggers()
    {
        var plugin = new FakePlugin { Transform = _ => null };
        await RunAsync((core, p, _, commands) =>
        {
            core.Triggers.AddTrigger("synthetic", "look");

            core.InjectParsedLine("synthetic line");

            Assert.DoesNotContain(commands, c => c.Contains("look"));
            return Task.CompletedTask;
        }, plugin);
    }

    [Fact]
    public async Task Parse_from_inside_the_hook_does_not_loop()
    {
        var plugin = new FakePlugin { ParseFromInsideHook = true };
        await RunAsync((core, p, _, _) =>
        {
            core.InjectParsedLine("outer line");

            // The nested #parse ran (scripts/triggers side) but was NOT
            // re-dispatched into OnGameText — only the outer line was seen.
            Assert.Single(p.Seen);
            Assert.Equal("outer line", p.Seen[0].Text);
            return Task.CompletedTask;
        }, plugin);
    }
}
