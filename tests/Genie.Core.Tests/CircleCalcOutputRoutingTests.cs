using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Extensions;
using Genie.Core.Extensions.Builtin.CircleCalc;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #207 — Genie 4 parity for the CircleCalc display/parse toggles. The
/// result honours two independent settings, both default on:
/// <c>$CircleCalc.Echo = 0</c> suppresses the window display; <c>$CircleCalc.Parse
/// = 0</c> stops it feeding script actions/triggers. The reporter's case is
/// <c>Echo 0 + Parse 1</c> — consume the output from a script without cluttering
/// the game window.
/// </summary>
public class CircleCalcOutputRoutingTests
{
    [Fact]
    public void Default_result_is_both_displayed_and_parsed()
    {
        var routed = RunSort(echo: null, parse: null);
        Assert.NotEmpty(routed);
        Assert.All(routed, r => { Assert.True(r.Display); Assert.True(r.Parse); });
    }

    [Fact]
    public void Echo_off_suppresses_display_but_still_feeds_actions()
    {
        // The reporter's scenario: parse the output silently.
        var routed = RunSort(echo: "0", parse: null);
        Assert.NotEmpty(routed);
        Assert.All(routed, r => { Assert.False(r.Display); Assert.True(r.Parse); });
    }

    [Fact]
    public void Parse_off_still_displays_but_does_not_feed_actions()
    {
        var routed = RunSort(echo: null, parse: "0");
        Assert.NotEmpty(routed);
        Assert.All(routed, r => { Assert.True(r.Display); Assert.False(r.Parse); });
    }

    [Fact]
    public void Both_off_emits_nothing_to_either_leg()
    {
        var routed = RunSort(echo: "0", parse: "0");
        Assert.NotEmpty(routed);
        Assert.All(routed, r => { Assert.False(r.Display); Assert.False(r.Parse); });
    }

    // ── drive a /sort end-to-end and return the routed result lines ──────────
    private static List<(string Text, bool Display, bool Parse)> RunSort(string? echo, string? parse)
    {
        var host = new CaptureHost();
        if (echo  is not null) host.Globals["CircleCalc.Echo"]  = echo;
        if (parse is not null) host.Globals["CircleCalc.Parse"] = parse;

        var ext = new CircleCalcExtension();
        ext.Initialize(host);                       // no data files in ConfigDir — plain /sort needs none

        Assert.True(ext.OnSlashCommand("/sort"));   // → Mode.Sorting, sends "exp all"
        ext.OnGameLine("Circle: 50");               // exp-dump header → start reading skills
        ext.OnGameLine("Small Edged:  142 71% examining");
        ext.OnPrompt();                             // footer fallback → Finish() → EmitResult

        return host.Routed;
    }

    private sealed class CaptureHost : IExtensionHost
    {
        public IDictionary<string, string> Globals { get; } = new Dictionary<string, string>();
        public List<(string Text, bool Display, bool Parse)> Routed { get; } = new();

        // Result output flows through EchoRouted — capture the two legs.
        public void EchoRouted(string text, bool display, bool parse) => Routed.Add((text, display, parse));

        // Usage/error lines use plain Echo; not exercised by a successful /sort.
        public void Echo(string text) { }
        public void SendCommand(string command) { }
        public void SetWindow(string window, string content) { }
        public string ConfigDir => Path.Combine(Path.GetTempPath(), "cc-routing-" + Guid.NewGuid().ToString("N"));
        public void Log(string message) { }
        public string? GetUserVar(string name) => null;
    }
}
