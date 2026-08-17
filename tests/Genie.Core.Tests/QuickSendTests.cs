using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 quick-send parity (public #278). G4 hardcodes <c>cQuickSendChar =
/// '-'</c> (Lists/Config.cs:15) and rewrites any ';'-chain segment starting
/// with it to <c>#send</c> + remainder (Core/Command.cs:254); #send's scanner
/// then peels a leading digit run — no space needed — as a POSITIVE
/// wait-before-send on the roundtime-gated CommandQueue. The reported repro:
/// <c>put health;-0.05 encumbrance</c> must send <c>health</c> now and
/// <c>encumbrance</c> 0.05s later, instead of sending the literal line
/// "-0.05 encumbrance" (DR: "Please rephrase that command.").
/// </summary>
public class QuickSendTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public QuickSendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_quicksend_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieQuickSendTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ── QuickSend.TryParse — the shared G4 Send() scanner ───────────────────

    [Theory]
    [InlineData("-0.05 encumbrance", "0.05", "encumbrance")]
    [InlineData("-1 go door", "1", "go door")]
    [InlineData("-3knock concealed door", "3", "knock concealed door")] // glued (Riverhaven ritual)
    [InlineData("- 0.5 cast", "0.5", "cast")]                           // space after dash tolerated
    [InlineData("-cast", "", "cast")]                                   // bare verb → 0-delay RT-gated send
    [InlineData("-whisp door secret", "", "whisp door secret")]
    public void TryParse_accepts_quick_send_forms(string seg, string expectedDelay, string expectedCmd)
    {
        Assert.True(QuickSend.TryParse(seg, out var delay, out var cmd));
        Assert.Equal(expectedDelay, delay);
        Assert.Equal(expectedCmd, cmd);
    }

    [Theory]
    [InlineData("-")]              // lone dash
    [InlineData("-3")]             // delay with no command — stays literal (G4 drops silently)
    [InlineData("health")]         // no dash
    [InlineData("swap-weapon")]    // hyphen mid-token
    [InlineData("")]
    public void TryParse_rejects_non_quick_send_segments(string seg)
    {
        Assert.False(QuickSend.TryParse(seg, out _, out _));
    }

    [Theory]
    [InlineData("-0.05 encumbrance", "#send 0.05 encumbrance")]
    [InlineData("-3knock concealed door", "#send 3 knock concealed door")]
    [InlineData("-pray", "#send pray")]
    public void TryRewrite_produces_the_hash_send_form(string seg, string expected)
    {
        Assert.True(QuickSend.TryRewrite(seg, '#', out var rewritten));
        Assert.Equal(expected, rewritten);
    }

    // ── CommandEngine.ProcessInput — the G4 ParseCommand choke point ────────

    private sealed class Harness
    {
        public readonly RecordingHost Host = new();
        public readonly CommandQueue Queue = new();
        public readonly CommandEngine Engine;

        public Harness(GenieConfig config)
            => Engine = new CommandEngine(config, Queue, new EventQueue(), Host);
    }

    [Fact]
    public void Issue278_repro_chain_sends_head_now_and_queues_dash_segment()
    {
        var h = new Harness(_config);
        h.Engine.ProcessInput("#put health;-0.05 encumbrance");

        Assert.Equal(new[] { "health" }, h.Host.Sent);
        var item = Assert.Single(h.Queue.EventList);
        Assert.Equal("encumbrance", item.Action);
        Assert.Equal(0.05, item.Delay);
        Assert.True(item.Restrictions.WaitForRoundtime);
    }

    [Fact]
    public void Plain_segments_after_a_dash_segment_still_send_immediately()
    {
        // G4 ordering: plain rows go out back-to-back; only the quick-send
        // row lags on the queue.
        var h = new Harness(_config);
        h.Engine.ProcessInput("#put assess;-0.5 gesture;exp");

        Assert.Equal(new[] { "assess", "exp" }, h.Host.Sent);
        var item = Assert.Single(h.Queue.EventList);
        Assert.Equal("gesture", item.Action);
        Assert.Equal(0.5, item.Delay);
    }

    [Fact]
    public void Glued_delay_row_is_queued_with_the_delay_split_off()
    {
        var h = new Harness(_config);
        h.Engine.ProcessInput("-3knock concealed door");

        Assert.Empty(h.Host.Sent);
        var item = Assert.Single(h.Queue.EventList);
        Assert.Equal("knock concealed door", item.Action);
        Assert.Equal(3.0, item.Delay);
    }

    [Fact]
    public void Bare_dash_verb_is_a_zero_delay_roundtime_gated_send()
    {
        var h = new Harness(_config);
        h.Engine.ProcessInput("-pray");
        Assert.Empty(h.Host.Sent);

        h.Engine.Tick(inRoundtime: true);         // RT active — held back
        Assert.Empty(h.Host.Sent);

        h.Engine.Tick(inRoundtime: false);        // RT cleared — fires
        Assert.Equal(new[] { "pray" }, h.Host.Sent);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("-3")]
    public void Degenerate_dash_rows_go_out_literally(string row)
    {
        // Deliberate, visible deviation from G4 (which drops them silently).
        var h = new Harness(_config);
        h.Engine.ProcessInput(row);

        Assert.Equal(new[] { row }, h.Host.Sent);
        Assert.Empty(h.Queue.EventList);
    }

    [Fact]
    public void Hash_send_with_dash_body_is_a_positive_pause_not_eager()
    {
        // G4 net behavior for `#send -0.5 unload …`: its queue re-parses the
        // dash row, so the delay comes out positive. The old G5 reading
        // (negative ⇒ fire eagerly) had no G4 basis.
        var h = new Harness(_config);
        h.Engine.ProcessInput("#send -0.5 unload my bow");

        var item = Assert.Single(h.Queue.EventList);
        Assert.Equal("unload my bow", item.Action);
        Assert.Equal(0.5, item.Delay);
        Assert.True(item.Restrictions.WaitForRoundtime);
    }

    [Fact]
    public void Dashless_glued_number_stays_literal_on_hash_send()
    {
        // Deliberate G5 deviation guarded here: `#send 5fire` does NOT parse
        // a delay (G4's scanner would read 5 + "fire").
        var h = new Harness(_config);
        h.Engine.ProcessInput("#send 5fire");

        var item = Assert.Single(h.Queue.EventList);
        Assert.Equal("5fire", item.Action);
        Assert.Equal(0.0, item.Delay);
    }

    // ── ScriptEngine put/send chains → host CommandQueue (end-to-end) ───────

    private sealed class ScriptHarness : IDisposable
    {
        public readonly List<string> Sent = new();
        public readonly CommandQueue Queue = new();
        public readonly ScriptEngine Engine;
        private readonly string _dir;

        public ScriptHarness(GenieConfig config, string script)
        {
            _dir = Path.Combine(Path.GetTempPath(), "gc_quicksend_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "t.cmd"), script);

            // Wire the script engine's # forwarding into a real CommandEngine
            // whose host records SendToGame — the same shape the app uses —
            // so a rewritten `#send 0.05 encumbrance` lands on the RT-gated
            // CommandQueue exactly as a typed one would.
            var host = new RecordingHost(Sent);
            var commandEngine = new CommandEngine(config, Queue, new EventQueue(), host);
            Engine = new ScriptEngine(_dir, new TypeAheadSession(),
                                      sendCommand: Sent.Add,
                                      echo: _ => { },
                                      handleHashCmd: s => commandEngine.ProcessInput(s));
            Assert.True(Engine.TryStart("t", new List<string>()));
        }

        public void Pump(int ticks = 10) { for (int i = 0; i < ticks; i++) Engine.Tick(); }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public void Script_put_chain_queues_the_dash_segment_with_its_delay()
    {
        // The exact #278 report, run through the script engine.
        using var h = new ScriptHarness(_config, "put health;-0.05 encumbrance\n");
        h.Pump();

        Assert.Equal(new[] { "health" }, h.Sent);
        var item = Assert.Single(h.Queue.EventList);
        Assert.Equal("encumbrance", item.Action);
        Assert.Equal(0.05, item.Delay);
        Assert.True(item.Restrictions.WaitForRoundtime);
    }

    [Fact]
    public void Script_send_dash_number_is_a_pause_not_an_eager_marker()
    {
        using var h = new ScriptHarness(_config, "send fire;-0.5 unload my bow\n");
        h.Pump();

        Assert.Equal(new[] { "fire" }, h.Sent);
        var item = Assert.Single(h.Queue.EventList);
        Assert.Equal("unload my bow", item.Action);
        Assert.Equal(0.5, item.Delay);
    }

    [Fact]
    public void Script_bare_dash_verb_fires_via_the_roundtime_gated_queue()
    {
        // The uber.cmd idiom `put -cast …`: under real G4 rules the dash makes
        // it a 0-delay RT-gated #send, so the bare verb still reaches the game.
        using var h = new ScriptHarness(_config, "put -cast\n");
        h.Pump();

        Assert.Empty(h.Sent);
        var item = Assert.Single(h.Queue.EventList);
        Assert.Equal("cast", item.Action);
        Assert.Equal(0.0, item.Delay);
    }

    [Fact]
    public void Script_put_with_leading_number_stays_verbatim()
    {
        // `put 20 kronars in box` must never be parsed as a delay.
        using var h = new ScriptHarness(_config, "put 20 kronars in box\n");
        h.Pump();

        Assert.Equal(new[] { "20 kronars in box" }, h.Sent);
        Assert.Empty(h.Queue.EventList);
    }

    /// <summary>Records <see cref="SendToGame"/>; everything else no-op.</summary>
    private sealed class RecordingHost : ICommandHost
    {
        public List<string> Sent { get; }
        public RecordingHost() : this(new List<string>()) { }
        public RecordingHost(List<string> sink) => Sent = sink;

        public Dictionary<string, string> Globals { get; } = new();

        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;

        public void Echo(string text) { }
        public void EchoTo(string text, string? window, string? color) { }
        public void EchoMain(string text, string? color, bool mono) { }
        public void EchoLink(string text, string command, string? window) { }
        public void EchoClear(string? window) { }
        public void WindowCommand(string sub, string window) { }
        public void SetStatusBar(string text, int index) { }
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null) => Sent.Add(text);
        public void RunScript(string text) { }
        public void InjectParsedLine(string line) { }
        public void StopScript(string? name) { }
        public void PauseScript(string? name) { }
        public void ResumeScript(string? name) { }
        public void StopAllScripts() { }
        public void PauseAllScripts() { }
        public void ResumeAllScripts() { }
        public void SetTraceLevelAll(int level) { }
        public IReadOnlyList<string> RunningScripts() => Array.Empty<string>();
        public void SetGlobalVariable(string name, string value) => Globals[name] = value;
        public void RemoveGlobalVariable(string name) => Globals.Remove(name);
        public string SetLiveAudit(Genie.Core.Diagnostics.AuditMode mode) => string.Empty;
        public void EditScript(string name) { }
        public void LayoutCommand(string args) { }
        public void PluginCommand(string args) { }
        public void ConfigCommand(string args) { }
        public void MapperGoto(string args) { }
        public void MapperCommand(string args) { }
        public void MapperReset() { }
        public void PlaySound(string soundName) { }
        public void Speak(string text, bool urgent = false) { }
        public void TtsCommand(string args) { }
        public void FlashWindow() { }
        public void Connect(ConnectRequest request) { }
    }
}
