using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Genie.Core.Events;
using Genie.Core.WindowLogging;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #270 — per-window logging (Genie 4 Window Logger parity): filename
/// template expansion, confinement to the Logs folder, a file shared by two
/// streams, per-line date rollover, timestamps, persistence, and the tap in the
/// game-text pipeline.
/// </summary>
public class WindowLogTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "genie_winlog_" + Guid.NewGuid().ToString("N"));
    private string LogDir => Path.Combine(_root, "Logs");

    public WindowLogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private static readonly DateTime Sept27 = new(2026, 9, 27, 14, 3, 0);

    private static WindowLogContext Ctx(string ch = "Renucci", string game = "DR",
                                        string stream = "talk", DateTime? t = null)
        => new(ch, game, stream, t ?? Sept27);

    // ── Template expansion ─────────────────────────────────────────────────

    [Fact]
    public void Expands_the_Genie4_default_template()
    {
        var s = WindowLogPath.Expand(
            @"\GenieWindows\Thoughts\{charactername}\Thoughts-{charactername}-{yyyy}.txt", Ctx());
        Assert.Equal(@"\GenieWindows\Thoughts\Renucci\Thoughts-Renucci-2026.txt", s);
    }

    [Theory]
    [InlineData("{character}", "Renucci")]
    [InlineData("{CharacterName}", "Renucci")]
    [InlineData("{gamename}", "DR")]
    [InlineData("{game}", "DR")]
    [InlineData("{stream}", "talk")]
    [InlineData("{yyyy}-{MM}-{dd}", "2026-09-27")]
    [InlineData("{yy}", "26")]
    public void Expands_each_token(string template, string expected)
        => Assert.Equal(expected, WindowLogPath.Expand(template, Ctx()));

    [Fact]
    public void Date_tokens_are_case_exact_and_unknown_tokens_stay_literal()
    {
        // {mm} would be MINUTES in a .NET format; it is not a token, so it must
        // not silently become the month.
        Assert.Equal("{mm}-{zone}", WindowLogPath.Expand("{mm}-{zone}", Ctx()));
        Assert.Equal(new[] { "mm", "zone" }, WindowLogPath.UnknownTokens("{mm}-{zone}-{yyyy}"));
    }

    [Fact]
    public void Token_values_cannot_introduce_folders()
    {
        var s = WindowLogPath.Expand("{charactername}.txt", Ctx(ch: @"..\..\evil"));
        Assert.DoesNotContain(@"\", s);
        Assert.DoesNotContain("/", s);
        Assert.NotNull(WindowLogPath.Resolve(LogDir, s, out _));
        Assert.Equal("__.txt", WindowLogPath.Expand("{charactername}.txt", Ctx(ch: "..")));
    }

    [Fact]
    public void An_empty_character_falls_back_rather_than_vanishing()
        => Assert.Equal("unknown-x", WindowLogPath.Expand("{charactername}-x", Ctx(ch: "")));

    // ── Path safety ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData("../outside.txt")]
    [InlineData(@"a\..\..\outside.txt")]
    [InlineData(@"a\.\b.txt")]
    [InlineData(@"C:\Windows\x.txt")]
    [InlineData("C:x.txt")]
    [InlineData("log.txt:stream")]
    [InlineData("")]
    [InlineData(@"\")]
    public void Refuses_paths_that_leave_the_logs_folder(string name)
    {
        Assert.Null(WindowLogPath.Resolve(LogDir, name, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData(@"\GenieWindows\Thoughts\x.txt", @"GenieWindows\Thoughts\x.txt")]
    [InlineData("/GenieWindows/Thoughts/x.txt", @"GenieWindows\Thoughts\x.txt")]
    [InlineData("plain.txt", "plain.txt")]
    [InlineData(@"\\server\share\x.txt", @"server\share\x.txt")]   // a UNC form is read as folders
    [InlineData(@"a\b\c.txt", @"a\b\c.txt")]
    public void Relative_and_leading_slash_names_land_under_logs(string name, string relative)
    {
        var full = WindowLogPath.Resolve(LogDir, name, out var error);
        Assert.Null(error);
        var expected = Path.Combine(new[] { Path.GetFullPath(LogDir) }
            .Concat(relative.Split('\\')).ToArray());
        Assert.Equal(expected, full);
    }

    // ── The sink ───────────────────────────────────────────────────────────

    private (WindowLogSink Sink, WindowLogRules Rules, List<string> Warnings) NewSink(Func<DateTime>? clock = null)
    {
        var rules = new WindowLogRules();
        var sink  = new WindowLogSink(rules, () => LogDir, clock ?? (() => Sept27), startTimer: false);
        var warnings = new List<string>();
        sink.Warning += warnings.Add;
        return (sink, rules, warnings);
    }

    private static string[] ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r  = new StreamReader(fs);
        return r.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Talk_and_whispers_share_one_file_in_arrival_order()
    {
        var (sink, rules, _) = NewSink();
        using var _s = sink;
        const string shared = @"Conversations\{charactername}-{yyyy}.txt";
        rules.Set(new WindowLogRule { Stream = "talk",     File = shared, TimestampFormat = "" });
        rules.Set(new WindowLogRule { Stream = "whispers", File = shared, TimestampFormat = "" });

        sink.Observe("talk",     "You say, \"one.\"",               "Renucci", "DR");
        sink.Observe("whispers", "Naper whispers, \"two.\"",        "Renucci", "DR");
        sink.Observe("combat",   "not logged",                       "Renucci", "DR");
        sink.Observe("talk",     "Naper says, \"three.\"",           "Renucci", "DR");
        sink.Observe("whispers", "You whisper to Naper, \"four.\"",  "Renucci", "DR");
        sink.Flush();

        Assert.Single(sink.OpenFiles);
        var lines = ReadLines(Path.Combine(LogDir, "Conversations", "Renucci-2026.txt"));
        Assert.Equal(new[]
        {
            "You say, \"one.\"", "Naper whispers, \"two.\"",
            "Naper says, \"three.\"", "You whisper to Naper, \"four.\"",
        }, lines);
    }

    [Fact]
    public void A_yyyy_file_rolls_over_at_the_new_year()
    {
        var now = new DateTime(2026, 12, 31, 23, 59, 0);
        var (sink, rules, _) = NewSink(() => now);
        using var _s = sink;
        rules.Set(new WindowLogRule { Stream = "thoughts", File = "Thoughts-{yyyy}.txt", TimestampFormat = "" });

        sink.Observe("thoughts", "last of the year", "Renucci", "DR");
        sink.Flush();
        now = new DateTime(2027, 1, 1, 0, 1, 0);
        sink.Observe("thoughts", "first of the next", "Renucci", "DR");
        sink.Flush();

        Assert.Equal(new[] { "last of the year" },  ReadLines(Path.Combine(LogDir, "Thoughts-2026.txt")));
        Assert.Equal(new[] { "first of the next" }, ReadLines(Path.Combine(LogDir, "Thoughts-2027.txt")));
    }

    [Fact]
    public void An_idle_file_is_closed_so_last_years_log_is_released()
    {
        var now = new DateTime(2026, 12, 31, 23, 59, 0);
        var (sink, rules, _) = NewSink(() => now);
        using var _s = sink;
        rules.Set(new WindowLogRule { Stream = "thoughts", File = "Thoughts-{yyyy}.txt" });

        sink.Observe("thoughts", "x", "Renucci", "DR");
        sink.Flush();
        now = now + WindowLogSink.IdleClose + TimeSpan.FromMinutes(1);
        sink.Observe("thoughts", "y", "Renucci", "DR");
        sink.Flush();   // writes 2027's line; 2026's writer has gone idle
        sink.Flush();

        Assert.Equal(new[] { Path.GetFullPath(Path.Combine(LogDir, "Thoughts-2027.txt")) }, sink.OpenFiles);
    }

    [Fact]
    public void Lines_carry_the_rule_timestamp_format()
    {
        var (sink, rules, _) = NewSink();
        using var _s = sink;
        rules.Set(new WindowLogRule { Stream = "logons", File = "a.txt" });   // default format
        rules.Set(new WindowLogRule { Stream = "death",  File = "b.txt", TimestampFormat = "HH:mm:ss" });

        sink.Observe("logons", "Naper joins the adventure.", "Renucci", "DR");
        sink.Observe("death",  "Naper was just struck down!", "Renucci", "DR");
        sink.Flush();

        Assert.Equal(new[] { "[2026-09-27 14:03] Naper joins the adventure." },
                     ReadLines(Path.Combine(LogDir, "a.txt")));
        Assert.Equal(new[] { "[14:03:00] Naper was just struck down!" },
                     ReadLines(Path.Combine(LogDir, "b.txt")));
    }

    [Fact]
    public void Disabled_and_unruled_streams_write_nothing_but_are_still_enumerated()
    {
        var (sink, rules, _) = NewSink();
        using var _s = sink;
        rules.Set(new WindowLogRule { Stream = "talk", File = "t.txt", Enabled = false });

        sink.Observe("talk", "quiet", "Renucci", "DR");
        sink.Observe("percWindow", "Minor Sanctuary (3 roisaen)", "Renucci", "DR");
        sink.Flush();

        Assert.False(File.Exists(Path.Combine(LogDir, "t.txt")));
        Assert.Empty(sink.OpenFiles);
        Assert.Contains("talk", sink.SeenStreams);
        Assert.Contains("percWindow", sink.SeenStreams);
    }

    [Fact]
    public void Stream_ids_match_without_regard_to_case()
    {
        var (sink, rules, _) = NewSink();
        using var _s = sink;
        rules.Set(new WindowLogRule { Stream = "percwindow", File = "p.txt", TimestampFormat = "" });

        sink.Observe("percWindow", "Minor Sanctuary", "Renucci", "DR");
        sink.Flush();

        Assert.Equal(new[] { "Minor Sanctuary" }, ReadLines(Path.Combine(LogDir, "p.txt")));
    }

    [Fact]
    public void An_unopenable_file_warns_once_and_never_throws()
    {
        var (sink, rules, warnings) = NewSink();
        using var _s = sink;
        rules.Set(new WindowLogRule { Stream = "talk", File = "held.txt" });
        Directory.CreateDirectory(LogDir);
        using var holder = new FileStream(Path.Combine(LogDir, "held.txt"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var ex = Record.Exception(() =>
        {
            sink.Observe("talk", "one", "Renucci", "DR");
            sink.Flush();
            sink.Observe("talk", "two", "Renucci", "DR");
            sink.Flush();
        });

        Assert.Null(ex);
        Assert.Single(warnings, w => w.Contains("could not open"));
    }

    [Fact]
    public void A_rule_whose_template_escapes_the_logs_folder_is_refused_at_write_time()
    {
        // The command refuses such a template up front; a hand-edited
        // windowlog.json can still carry one, so the sink checks again.
        var (sink, rules, warnings) = NewSink();
        using var _s = sink;
        rules.Set(new WindowLogRule { Stream = "talk", File = @"..\..\escaped-{yyyy}.txt" });

        sink.Observe("talk", "x", "Renucci", "DR");
        sink.Flush();

        Assert.False(File.Exists(Path.Combine(_root, "escaped-2026.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped-2026.txt")));
        Assert.Single(warnings, w => w.Contains("not usable"));
    }

    [Fact]
    public void CloseAll_writes_the_queued_tail_and_releases_the_file()
    {
        var (sink, rules, _) = NewSink();
        using var _s = sink;
        rules.Set(new WindowLogRule { Stream = "talk", File = "t.txt", TimestampFormat = "" });

        sink.Observe("talk", "tail", "Renucci", "DR");
        sink.CloseAll();

        Assert.Empty(sink.OpenFiles);
        Assert.Equal(new[] { "tail" }, File.ReadAllLines(Path.Combine(LogDir, "t.txt")));
    }

    // ── Rules persistence ──────────────────────────────────────────────────

    [Fact]
    public void Rules_round_trip_and_a_torn_file_keeps_the_current_rules()
    {
        var path = Path.Combine(_root, WindowLogRules.FileName);
        var rules = new WindowLogRules();
        foreach (var d in WindowLogRules.Genie4Defaults) rules.Set(d);
        Assert.True(rules.Save(path));

        var reloaded = new WindowLogRules();
        Assert.True(reloaded.Load(path));
        Assert.Equal(5, reloaded.All().Count);
        Assert.Equal(reloaded.TryGet("talk")!.File, reloaded.TryGet("whispers")!.File);
        Assert.Equal(WindowLogPath.DefaultTimestampFormat, reloaded.TryGet("thoughts")!.TimestampFormat);

        File.WriteAllText(path, "[ { \"Stream\": \"talk\", ");   // torn write
        Assert.False(reloaded.Load(path));
        Assert.Equal(5, reloaded.All().Count);

        File.Delete(path);                                        // a profile with no file
        reloaded.Load(path);
        Assert.Empty(reloaded.All());
    }

    [Fact]
    public void A_character_without_its_own_rules_loads_the_shared_ones()
    {
        var profile = Path.Combine(_root, "Profiles", "Renucci-MONIL");
        var shared  = Path.Combine(_root, "Config");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(shared);

        Assert.Equal(Path.Combine(shared, WindowLogRules.FileName),
                     WindowLogRules.PathToLoad(profile, shared));

        File.WriteAllText(Path.Combine(profile, WindowLogRules.FileName), "[]");
        Assert.Equal(Path.Combine(profile, WindowLogRules.FileName),
                     WindowLogRules.PathToLoad(profile, shared));
    }

    // ── The pipeline tap ───────────────────────────────────────────────────

    [Fact]
    public async Task The_core_logs_stream_text_as_the_windows_show_it()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        await using var core = new GenieCore(dataDirectoryOverride: data, gameThreadOverride: false);
        core.Scripts.Globals["charactername"] = "Renucci";
        core.Scripts.Globals["game"] = "DR";
        core.WindowLogs.Rules.Set(new WindowLogRule { Stream = "talk", File = "talk.txt", TimestampFormat = "" });
        core.WindowLogs.Rules.Set(new WindowLogRule { Stream = "main", File = "main.txt", TimestampFormat = "" });
        core.Substitutes.AddRule("goblin", "GOBLIN");
        core.Gags.AddRule("spam line");

        core.ProcessGameTextEvent(new TextEvent("talk", "You say, \"goblin.\""));
        core.ProcessGameTextEvent(new TextEvent("main", "You say, \"goblin.\"", DuplicateEcho: true));
        core.ProcessGameTextEvent(new TextEvent("main", "A goblin arrives."));
        core.ProcessGameTextEvent(new TextEvent("main", "a spam line"));
        core.WindowLogs.Flush();

        var logDir = core.Config.LogDir;
        // The talk window shows its text unsubstituted; main shows substitutes,
        // hides gagged lines, and never shows DR's bare duplicate of a talk line.
        Assert.Equal(new[] { "You say, \"goblin.\"" }, ReadLines(Path.Combine(logDir, "talk.txt")));
        Assert.Equal(new[] { "A GOBLIN arrives." },    ReadLines(Path.Combine(logDir, "main.txt")));
        Assert.Contains("talk", core.WindowLogs.SeenStreams);
    }
}
