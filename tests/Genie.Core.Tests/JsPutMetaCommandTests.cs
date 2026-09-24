using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// 2026-08-30 Lich gap analysis — <c>genie.put()</c> sent <c>#</c> meta-commands to
/// the game server literally, where a <c>.cmd</c> <c>put</c> runs them client-side.
/// A <c>.js</c> script could not set a <c>#var</c>, start a script, or walk a
/// <c>#goto</c>, and the raw <c>#…</c> text leaked into the game as a failed
/// command. Both JS sinks — standalone <c>.js</c> array scripts and inline
/// <c>&lt;% %&gt;</c> blocks — now route like <c>.cmd</c>'s <c>put</c>.
/// </summary>
public class JsPutMetaCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _sent = new();
    private readonly List<string> _echoed = new();
    private readonly List<string> _hash = new();

    public JsPutMetaCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gc_jsput_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private ScriptEngine NewEngine(bool withHost) =>
        new(_dir, new TypeAheadSession(),
            sendCommand: c => { lock (_sent) _sent.Add(c); },
            echo: l => { lock (_echoed) _echoed.Add(l); },
            handleHashCmd: withHost ? c => { lock (_hash) _hash.Add(c); } : null);

    /// <summary>Standalone .js runs on its own thread; tick until the sentinel
    /// `look` reaches the game (every earlier put has been dispatched by then).</summary>
    private void RunJs(ScriptEngine engine, string body)
    {
        File.WriteAllText(Path.Combine(_dir, "t.js"), body + "genie.put('look');\n");
        Assert.True(engine.TryStart("t", new List<string>()));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (_sent) if (_sent.Contains("look")) break;
            engine.Tick();
            System.Threading.Thread.Sleep(10);
        }
        lock (_sent) Assert.Contains("look", _sent);
    }

    [Fact]
    public void Js_hash_command_runs_client_side_and_never_reaches_the_game()
    {
        var engine = NewEngine(withHost: false);   // no host: #tvar sets the global itself
        RunJs(engine, "genie.put('#tvar jsprobe 1');\ngenie.put('#echo from js');\n");

        Assert.Equal("1", engine.Globals["jsprobe"]);
        lock (_echoed) Assert.Contains("from js", _echoed);
        lock (_sent) Assert.DoesNotContain(_sent, s => s.StartsWith("#"));
    }

    [Fact]
    public void Js_hash_command_is_forwarded_to_the_host_command_engine()
    {
        var engine = NewEngine(withHost: true);
        RunJs(engine, "genie.put('#goto 42');\n");

        lock (_hash) Assert.Contains("#goto 42", _hash);
        lock (_sent) Assert.DoesNotContain("#goto 42", _sent);
    }

    [Fact]
    public void Js_dot_command_starts_a_script_instead_of_reaching_the_game()
    {
        var engine = NewEngine(withHost: true);
        RunJs(engine, "genie.put('.hunt north');\n");

        lock (_hash) Assert.Contains(".hunt north", _hash);
        lock (_sent) Assert.DoesNotContain(".hunt north", _sent);
    }

    [Fact]
    public void Js_plain_verb_still_reaches_the_game()
    {
        var engine = NewEngine(withHost: true);
        RunJs(engine, "genie.put('stand');\n");

        lock (_sent) Assert.Equal(new[] { "stand", "look" }, _sent);
        lock (_hash) Assert.Empty(_hash);
    }

    [Fact]
    public void Inline_block_put_of_a_hash_command_runs_client_side()
    {
        var engine = NewEngine(withHost: false);
        File.WriteAllText(Path.Combine(_dir, "t.cmd"),
            "<%\n   put('#tvar blockprobe 2');\n   put('stand');\n%>\n");
        engine.TryStart("t", new List<string>());
        for (int i = 0; i < 200; i++) engine.Tick();

        Assert.Equal("2", engine.Globals["blockprobe"]);
        Assert.Equal(new[] { "stand" }, _sent);
    }
}
