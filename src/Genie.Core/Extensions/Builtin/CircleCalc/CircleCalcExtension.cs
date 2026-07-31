using System.Text.RegularExpressions;

namespace Genie.Core.Extensions.Builtin.CircleCalc;

/// <summary>
/// Built-in <b>Circle Calculator</b> — the V5 port of VTCifer's Genie 4 plugin
/// (v4.0.6b), now a sibling of the Experience tracker rather than an external DLL.
/// Two console commands, both driven off DR's <c>exp</c> output:
///
/// <list type="bullet">
/// <item><c>/calc [guild] [circle]</c> — work out, per skill, how many ranks you
///   still need to reach your next circle (or a circle you name) in a guild,
///   using the requirement tables in <c>CircleReqs.xml</c>;</item>
/// <item><c>/sort [skillset|group] [rank]</c> — list your skills highest-rank
///   first, optionally restricted to a skillset or a custom group from
///   <c>SortGroups.xml</c>.</item>
/// </list>
///
/// <para><b>How it runs.</b> A command resolves its arguments and sends the
/// <c>exp</c> (or <c>info</c>) verb to the game; the extension then reads the
/// experience dump back through <see cref="OnGameLine"/>, accumulates skill ranks,
/// and on the end of the dump runs the pure <see cref="CircleEngine"/> math and
/// echoes the result to the main window via <see cref="IExtensionHost.Echo"/> — as
/// the Genie 4 plugin did. Unlike the live Experience panel this is a one-shot
/// report, so it has no dock window and isn't gated by a settings toggle.</para>
///
/// <para><b>Skillset filtering is delegated to DR</b> (e.g. <c>exp armor all</c>),
/// exactly as Genie 4 did, so no client-side skill→skillset map is needed; the
/// Experience tracker's live table is intentionally NOT shared (guild auto-detect
/// already requires our own <c>info</c> parse, and re-parsing the filtered dump is
/// the faithful behaviour).</para>
///
/// <para><b>Config</b> (optional, read from a persistent <c>#var</c> first, then a
/// session global): <c>$CircleCalc.Guild</c> default guild, <c>$CircleCalc.Sort</c>
/// (1 = highest-circle reqs first), <c>$CircleCalc.Display</c> (0 = up to circle 200,
/// 1 = all, 2 = next binding circle only) — matching the Genie 4 plugin's settings.
/// Result output honours two independent toggles, both default on:
/// <c>$CircleCalc.Echo</c> (0 = don't display the result) and <c>$CircleCalc.Parse</c>
/// (0 = don't feed the result to script actions/triggers). Set <c>Echo 0</c> +
/// <c>Parse 1</c> to consume the output from a script without cluttering the window
/// (public #207).</para>
/// </summary>
public sealed class CircleCalcExtension : IGameExtension
{
    public string Name        => "Circle Calculator";
    public string Version     => "2.0";
    public string Description => "Calculates guild circle requirements and sorts your skills by rank (/calc, /sort).";

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => _enabled = value; }

    private IExtensionHost _host = null!;
    private readonly CircleData _data = new();

    private enum Mode { Idle, Calculating, Sorting }
    private Mode _mode = Mode.Idle;
    private bool _parsing;                          // inside the exp dump body
    private bool _awaitingGuild;                    // waiting on `info` for guild auto-detect
    private GuildType? _guild;
    private int _targetCircle;                      // 0 = "next circle up"
    private string _sortLabel = "all skillsets";
    private IReadOnlySet<string>? _sortFilter;      // custom-group skill filter, if any
    private readonly Dictionary<string, double> _ranks = new(StringComparer.Ordinal);

    // "Small Edged:  142 71% examining" — name, ranks, learning %. Two skills per line.
    private static readonly Regex SkillRe = new(
        @"(?<name>[A-Z][A-Za-z '\-]+?):\s+(?<rank>\d+)\s+(?<pct>\d+)%",
        RegexOptions.Compiled);

    public void Initialize(IExtensionHost host)
    {
        _host = host;
        _data.ConfigDir = host.ConfigDir;
        _data.LoadAll(host.Log);
    }

    public void OnCommandSent(string command) { }
    public void Shutdown() { }

    /// <summary>Character switch — abandon any in-flight calculation so a half-read
    /// dump from the previous character doesn't bleed into the next.</summary>
    public void OnReset() => Reset();

    // ── command entry ─────────────────────────────────────────────────────────────

    public bool OnSlashCommand(string input)
    {
        var t = input.Trim();
        if (t == "/cc" || t == "/cc ?" || t == "/calc ?") { Help(); return true; }

        if (t.StartsWith("/cc ", StringComparison.OrdinalIgnoreCase))
        {
            var sub = t[4..].Trim().ToLowerInvariant();
            switch (sub)
            {
                case "reload":     _data.LoadAll(Echo); break;
                case "reloadreqs": _data.LoadReqs(Echo); break;
                case "reloadsort": _data.LoadSort(Echo); break;
                default:           Help(); break;
            }
            return true;
        }

        if (t.Equals("/calc", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("/calc ", StringComparison.OrdinalIgnoreCase))
        { StartCalc(t); return true; }

        if (t.Equals("/sort", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("/sort ", StringComparison.OrdinalIgnoreCase))
        { StartSort(t); return true; }

        return false;
    }

    private void StartCalc(string t)
    {
        if (!_data.ReqsLoaded) { Echo("Circle Calc: no requirements loaded — calculating is disabled."); return; }

        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);   // [/calc, (guild?), (circle?)]
        string? guildArg = null;
        _targetCircle = 0;

        // a trailing integer is the target circle; a remaining token is the guild.
        var rest = parts.Skip(1).ToList();
        if (rest.Count > 0 && int.TryParse(rest[^1], out var circle))
        {
            if (circle is < 2 or > 500) { Echo("Circle Calc: circle must be between 2 and 500."); return; }
            _targetCircle = circle;
            rest.RemoveAt(rest.Count - 1);
        }
        if (rest.Count > 0) guildArg = rest[0];
        if (rest.Count > 1) { Help(); return; }

        guildArg ??= Cfg("CircleCalc.Guild");

        _mode          = Mode.Calculating;
        _parsing       = false;
        _awaitingGuild = false;
        _ranks.Clear();

        if (guildArg is null)
        {
            _guild = null;
            _awaitingGuild = true;
            _host.SendCommand("info");     // auto-detect the guild, then we send `exp 0`
            return;
        }
        if (!_data.TryGuild(guildArg, out var g))
        {
            Echo($"Circle Calc: unknown guild '{guildArg}'. Spell it with no spaces (moonmage, warriormage).");
            _mode = Mode.Idle;
            return;
        }
        _guild = g;
        _host.SendCommand("exp 0");
    }

    private void StartSort(string t)
    {
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);   // [/sort, (set?), (rank?)]
        var rest  = parts.Skip(1).ToList();
        int? minRank = null;
        if (rest.Count > 0 && int.TryParse(rest[^1], out var r))
        {
            if (r < 1) { Help(); return; }
            minRank = r;
            rest.RemoveAt(rest.Count - 1);
        }
        var setArg = rest.Count > 0 ? rest[0] : null;
        if (rest.Count > 1) { Help(); return; }

        _mode          = Mode.Sorting;
        _parsing       = false;
        _awaitingGuild = false;
        _ranks.Clear();
        _sortFilter    = null;

        // skillset keyword → DR exp filter; otherwise a custom group from SortGroups.xml.
        var (kind, setName) = ParseSet(setArg);
        if (kind != SetKind.Custom)
        {
            _sortLabel = setName is null ? "all skillsets" : $"the {setName} skillset";
            _host.SendCommand("exp " + (setName is null ? "" : setName + " ") + (minRank?.ToString() ?? "all"));
            return;
        }

        if (!_data.SortLoaded || !_data.SortByShort.TryGetValue(setArg!, out var grp))
        {
            Echo($"Circle Calc: invalid sort group '{setArg}'.");
            _mode = Mode.Idle;
            return;
        }
        _sortLabel  = grp.Name;
        _sortFilter = grp.Skills;
        var setPart = grp.Skillset == "all" ? "" : grp.Skillset + " ";
        _host.SendCommand("exp " + setPart + (minRank?.ToString() ?? "all"));
    }

    // ── reading the exp dump ──────────────────────────────────────────────────────

    public void OnGameLine(string line)
    {
        if (_mode == Mode.Idle) return;

        // auto-detect guild from `info` ("... Guild: X ...")
        if (_mode == Mode.Calculating && _guild is null && _awaitingGuild)
        {
            var idx = line.IndexOf("Guild:", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var guildName = ExtractGuild(line, idx + 6);
                if (_data.TryGuild(guildName, out var g) ||
                    _data.TryGuild(FirstToken(guildName), out g))
                {
                    _guild = g; _awaitingGuild = false;
                    _host.SendCommand("exp 0");
                }
                else { Echo($"Circle Calc: couldn't match your guild '{guildName}'."); Reset(); }
            }
            return;
        }

        if (_parsing)
        {
            if (line.Contains("EXP HELP", StringComparison.OrdinalIgnoreCase)) { Finish(); return; }
            Accumulate(line);
        }
        else if (line.TrimStart().StartsWith("Circle:", StringComparison.Ordinal))
        {
            _parsing = true;
        }
    }

    /// <summary>Fallback finaliser: DR's exp footer wording can vary, so close out
    /// the dump on the next prompt once we've started reading skills.</summary>
    public void OnPrompt()
    {
        if (_mode != Mode.Idle && _parsing && _ranks.Count > 0) Finish();
    }

    private void Accumulate(string line)
    {
        foreach (Match m in SkillRe.Matches(line))
        {
            var name = m.Groups["name"].Value.Trim();
            var rank = int.Parse(m.Groups["rank"].Value) + int.Parse(m.Groups["pct"].Value) / 100.0;
            _ranks[name] = rank;     // ranks.pct, e.g. 142.71 (floor = ranks)
        }
    }

    private void Finish()
    {
        try
        {
            List<string> lines;
            if (_mode == Mode.Calculating)
            {
                if (_guild is null) { Reset(); return; }
                var opt = new CircleEngine.Options(
                    _targetCircle,
                    Cfg("CircleCalc.Sort") == "1",
                    int.TryParse(Cfg("CircleCalc.Display"), out var d) ? d : 0);
                lines = CircleEngine.Calculate(_guild, _ranks, opt);
            }
            else
            {
                lines = CircleEngine.Sort(_ranks, _sortLabel, _sortFilter);
            }
            EmitResult(lines);
        }
        catch (Exception ex) { Echo("Circle Calc error: " + ex.Message); }
        finally { Reset(); }
    }

    /// <summary>Emit the calculation/sort result honouring the Genie 4 display/parse
    /// toggles, both default ON:
    /// <list type="bullet">
    /// <item><c>$CircleCalc.Echo = 0</c> — don't show the result in the game window.</item>
    /// <item><c>$CircleCalc.Parse = 0</c> — don't feed the result to script actions/triggers.</item>
    /// </list>
    /// The two legs are independent, so a script can parse the output silently
    /// (<c>Echo 0</c>, <c>Parse 1</c>) — the reporter's use case in public #207.
    /// Usage/error lines still go through the plain <see cref="Echo(string)"/> path
    /// (always shown); the toggles govern the result only, matching Genie 4.</summary>
    private void EmitResult(IReadOnlyList<string> lines)
    {
        var echo  = Cfg("CircleCalc.Echo")  != "0";   // default on ("0" suppresses the window echo)
        var parse = Cfg("CircleCalc.Parse") != "0";   // default on ("0" suppresses action feeding)
        foreach (var l in lines)
            _host.EchoRouted(l, echo, parse);
    }

    private void Reset()
    {
        _mode          = Mode.Idle;
        _parsing       = false;
        _awaitingGuild = false;
        _guild         = null;
        _sortFilter    = null;
        _ranks.Clear();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>Read an optional <c>$CircleCalc.*</c> setting: a persistent <c>#var</c>
    /// (as Genie 4 stored these) takes precedence, falling back to a session/game-state
    /// global of the same name; null/blank → unset.</summary>
    private string? Cfg(string key)
    {
        var v = _host.GetUserVar(key);
        if (string.IsNullOrWhiteSpace(v) && _host.Globals.TryGetValue(key, out var g)) v = g;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>Pull the guild name out of an <c>info</c> line after "Guild:", cutting
    /// at the first column gap (2+ spaces) so trailing fields don't leak in.</summary>
    private static string ExtractGuild(string line, int from)
    {
        var rest = line[from..].TrimStart();
        var gap  = rest.IndexOf("  ", StringComparison.Ordinal);
        if (gap >= 0) rest = rest[..gap];
        return rest.Trim();
    }

    private static string FirstToken(string s)
    {
        var sp = s.IndexOf(' ');
        return sp >= 0 ? s[..sp] : s;
    }

    private enum SetKind { All, Known, Custom }

    /// <summary>Classify a /sort argument: a built-in skillset (with its canonical
    /// DR exp keyword), "all"/none, or something to look up as a custom group.</summary>
    private static (SetKind kind, string? name) ParseSet(string? s) => s?.ToLowerInvariant() switch
    {
        null                                                  => (SetKind.All, null),
        "all"                                                 => (SetKind.All, null),
        "armor" or "armo" or "arm"                            => (SetKind.Known, "armor"),
        "weapons" or "weapon" or "weapo" or "weap" or "wea"   => (SetKind.Known, "weapons"),
        "magic" or "magi" or "mag"                            => (SetKind.Known, "magic"),
        "survival" or "surviva" or "surviv" or "survi"
            or "surv" or "sur"                                => (SetKind.Known, "survival"),
        "lore" or "lor"                                       => (SetKind.Known, "lore"),
        _                                                     => (SetKind.Custom, s),
    };

    private void Help()
    {
        Echo($"Circle Calculator (v{Version}) — usage:");
        Echo("  /calc                  calculate to your next circle (uses $CircleCalc.Guild, or 'info')");
        Echo("  /calc <guild>          calculate for a guild (no spaces: moonmage, warriormage)");
        Echo("  /calc <circle>         what you need for a specific circle");
        Echo("  /calc <guild> <circle> both");
        Echo("  /sort [all]            list all your skills, highest rank first");
        Echo("  /sort <skillset>       armor | weapons | magic | survival | lore");
        Echo("  /sort <group>          a custom group from SortGroups.xml");
        Echo("  /sort [...] <rank>     only skills at or above <rank>");
        Echo("  /cc reload[reqs|sort]  reload the data files");
        Echo("  settings: $CircleCalc.Echo 0 hides the result; $CircleCalc.Parse 0 stops");
        Echo("            it feeding actions (both default on).");
    }

    private void Echo(string line) => _host.Echo(line);
}
