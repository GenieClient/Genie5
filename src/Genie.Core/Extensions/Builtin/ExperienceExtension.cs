using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Genie.Core.Events;

namespace Genie.Core.Extensions.Builtin;

/// <summary>
/// Built-in Experience tracker (was Plugin_EXPTrackerV5; ports Genie 4's EXPTracker).
/// Reads the live <c>&lt;component id='exp Skill'&gt;… rank pct% mindstate …</c> push in
/// <see cref="OnXml"/> and the <c>exp</c> full-dump lines in <see cref="OnGameLine"/>,
/// keeps a per-skill table (rank, mindstate 0–34), publishes the Genie 4-parity
/// script globals, and re-renders the actively-learning skills to the "Experience"
/// dock panel on each prompt.
///
/// <para>Skill names are accepted dynamically from the stream; the 35 learning-state
/// names are hardcoded (they effectively never change in DR).</para>
///
/// <para>Both live-pulse shapes are handled: DR's per-character <c>BRIEFEXP</c>
/// setting swaps the mindstate word for a <c>[ n/34]</c> number, so a character
/// with BRIEFEXP ON emits <c>Stealth:  550 73% [ 5/34]</c> where another emits
/// <c>Stealth:  550 73% dabbling</c>.</para>
/// </summary>
public sealed class ExperienceExtension : IGameExtension
{
    public string Name        => "Experience";
    public string Version     => "2.0";
    public string Description => "Tracks skill ranks and learning rates; $Skill.* / $TDPs globals + a dock panel.";

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) _host?.SetWindow(WindowName, "(Experience disabled)");
            else        _dirty = true;
        }
    }

    private const string WindowName = "Experience";

    private IExtensionHost _host = null!;
    private bool _dirty;

    /// <summary>A skill's (rank, percent, mindstate) actually changed —
    /// (name, rank, percent, mindstate). Deduplicated (identical pulses don't
    /// fire) and raised outside the internal lock on the connection read-loop
    /// thread; handlers must be fast and non-throwing. Feeds the skill-history
    /// recorder (Analytics).</summary>
    public event Action<string, int, int, int>? SkillUpdated;

    /// <summary>The TDP total was (re)reported — may repeat the same value.</summary>
    public event Action<int>? TdpUpdated;

    private readonly Dictionary<string, SkillInfo> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly record struct SkillInfo(int Rank, int Percent, int Mindstate);

    // Guards _skills structural access. Writes (Apply's insert, the empty-clear's
    // Remove) run on the connection read-loop thread; the /exp console command and
    // OnReset read/clear it on the UI thread. Without this, a /exp typed while a
    // skill is pulsing experience can enumerate _skills mid-mutation →
    // "collection was modified".
    private readonly object _gate = new();

    /// <summary>First-seen (rank, percent) per skill this session — the baseline the
    /// optional rank-gain display subtracts from (#144). Guarded by <see cref="_gate"/>;
    /// cleared on character switch.</summary>
    private readonly Dictionary<string, (int Rank, int Percent)> _baseline = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UTC time the first skill datum of this session arrived — drives the
    /// "Session H:MM:SS" header (#144). Null until data arrives; reset on character
    /// switch. Guarded by <see cref="_gate"/>.</summary>
    private DateTime? _sessionStart;

    /// <summary>Pulse-echo accumulators (Genie 4 EXPTracker's EchoExp, public #272):
    /// mindstate rises collect into <see cref="_echoLearned"/> ("Skill(+2)") and
    /// drops into <see cref="_echoPulsed"/> ("Skill(-1)", deduped by name like G4);
    /// <see cref="OnPrompt"/> flushes each as one line. Guarded by <see cref="_gate"/>.</summary>
    private readonly List<string> _echoLearned = new();
    private readonly List<string> _echoPulsed  = new();
    private readonly HashSet<string> _echoPulsedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Latest rested-EXP snapshot from the <c>exp rexp</c> component
    /// ("5:58", "5:56", "17:11" — the " hours" suffix stripped), or null before any
    /// arrives. Guarded by <see cref="_gate"/>; cleared on character switch.</summary>
    private (string Stored, string Usable, string Refresh)? _rested;

    /// <summary>Canonical 35 DR learning states (0–34), authoritative order from
    /// Genie 4's EXPTracker.</summary>
    private static readonly string[] MindStates =
    {
        "clear", "dabbling", "perusing", "learning", "thoughtful", "thinking",
        "considering", "pondering", "ruminating", "concentrating", "attentive",
        "deliberative", "interested", "examining", "understanding", "absorbing",
        "intrigued", "scrutinizing", "analyzing", "studious", "focused",
        "very focused", "engaged", "very engaged", "cogitating", "fascinated",
        "captivated", "engrossed", "riveted", "very riveted", "rapt",
        "very rapt", "enthralled", "nearly locked", "mind lock",
    };

    /// <summary>Genie 4 EXPTracker's master skill order (its "Left to Right" sort,
    /// recovered from the plugin) — the order DR's own <c>exp</c> table walks the
    /// skillsets. The hundreds band doubles as the category: 0xx Armor, 1xx Weapons,
    /// 2xx Magic, 3xx Survival, 4xx Lore. Skills DR adds later fall to
    /// <see cref="UnknownOrder"/> (sorted last, alphabetically).</summary>
    private static readonly Dictionary<string, int> SkillOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Shield Usage"] = 0,   ["Light Armor"] = 1,     ["Chain Armor"] = 2,     ["Brigandine"] = 3,
        ["Plate Armor"] = 4,    ["Defending"] = 5,       ["Conviction"] = 6,
        ["Parry Ability"] = 100, ["Small Edged"] = 101,  ["Large Edged"] = 102,   ["Twohanded Edged"] = 103,
        ["Small Blunt"] = 104,  ["Large Blunt"] = 105,   ["Twohanded Blunt"] = 106, ["Slings"] = 107,
        ["Bow"] = 108,          ["Crossbow"] = 109,      ["Staves"] = 110,        ["Polearms"] = 111,
        ["Light Thrown"] = 112, ["Heavy Thrown"] = 113,  ["Brawling"] = 114,      ["Offhand Weapon"] = 115,
        ["Melee Mastery"] = 116, ["Missile Mastery"] = 117, ["Expertise"] = 118,
        ["Lunar Magic"] = 200,  ["Elemental Magic"] = 200, ["Holy Magic"] = 200,  ["Life Magic"] = 200,
        ["Arcane Magic"] = 200, ["Inner Magic"] = 200,   ["Inner Fire"] = 200,
        ["Attunement"] = 201,   ["Arcana"] = 202,        ["Targeted Magic"] = 203, ["Augmentation"] = 204,
        ["Debilitation"] = 205, ["Utility"] = 206,       ["Warding"] = 207,       ["Sorcery"] = 208,
        ["Astrology"] = 209,    ["Summoning"] = 209,     ["Theurgy"] = 209,
        ["Evasion"] = 300,      ["Athletics"] = 301,     ["Perception"] = 302,    ["Stealth"] = 303,
        ["Locksmithing"] = 304, ["Thievery"] = 305,      ["First Aid"] = 306,     ["Outdoorsmanship"] = 307,
        ["Skinning"] = 308,     ["Backstab"] = 309,      ["Scouting"] = 309,      ["Thanatology"] = 309,
        ["Forging"] = 401,      ["Engineering"] = 402,   ["Outfitting"] = 403,    ["Alchemy"] = 404,
        ["Enchanting"] = 405,   ["Scholarship"] = 406,   ["Mechanical Lore"] = 407, ["Appraisal"] = 408,
        ["Performance"] = 409,  ["Bardic Lore"] = 410,   ["Empathy"] = 410,       ["Tactics"] = 410,
        ["Trading"] = 410,
    };

    private const int UnknownOrder = 500;

    /// <summary>Left-to-right index for a skill (Genie 4 master order); unknown
    /// skills get <see cref="UnknownOrder"/>.</summary>
    internal static int OrderOf(string name)
        => SkillOrder.TryGetValue(name, out var v) ? v : UnknownOrder;

    /// <summary>Category name for a skill ("armor" / "weapons" / "magic" /
    /// "survival" / "lore"), or "" for a skill not in the master table.</summary>
    internal static string CategoryOf(string name) => OrderOf(name) switch
    {
        < 100 => "armor",
        < 200 => "weapons",
        < 300 => "magic",
        < 400 => "survival",
        < UnknownOrder => "lore",
        _ => "",
    };

    /// <summary>Rank of a skill's category within the user's
    /// <c>experiencesortorder</c> list. Listed categories come first in the
    /// user's order; a known category the user omitted keeps its Genie 4
    /// relative position after them; unknown skills always land last.</summary>
    internal static int GroupRank(string name, string[] order)
    {
        var cat = CategoryOf(name);
        if (cat.Length == 0) return order.Length + 10;         // unknown skill → last
        var idx = Array.IndexOf(order, cat);
        return idx >= 0 ? idx : order.Length + OrderOf(name) / 100;  // omitted → after listed, G4-relative
    }

    private static readonly Regex TagRe    = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex DigitsRe = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex SkillLineRe = new(
        @"([A-Z][A-Za-z '\-]+?):\s+(\d+)\s+(\d+)%\s+([a-z][a-z ]*?)(?=\s*\(|\s{2,}|$)",
        RegexOptions.Compiled);

    /// <summary>BRIEFEXP-ON form of the live pulse: the mindstate arrives as
    /// <c>[ 5/34]</c> instead of the word ("dabbling"). <c>BRIEFEXP</c> is a
    /// per-character DR setting, so one character's Experience window would go
    /// stale between manual <c>exp</c> dumps while another's updated live — the
    /// full-dump text table always spells the mindstate out, which is why the
    /// manual path kept working. Matches Lich's <c>BriefExpOn</c> pattern.</summary>
    private static readonly Regex SkillLineBriefRe = new(
        @"([A-Z][A-Za-z '\-]*?):\s+(\d+)\s+(\d+)%\s*\[\s*(\d+)\s*/\s*34\s*\]",
        RegexOptions.Compiled);
    private static readonly Regex TdpRe = new(
        @"Time Development Points:\s*(\d+)", RegexOptions.Compiled);

    /// <summary>The <c>exp rexp</c> component body: <c>Rested EXP Stored: 5:58 hours
    /// Usable This Cycle: 5:56 hours  Cycle Refreshes: 17:11 hours</c>. Values can be
    /// bare ("6 hours") or H:MM; the " hours" suffix is dropped on capture.</summary>
    private static readonly Regex RestedRe = new(
        @"Rested EXP Stored:\s*(.+?)\s+Usable This Cycle:\s*(.+?)\s+Cycle Refreshes:\s*(.+)$",
        RegexOptions.Compiled);

    public void Initialize(IExtensionHost host) => _host = host;
    public void OnCommandSent(string command) { }
    public void Shutdown() { }

    /// <summary>Character switch (clear-then-load connect): drop the accumulated
    /// skill table so the next character's Experience window and <c>$Skill.*</c>
    /// globals start blank instead of inheriting the previous character's ranks and
    /// learning rates. A same-character reconnect does NOT call this.</summary>
    public void OnReset()
    {
        lock (_gate)
        {
            _skills.Clear();
            _baseline.Clear();
            _sessionStart = null;
            _rested = null;
            _echoLearned.Clear();
            _echoPulsed.Clear();
            _echoPulsedNames.Clear();
        }
        _dirty = false;
        _host?.SetWindow(WindowName, Render());
    }

    public void OnGameEvent(GameEvent ev)
    {
        // The live experience push arrives as a parsed ComponentEvent per skill —
        // <component id='exp Attunement'>Attunement: 550 73% dabbling</component> —
        // reliable across the connection's tag-splitting chunk boundaries (raw XML
        // is not). DR also pushes a few non-skill sub-components under the same
        // "exp " prefix (tdp / rexp / favor) which we handle or skip.
        if (ev is not ComponentEvent c
            || !c.ComponentId.StartsWith("exp ", StringComparison.Ordinal))
            return;

        var sub   = c.ComponentId.Substring(4).Trim();   // "Attunement", "tdp", "rexp", …
        var inner = TagRe.Replace(c.Content ?? "", "").Trim();

        if (sub.Equals("tdp", StringComparison.OrdinalIgnoreCase))
        {
            var m = DigitsRe.Match(inner);               // "TDPs:  3017"
            if (m.Success)
            {
                _host.Globals["TDPs"] = m.Value;
                if (int.TryParse(m.Value, out var tdpVal)) TdpUpdated?.Invoke(tdpVal);
            }
            return;
        }
        if (sub.Equals("rexp", StringComparison.OrdinalIgnoreCase))
        {
            // Rested EXP (public #272). Globals always publish; the panel line is
            // gated on #config experiencerested at render time.
            var r = RestedRe.Match(inner);
            if (r.Success)
            {
                var snap = (Stored:  StripHours(r.Groups[1].Value),
                            Usable:  StripHours(r.Groups[2].Value),
                            Refresh: StripHours(r.Groups[3].Value));
                _host.Globals["RestedEXP.Stored"]  = snap.Stored;
                _host.Globals["RestedEXP.Usable"]  = snap.Usable;
                _host.Globals["RestedEXP.Refresh"] = snap.Refresh;
                lock (_gate) { if (_rested != snap) { _rested = snap; _dirty = true; } }
            }
            return;
        }
        if (sub.Equals("favor", StringComparison.OrdinalIgnoreCase) ||
            sub.Equals("mxp",   StringComparison.OrdinalIgnoreCase))
            return;                                       // not skills — ignore

        if (inner.Length == 0)                            // empty = skill pulsed to clear
        {
            lock (_gate) { if (_skills.Remove(sub)) _dirty = true; }
            _host.Globals[$"{Var(sub)}.LearningRate"] = "0";
            return;
        }
        // The component id always carries the FULL skill name; the body doesn't.
        // Under BRIEFEXP ON, DR abbreviates it there ("IM", "Aug", "Outdoors"),
        // which would register a second, duplicate entry alongside the full-named
        // one the `exp` dump produces. Lich sidesteps this the same way, reading
        // the name from <d cmd='skill Inner Magic'> rather than the text.
        ApplyLine(inner, sub);
    }

    public void OnGameLine(string line)
    {
        // The `exp`/`experience` full dump arrives as plain text (two skills per
        // line). The skill regex is specific enough to be safe across streams.
        if (line.IndexOf('%') >= 0 && line.IndexOf(':') >= 0)
        {
            // The dump spells the mindstate out even under BRIEFEXP ON, but a
            // frontend-echoed pulse can reach this path too — accept both shapes.
            var brief = SkillLineBriefRe.Matches(line);
            if (brief.Count > 0)
                foreach (Match m in brief)
                    ApplyBrief(m.Groups[1].Value.Trim(), m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
            else
                foreach (Match m in SkillLineRe.Matches(line))
                    Apply(m.Groups[1].Value.Trim(), m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
        }

        var tdp = TdpRe.Match(line);
        if (tdp.Success)
        {
            _host.Globals["TDPs"] = tdp.Groups[1].Value;
            if (int.TryParse(tdp.Groups[1].Value, out var tdpVal)) TdpUpdated?.Invoke(tdpVal);
        }
    }

    public void OnPrompt()
    {
        FlushEchoExp();
        if (!_dirty) return;
        _dirty = false;
        _host.SetWindow(WindowName, Render());
    }

    /// <summary>Flush the accumulated pulse echoes as at most two lines —
    /// <c>Learned: Skill(+2), …</c> / <c>Pulsed: Skill(-1), …</c> — matching Genie 4
    /// EXPTracker's EchoExp output shape (numbers are mindstate deltas). DISPLAY-ONLY
    /// by default: the G4-parity trigger/action feed (the <c>#parse Learned: …</c>
    /// leg) is gated behind <c>#config experienceechoparse</c>, because in live
    /// combat these synthetic lines hit the parse pipeline every prompt and a
    /// running combat script whose match/action patterns brush against them fires
    /// commands per pulse — the 2026-08-29 smoke walk flooded DR into a disconnect
    /// that way (uber running). While the toggle is off the accumulators are
    /// discarded so turning it on doesn't replay a backlog.</summary>
    private void FlushEchoExp()
    {
        List<string>? learned = null, pulsed = null;
        lock (_gate)
        {
            if (_echoLearned.Count > 0) { learned = new List<string>(_echoLearned); _echoLearned.Clear(); }
            if (_echoPulsed.Count  > 0) { pulsed  = new List<string>(_echoPulsed);  _echoPulsed.Clear(); }
            _echoPulsedNames.Clear();
        }
        if (!EchoExp()) return;
        var parse = EchoExpParse();
        if (learned is not null) _host.EchoRouted("Learned: " + string.Join(", ", learned), display: true, parse: parse);
        if (pulsed  is not null) _host.EchoRouted("Pulsed: "  + string.Join(", ", pulsed),  display: true, parse: parse);
    }

    /// <summary>Re-render the panel immediately (without waiting for the next prompt) —
    /// used when <c>#config experiencedensity</c> changes so the View → Density menu and
    /// the command give instant feedback. No-op while disabled.</summary>
    public void Refresh()
    {
        if (_enabled) _host?.SetWindow(WindowName, Render());
    }

    public bool OnSlashCommand(string input)
    {
        var t = input.Trim();

        // /track — the Genie 4 EXPTracker plugin's command surface. Community
        // scripts drive it blind (uber.cmd:2127 sends `put /track clear` at
        // hunt start to zero the gain readout); in G4 the plugin consumed
        // every /track form at the outbound-send sink, so the whole namespace
        // is claimed here — an unrecognized subcommand gets usage instead of
        // leaking to the game and bouncing with "Please rephrase" (smoke
        // 2026-08-03 finding #9).
        // /trackreset — the one-word reset variant (typed from muscle memory in
        // the 2026-08-04 smoke: it leaked to DR and bounced). Same action as
        // /track clear.
        var isTrackReset = t.Equals("/trackreset", StringComparison.OrdinalIgnoreCase);

        if (isTrackReset ||
            (t.StartsWith("/track", StringComparison.OrdinalIgnoreCase) &&
             (t.Length == 6 || char.IsWhiteSpace(t[6]))))
        {
            var arg = isTrackReset ? "reset"
                    : t.Length > 6 ? t[6..].Trim() : string.Empty;
            if (arg.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                lock (_gate)
                {
                    // Re-baseline to the CURRENT ranks (not just clear): every
                    // known skill reads +0.00 from this moment, matching the
                    // G4 plugin's "start tracking afresh" semantics. The
                    // session clock restarts with it so "Total gained" and
                    // "Session H:MM:SS" describe the same window.
                    _baseline.Clear();
                    foreach (var kv in _skills)
                        _baseline[kv.Key] = (kv.Value.Rank, kv.Value.Percent);
                    _sessionStart = _skills.Count > 0 ? DateTime.UtcNow : null;
                }
                _host.SetWindow(WindowName, Render());
                _host.Echo("[Experience] gain tracking reset.");
            }
            else
            {
                _host.Echo("[Experience] /track clear — reset the session gain " +
                           "baselines (Genie 4 EXPTracker command).");
            }
            return true;
        }

        if (!t.StartsWith("/experience", StringComparison.OrdinalIgnoreCase) &&
            !t.Equals("/exp", StringComparison.OrdinalIgnoreCase))
            return false;
        _host.SetWindow(WindowName, Render());
        _host.Echo("[Experience] window updated (Window → Experience to show it).");
        return true;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Parse one pulse body. <paramref name="skillName"/> overrides the name
    /// the regex reads out of the text — callers that know the canonical name (the
    /// component id) must pass it, because BRIEFEXP ON abbreviates the name in the
    /// body.</summary>
    private void ApplyLine(string line, string? skillName = null)
    {
        // BRIEFEXP ON first: its "[ n/34]" tail can't match the word form, but
        // checking the numeric shape up front keeps the two mutually exclusive.
        var b = SkillLineBriefRe.Match(line);
        if (b.Success)
        {
            ApplyBrief(skillName ?? b.Groups[1].Value.Trim(), b.Groups[2].Value, b.Groups[3].Value, b.Groups[4].Value);
            return;
        }
        var m = SkillLineRe.Match(line);
        if (m.Success)
            Apply(skillName ?? m.Groups[1].Value.Trim(), m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
    }

    private void Apply(string name, string rankText, string pctText, string mindstateText)
    {
        if (!int.TryParse(rankText, out var rank) || !int.TryParse(pctText, out var pct)) return;
        mindstateText = mindstateText.Trim();
        Apply(name, rank, pct, MindstateValue(mindstateText), mindstateText);
    }

    /// <summary>BRIEFEXP-ON variant: the mindstate is already the 0–34 number, so
    /// the learning-rate word is looked up rather than parsed. Out-of-range values
    /// are clamped — the globals and the renderer both index
    /// <see cref="MindStates"/> directly.</summary>
    private void ApplyBrief(string name, string rankText, string pctText, string mindText)
    {
        if (!int.TryParse(rankText, out var rank) || !int.TryParse(pctText, out var pct) ||
            !int.TryParse(mindText, out var mind)) return;
        mind = Math.Clamp(mind, 0, MindStates.Length - 1);
        Apply(name, rank, pct, mind, MindStates[mind]);
    }

    private void Apply(string name, int rank, int pct, int mind, string mindstateText)
    {
        var v = Var(name);
        _host.Globals[$"{v}.Ranks"]            = rank.ToString();
        _host.Globals[$"{v}.LearningRate"]     = mind.ToString();
        _host.Globals[$"{v}.LearningRateName"] = mindstateText;

        var info = new SkillInfo(rank, pct, mind);
        bool changed;
        lock (_gate)
        {
            _sessionStart ??= DateTime.UtcNow;   // session clock starts at the first datum
            _baseline.TryAdd(name, (rank, pct));  // first-seen rank = session baseline (#144)
            var had = _skills.TryGetValue(name, out var prev);
            changed = !(had && prev == info);     // no display change
            if (changed)
            {
                _skills[name] = info;
                // EchoExp accumulation (public #272): the number is the MINDSTATE
                // delta, exactly like Genie 4's build_echo_exp — a rise is
                // "Learned", a drop is "Pulsed" (deduped per flush by name). A
                // skill's first appearance counts as learning from clear (0).
                var delta = mind - (had ? prev.Mindstate : 0);
                if (delta > 0)
                    _echoLearned.Add($"{name}(+{delta})");
                else if (delta < 0 && _echoPulsedNames.Add(name))
                    _echoPulsed.Add($"{name}({delta})");
            }
        }
        if (!changed) return;
        _dirty = true;
        SkillUpdated?.Invoke(name, rank, pct, mind);   // outside _gate — see event doc
    }

    /// <summary>Skill name → global-variable token (spaces → underscores), e.g.
    /// "Small Edged" → "Small_Edged", matching Genie 4's $Skill.* convention.</summary>
    private static string Var(string name) => name.Replace(' ', '_');

    private static int MindstateValue(string state)
    {
        for (int i = 0; i < MindStates.Length; i++)
            if (MindStates[i].Equals(state, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    /// <summary>Experience-window line density (Genie 4 EXPTracker parity), read live
    /// from <c>#config experiencedensity</c> so the slider / command / settings.cfg all
    /// drive one value. Clamped 0–4; an unset or unparseable value falls back to
    /// 0 = Full.</summary>
    private int Density() =>
        int.TryParse(_host.GetConfig("experiencedensity"), out var d) ? Math.Clamp(d, 0, 4) : 0;

    /// <summary>Whether to show per-skill session rank-gain (a "+N.NN" column plus a
    /// session total). Genie 4 EXPTracker parity (#144); read live from
    /// <c>#config experiencetrackgain</c> so the panel checkbox, command line, and
    /// settings.cfg all drive one value.</summary>
    private bool TrackGain() => bool.TryParse(_host.GetConfig("experiencetrackgain"), out var b) && b;

    /// <summary>Whether to use the Genie 4 EXPTracker layout — summary line as a footer
    /// beneath the skill list — instead of the default G5 header. Read live from
    /// <c>#config experienceg4layout</c> so the panel checkbox, command line, and
    /// settings.cfg all drive one value.</summary>
    private bool G4Layout() => bool.TryParse(_host.GetConfig("experienceg4layout"), out var b) && b;

    /// <summary>Sort mode (public #272): 0 = A to Z, 1 = Left to Right (G4 master
    /// order, category-grouped), 2 = Learning Rate high→low, 3 = low→high. Read live
    /// from <c>#config experiencesort</c>; unset falls back to 2, the order the G5
    /// panel has always used.</summary>
    private int SortMode() =>
        int.TryParse(_host.GetConfig("experiencesort"), out var s) ? Math.Clamp(s, 0, 3) : 2;

    /// <summary>Category order for sort mode 1, from <c>#config
    /// experiencesortorder</c> — lower-cased, trimmed; unset/blank falls back to the
    /// Genie 4 order.</summary>
    private string[] SortOrder()
    {
        var raw = _host.GetConfig("experiencesortorder");
        if (string.IsNullOrWhiteSpace(raw)) raw = "armor,weapons,magic,survival,lore";
        return raw.ToLowerInvariant()
                  .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Pulse echo toggle (public #272), read live from
    /// <c>#config experienceecho</c>.</summary>
    private bool EchoExp() => bool.TryParse(_host.GetConfig("experienceecho"), out var b) && b;

    /// <summary><c>#config experienceechoparse</c> — opt-in trigger/action feed
    /// for the pulse-echo lines (see <see cref="FlushEchoExp"/>).</summary>
    private bool EchoExpParse() => bool.TryParse(_host.GetConfig("experienceechoparse"), out var b) && b;

    /// <summary>Rested-EXP summary line toggle (public #272), read live from
    /// <c>#config experiencerested</c>.</summary>
    private bool ShowRested() => bool.TryParse(_host.GetConfig("experiencerested"), out var b) && b;

    /// <summary>"5:58 hours" / "6 hours" → "5:58" / "6" — the panel line and the
    /// $RestedEXP.* globals carry the bare duration.</summary>
    internal static string StripHours(string v)
    {
        v = v.Trim();
        if (v.EndsWith(" hours", StringComparison.OrdinalIgnoreCase)) return v[..^6].TrimEnd();
        if (v.EndsWith(" hour",  StringComparison.OrdinalIgnoreCase)) return v[..^5].TrimEnd();
        return v;
    }

    /// <summary>Render one learning row at the given density. 0 = Full (rank, %,
    /// learning word, count); 1 = drop the <c>(n/34)</c> count; 2 = numbers only
    /// (rank + % + numeric mindstate); 3 = short skill name + rank + % + numeric
    /// mindstate; 4 = Brief (short name + rank). The "Numbers only" and "Short names"
    /// stops carry the mindstate as a number (#144) — it's the most-watched field.
    /// Column widths match within a name style so the list stays aligned.</summary>
    internal static string FormatLine(string name, int rank, int percent, int mindstate, int density) =>
        density switch
        {
            >= 4 => $"{ShortName(name),-12} {rank,3}",
            3    => $"{ShortName(name),-12} {rank,3} {percent,2}%  {mindstate,2}",
            2    => $"{name,-18} {rank,3} {percent,2}%  {mindstate,2}",
            1    => $"{name,-18} {rank,3} {percent,2}%  {MindStates[mindstate]}",
            _    => $"{name,-18} {rank,3} {percent,2}%  {MindStates[mindstate]} ({mindstate}/34)",
        };

    /// <summary>Fractional ranks gained: the whole-rank delta plus the percent-into-rank
    /// delta, so a rank 100 34% baseline now at 101 5% reads as +0.71.</summary>
    internal static double GainValue(int rank, int percent, int baseRank, int basePercent)
        => (rank - baseRank) + (percent - basePercent) / 100.0;

    /// <summary>Signed 2-dp gain string ("+2.34", "+0.00"). Invariant culture so the
    /// decimal point never localises to a comma.</summary>
    internal static string FormatGain(double gain)
        => (gain >= 0 ? "+" : "") + gain.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Elapsed session time — "H:MM:SS" once past an hour, "M:SS" under it.
    /// Clamped at zero (replay timestamps can run negative).</summary>
    internal static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int h = (int)t.TotalHours;
        return h > 0 ? $"{h}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Compact skill name for the short/brief densities: every word except
    /// the last is clipped to a 2-letter prefix ("Small Edged" → "Sm Edged",
    /// "Twohanded Blunt" → "Tw Blunt"); single-word names ("Astrology") are left whole.
    /// Deterministic and table-free, so it covers any skill DR adds.</summary>
    internal static string ShortName(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1) return name;
        for (int i = 0; i < words.Length - 1; i++)
            if (words[i].Length > 2) words[i] = words[i].Substring(0, 2);
        return string.Join(' ', words);
    }

    private string Render()
    {
        List<KeyValuePair<string, SkillInfo>> learning;
        Dictionary<string, (int Rank, int Percent)> baseline;
        DateTime? start;
        (string Stored, string Usable, string Refresh)? rested;
        int locked;
        double totalGain;
        var sortMode  = SortMode();
        var sortOrder = sortMode == 1 ? SortOrder() : Array.Empty<string>();
        lock (_gate)
        {
            var active = _skills.Where(kv => kv.Value.Mindstate > 0);
            // Sort modes (public #272), Genie 4 EXPTracker's $ExpTracker.SortType:
            // 0 A to Z · 1 Left to Right (category-grouped, user-orderable) ·
            // 2 Learning Rate high→low (the long-standing G5 default) · 3 reverse.
            learning = (sortMode switch
            {
                0 => active.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase),
                1 => active.OrderBy(kv => GroupRank(kv.Key, sortOrder))
                           .ThenBy(kv => OrderOf(kv.Key))
                           .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase),
                3 => active.OrderBy(kv => kv.Value.Mindstate)
                           .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase),
                _ => active.OrderByDescending(kv => kv.Value.Mindstate)
                           .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase),
            }).ToList();
            rested = _rested;
            locked = _skills.Count(kv => kv.Value.Mindstate >= MindStates.Length - 1);  // 34 = mind lock
            start  = _sessionStart;
            totalGain = 0;
            foreach (var kv in _skills)
                if (_baseline.TryGetValue(kv.Key, out var b))
                    totalGain += GainValue(kv.Value.Rank, kv.Value.Percent, b.Rank, b.Percent);
            baseline = new Dictionary<string, (int Rank, int Percent)>(_baseline, StringComparer.OrdinalIgnoreCase);
        }

        var density   = Density();
        var trackGain = TrackGain();
        var g4        = G4Layout();

        // Summary: count of skills currently absorbing experience (Genie 4 EXPTracker's
        // "Learning Skills: N" — a glance tells you if training is off, e.g. 44 when you
        // expect 46), plus mind-locked count and session clock (#144). Placed as the top
        // header in the default G5 layout, or as a footer beneath the list in the G4
        // layout (#config experienceg4layout) to match the classic EXPTracker window.
        var summary = new StringBuilder();
        summary.Append("Learning Skills: ").Append(learning.Count);
        if (locked > 0)     summary.Append("   Locked: ").Append(locked);
        if (start is { } s) summary.Append("   Session ").Append(FormatElapsed(DateTime.UtcNow - s));
        // Rested-EXP summary line (public #272, G4 DisplayREXP): rides directly
        // under the "Learning Skills" summary in whichever layout placed it.
        if (ShowRested() && rested is { } r)
            summary.Append('\n')
                   .Append("Rested: stored ").Append(r.Stored)
                   .Append(" · usable ").Append(r.Usable)
                   .Append(" · refreshes ").Append(r.Refresh);

        const string Rule = "──────────────────────────────────────";

        var sb = new StringBuilder();
        if (!g4)
        {
            sb.Append(summary).Append('\n');
            sb.Append(Rule).Append('\n');
        }

        foreach (var (name, info) in learning)
        {
            sb.Append(FormatLine(name, info.Rank, info.Percent, info.Mindstate, density));
            if (trackGain && baseline.TryGetValue(name, out var b))
                sb.Append("  ").Append(FormatGain(GainValue(info.Rank, info.Percent, b.Rank, b.Percent)));
            sb.Append('\n');
        }
        if (learning.Count == 0)
            sb.Append("(nothing learning — train a skill, or type 'exp')\n");

        if (g4)
        {
            sb.Append(Rule).Append('\n');
            sb.Append(summary).Append('\n');
        }
        if (trackGain && start is not null)
            sb.Append("Total gained: ").Append(FormatGain(totalGain)).Append(" ranks\n");
        return sb.ToString().TrimEnd();
    }
}
