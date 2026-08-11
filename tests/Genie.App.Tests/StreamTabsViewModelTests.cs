using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Events;
using Genie.Core.Layout;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Coverage for <see cref="StreamTabsViewModel.RouteToMain"/> — the private
/// decision method (~line 157) that resolves whether a side-stream line
/// (talk, whispers, combat, …) also lands in the main game window / the
/// consolidated Log window, per the <c>EchoToMain</c> toggle × panel
/// visibility × <see cref="IfClosedResolver"/> matrix documented on that
/// method. This is the method flagged in code review on PR #222 as having
/// zero test coverage (no Genie.App test project existed) — this file is
/// that harness's first tenant.
///
/// <para>
/// Tests drive the REAL <see cref="StreamTabsViewModel.Attach"/> Rx
/// subscription (not a reflection call into the private method itself) by
/// publishing <see cref="TextEvent"/>s via <see cref="GenieCore.PublishGameEventForTests"/>
/// — an <c>internal</c> test seam exposed to this assembly through
/// <c>InternalsVisibleTo</c> (see Genie.Core's AssemblyInfo.cs / csproj)
/// rather than reflection into the private relay field. Driving it this way
/// exercises the exact subscribe callback that ships, including the
/// Log-mirror line that sits beside <c>RouteToMain</c> in <c>Attach</c> (not
/// inside it).
/// </para>
///
/// <para>
/// <b>RxApp.MainThreadScheduler:</b> confirmed empirically (not assumed) —
/// ReactiveUI detects a unit-test host (xunit's runner assembly) and
/// defaults <c>RxApp.MainThreadScheduler</c> to an immediate/current-thread
/// scheduler with no Avalonia platform module registered, so
/// <c>.ObserveOn(RxApp.MainThreadScheduler)</c> delivers synchronously here
/// and assertions can run immediately after <c>Publish</c>. No override or
/// dispatcher pump was needed; see the task report for how this was verified.
/// </para>
/// </summary>
public class StreamTabsViewModelTests
{
    // ── Test harness ─────────────────────────────────────────────────────

    /// <summary>
    /// Wires a real <see cref="GenieCore"/> (isolated temp data dir, exactly
    /// like the Genie.Core.Tests convention) to a real
    /// <see cref="StreamTabsViewModel"/> + <see cref="GameTextViewModel"/> +
    /// <see cref="WindowSettingsStore"/> via the real <c>Attach</c> call, then
    /// exposes a way to push <see cref="GameEvent"/>s onto the core's event
    /// stream and a mutable open/closed panel set — the same three inputs
    /// <c>MainWindowViewModel</c> hands to <c>Attach</c> in production
    /// (<c>GameText</c>, <c>IsStreamPanelVisible</c>, <c>WindowSettings</c>).
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        public GenieCore Core { get; }
        public StreamTabsViewModel Tabs { get; } = new();
        public GameTextViewModel Main { get; } = new();
        public WindowSettingsStore Store { get; } = new();

        private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _dir;

        private static readonly string[] StreamIds =
        {
            "talk", "whispers", "thoughts", "combat", "logons",
            "familiar", "death", "assess", "atmospherics", "log", "itemlog",
        };

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_app_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Core = new GenieCore(dataDirectoryOverride: _dir);

            foreach (var id in StreamIds)
                Store.Register(id, id);

            // Mirror Genie.App.Docking.StreamTool's real wiring: each buffer's
            // Settings is the SAME WindowSettings instance the store hands
            // back, so EchoToMain (read off buf.Settings) and IfClosed (read
            // off the store inside IfClosedResolver) agree with each other,
            // exactly as the doc comment on RouteToMain calls out.
            Tabs.Talk.Settings         = Store.Get("talk");
            Tabs.Whispers.Settings     = Store.Get("whispers");
            Tabs.Thoughts.Settings     = Store.Get("thoughts");
            Tabs.Combat.Settings       = Store.Get("combat");
            Tabs.Logons.Settings       = Store.Get("logons");
            Tabs.Familiar.Settings     = Store.Get("familiar");
            Tabs.Death.Settings        = Store.Get("death");
            Tabs.Assess.Settings       = Store.Get("assess");
            Tabs.Atmospherics.Settings = Store.Get("atmospherics");
            Tabs.Log.Settings          = Store.Get("log");
            Tabs.ItemLog.Settings      = Store.Get("itemlog");

            Tabs.Attach(Core, Main, id => _open.Contains(id), Store);
        }

        /// <summary>Mark a stream's dock panel open (visible). Everything
        /// starts closed — tests opt panels in explicitly so each test's
        /// premise is visible in its own body.</summary>
        public void Open(string id) => _open.Add(id);

        public void Publish(GameEvent e) => Core.PublishGameEventForTests(e);

        public async ValueTask DisposeAsync()
        {
            await Core.DisposeAsync();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── EchoToMain × panel-visibility matrix ────────────────────────────
    // Uses "combat" throughout: a plain stream with no DefaultIfClosed
    // override (WindowSettingsStore), so its closed-panel fallback resolves
    // to Main — isolating these from the talk/whispers → log special case
    // covered separately below.

    [Fact]
    public async Task EchoToMain_on_panel_open_mirrors_plain_to_main_and_keeps_own_buffer()
    {
        await using var h = new Harness();
        h.Store.Get("combat").EchoToMain = true;
        h.Open("combat");

        h.Publish(new TextEvent("combat", "You hit the orc for 12 damage!"));

        Assert.Single(h.Tabs.Combat.Lines);
        Assert.Equal("You hit the orc for 12 damage!", h.Tabs.Combat.Lines[0].Text);

        Assert.Single(h.Main.Lines);
        Assert.Equal("You hit the orc for 12 damage!", h.Main.Lines[0].Text); // no [combat] prefix
    }

    [Fact]
    public async Task EchoToMain_on_panel_closed_still_mirrors_plain_to_main()
    {
        await using var h = new Harness();
        h.Store.Get("combat").EchoToMain = true;
        // panel left closed — EchoToMain doesn't consult visibility at all.

        h.Publish(new TextEvent("combat", "You hit the orc for 12 damage!"));

        Assert.Single(h.Tabs.Combat.Lines);
        Assert.Single(h.Main.Lines);
        Assert.Equal("You hit the orc for 12 damage!", h.Main.Lines[0].Text);
    }

    [Fact]
    public async Task EchoToMain_off_panel_open_adds_nothing_to_main()
    {
        await using var h = new Harness();
        h.Store.Get("combat").EchoToMain = false;
        h.Open("combat");

        h.Publish(new TextEvent("combat", "You hit the orc for 12 damage!"));

        Assert.Single(h.Tabs.Combat.Lines); // own open panel already shows it
        Assert.Empty(h.Main.Lines);
    }

    [Fact]
    public async Task EchoToMain_off_panel_closed_falls_back_to_main_with_stream_prefix()
    {
        await using var h = new Harness();
        h.Store.Get("combat").EchoToMain = false;
        // panel left closed; "combat" has no DefaultIfClosed override, so
        // IfClosed stays null → IfClosedResolver resolves to Main.

        h.Publish(new TextEvent("combat", "You hit the orc for 12 damage!"));

        Assert.Single(h.Tabs.Combat.Lines);
        Assert.Single(h.Main.Lines);
        // AddStreamLine (the closed-panel fallback), unlike EchoStreamToMain,
        // prefixes the source stream — the visible difference between the
        // two routes.
        Assert.Equal("[combat] You hit the orc for 12 damage!", h.Main.Lines[0].Text);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Main_never_receives_a_line_twice_for_one_event(bool echoToMain, bool panelOpen)
    {
        // Regression guard for the bug referenced in RouteToMain's own doc
        // comment: every combat line appearing twice in Main (once plain via
        // EchoToMain, once "[combat] …" via the closed-panel fallback) the
        // moment the Combat panel was closed. The two routes must be
        // mutually exclusive across the whole matrix.
        await using var h = new Harness();
        h.Store.Get("combat").EchoToMain = echoToMain;
        if (panelOpen) h.Open("combat");

        h.Publish(new TextEvent("combat", "single event"));

        Assert.True(h.Main.Lines.Count <= 1,
            $"Main got {h.Main.Lines.Count} line(s) for one event " +
            $"(EchoToMain={echoToMain}, panelOpen={panelOpen}) — the two routes fired together.");
    }

    // ── IfClosed redirect (public #211) via RouteToMain's closed-panel path ─

    [Fact]
    public async Task Closed_panel_with_custom_IfClosed_target_delivers_into_that_streams_own_buffer()
    {
        await using var h = new Harness();
        h.Store.Get("combat").EchoToMain = false;
        h.Store.Get("combat").IfClosed = "familiar";
        h.Open("familiar"); // combat stays closed

        h.Publish(new TextEvent("combat", "A shadow moves nearby."));

        Assert.Single(h.Tabs.Combat.Lines);   // still lands in its own (closed) buffer
        Assert.Single(h.Tabs.Familiar.Lines); // and is redirected into the open target
        Assert.Equal("A shadow moves nearby.", h.Tabs.Familiar.Lines[0].Text); // direct buffer add — no [combat] prefix
        Assert.Empty(h.Main.Lines);
    }

    [Fact]
    public async Task Closed_panel_with_IfClosed_disabled_drops_the_main_fallback_but_keeps_own_buffer()
    {
        await using var h = new Harness();
        h.Store.Get("combat").EchoToMain = false;
        h.Store.Get("combat").IfClosed = ""; // sentinel: explicitly disabled

        h.Publish(new TextEvent("combat", "A shadow moves nearby."));

        Assert.Single(h.Tabs.Combat.Lines); // never vanishes from its own buffer
        Assert.Empty(h.Main.Lines);
    }

    // ── Log double-add (PR #222) ────────────────────────────────────────
    // talk/whispers lines are unconditionally mirrored into Log by the
    // subscribe callback in Attach (StreamTabsViewModel.cs ~118-119),
    // regardless of what RouteToMain decides. Separately, talk/whispers'
    // shipped DefaultIfClosed target (WindowSettingsStore) is also "log". So
    // with EchoToMain off and the Talk panel closed, RouteToMain's own
    // IfClosedResolver fallback ALSO resolves to "log" — without a guard,
    // that's a second Log.Add for the same line.

    [Fact]
    public async Task Talk_mirrors_into_log_exactly_once_regardless_of_echo_route()
    {
        // Control case: EchoToMain on, panel open — the ordinary route.
        // Log's mirror is unconditional and lives outside RouteToMain
        // entirely, so this passes independent of the closed-panel guard
        // below; it locks down that the mirror itself never double-fires.
        await using var h = new Harness();
        h.Store.Get("talk").EchoToMain = true;
        h.Open("talk");

        h.Publish(new TextEvent("talk", "Bob says, \"Hello there.\""));

        Assert.Single(h.Tabs.Log.Lines);
        Assert.Single(h.Main.Lines); // EchoToMain route, plain (no prefix)
    }

    // PR #222's Log-guard (`decision.StreamId == "log" && e.Stream is "talk" or
    // "whispers"` → skip the redundant add) is merged into main as of this
    // branch's rebase, so these run unskipped.
    [Fact]
    public async Task Talk_closed_with_echoToMain_off_lands_in_log_exactly_once_not_twice()
    {
        await using var h = new Harness();
        h.Store.Get("talk").EchoToMain = false;
        h.Open("log"); // talk's default IfClosed target — must be open to resolve straight to it
        // talk itself stays closed.

        h.Publish(new TextEvent("talk", "Bob says, \"Hello there.\""));

        Assert.Single(h.Tabs.Talk.Lines);            // own (closed) panel still gets it
        Assert.Single(h.Tabs.Log.Lines);              // Log gets it exactly once — the bug PR #222 fixed
        Assert.Empty(h.Main.Lines);                   // guarded fallback: no second route fires
    }

    // Same PR #222 guard as the talk case above.
    [Fact]
    public async Task Whispers_closed_with_echoToMain_off_lands_in_log_exactly_once_not_twice()
    {
        await using var h = new Harness();
        h.Store.Get("whispers").EchoToMain = false;
        h.Open("log");

        h.Publish(new TextEvent("whispers", "Bob whispers, \"Watch out.\""));

        Assert.Single(h.Tabs.Whispers.Lines);
        Assert.Single(h.Tabs.Log.Lines);
        Assert.Empty(h.Main.Lines);
    }
}
