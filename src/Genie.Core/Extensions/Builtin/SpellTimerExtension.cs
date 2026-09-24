using System.Text;
using System.Text.RegularExpressions;
using Genie.Core.Events;

namespace Genie.Core.Extensions.Builtin;

/// <summary>
/// Built-in Spell Timer (was the external Plugin_SpellTimerV5; originally
/// GenieClient/Plugin_SpellTimer by UFTimmy for Genie 4). Turns DR's <b>Active
/// Spells</b> window into persistent script variables and re-renders the live list
/// into the "Active Spells" dock panel.
///
/// <para><b>How DR sends it.</b> The active-spells window is the <c>percWindow</c>
/// stream: each refresh is a <c>&lt;clearStream id="percWindow"/&gt;</c> followed by
/// one <c>&lt;pushStream id="percWindow"/&gt;Spell Name  (N roisaen)</c> line per
/// active spell. The full list is re-sent every prompt, so a dropped spell simply
/// stops appearing. We parse the whole raw chunk in <see cref="OnXml"/> so the
/// clearStream boundary and its spell lines stay in true wire order.</para>
///
/// <para><b>Script surface (Genie 4 parity).</b> <c>$SpellTimer.&lt;Spell&gt;.active</c>
/// (1/0), <c>$SpellTimer.&lt;Spell&gt;.duration</c>, and (charge-rule spells)
/// <c>$SpellTimer.&lt;Spell&gt;.charge</c>. Spell names auto-discover; the only
/// configurable parts are the special-parse rules and variable-name overrides in
/// <see cref="SpellTimerConfig"/> (<c>spelltimer.cfg</c>).</para>
/// </summary>
public sealed partial class SpellTimerExtension : IGameExtension
{
    public string Name        => "SpellTimer";
    public string Version     => "2.0";
    public string Description => "Active Spells window → $SpellTimer.* globals and a dock panel.";

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) _host?.SetWindow(WindowName, "(Spell Timer disabled)");
            else        _dirty = true;
        }
    }

    private const string WindowName = "Active Spells";
    private const int Indefinite = 999;

    private IExtensionHost _host = null!;
    private readonly SpellTimerConfig _config = new();
    private bool _dirty;
    private bool _configLoaded;

    private sealed partial class Spell
    {
        public required string Name;
        public bool Active;
        public int  Duration;   // roisaen remaining (Indefinite => 999)
        public int  Charge;     // charge-rule spells (e.g. Stellar Collector)
        public bool Fading;
    }

    private const string PercStream = "percWindow";

    private readonly Dictionary<string, Spell> _spells = new(StringComparer.Ordinal);
    private readonly HashSet<string> _poppedThisRound = new(StringComparer.Ordinal);
    // Guards _spells structural access. Writes (Get's insert) run on the connection
    // read-loop thread; the /spelltimer console command and OnReset read/clear it on
    // the UI thread. Without this, a /spelltimer typed mid-combat (percWindow refreshes
    // every prompt) can enumerate _spells while a new spell is being inserted →
    // "collection was modified". Field mutations on existing Spell objects are atomic
    // enough and don't need the lock; only the dictionary structure does.
    private readonly System.Threading.Lock _gate = new();

    private static readonly Regex SpellLineRe = SpellLineRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"^(.+?)\s+\((.+)\)\s*$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex SpellLineRegex();
    // DR uses singular "roisan" for a 1-roisaen duration; "roisae?n" matches both.
    private static readonly Regex RoisaenRe = RoisaenRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"(\d+)\s+roisae?n", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex RoisaenRegex();
    private static readonly Regex PercentRe = PercentRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"(\d+)%", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex PercentRegex();

    public void Initialize(IExtensionHost host) => _host = host;

    // Config (special-parse rules + var overrides) loads lazily on first use: the
    // host's ConfigDir isn't resolvable until after the engine's Config is wired,
    // which happens after Initialize.
    private void EnsureConfig()
    {
        if (_configLoaded) return;
        _configLoaded = true;
        _config.Load(_host.ConfigDir);
    }

    public void OnGameLine(string line) { }
    public void OnCommandSent(string command) { }
    public void Shutdown() { }

    /// <summary>Character switch (clear-then-load connect): drop the accumulated
    /// spell table so the next character's Active Spells window and
    /// <c>$SpellTimer.*</c> globals start blank instead of inheriting the previous
    /// character's timers. A same-character reconnect does NOT call this.</summary>
    public void OnReset()
    {
        lock (_gate) { _spells.Clear(); _poppedThisRound.Clear(); }
        _dirty = false;
        _host?.SetWindow(WindowName, Render());
    }

    public void OnGameEvent(GameEvent ev)
    {
        // DR re-sends the whole Active Spells list each refresh as a
        // ClearStreamEvent(percWindow) followed by one TextEvent per active spell
        // on the percWindow stream. Driving off the parser's typed events keeps the
        // clear boundary and its spell lines in true wire order — and, unlike raw
        // XML, the spell text is never split from its <pushStream> tag.
        switch (ev)
        {
            case ClearStreamEvent cs when cs.StreamId.Equals(PercStream, StringComparison.Ordinal):
                EnsureConfig();
                FinishRound();                       // deactivate anything last refresh dropped
                break;
            case TextEvent te when te.Stream.Equals(PercStream, StringComparison.Ordinal):
                EnsureConfig();
                ApplySpellLine(te.Text);
                break;
        }
    }

    public void OnPrompt()
    {
        if (!_dirty) return;
        _dirty = false;
        _host.SetWindow(WindowName, Render());
    }

    public bool OnSlashCommand(string input)
    {
        var t = input.Trim();
        if (!t.StartsWith("/spelltimer", StringComparison.OrdinalIgnoreCase)) return false;
        EnsureConfig();

        List<Spell> active, inactive;
        lock (_gate)
        {
            active   = _spells.Values.Where(s => s.Active).OrderBy(DurationSort).ToList();
            inactive = _spells.Values.Where(s => !s.Active).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        }

        _host.Echo("Active spells:");
        if (active.Count == 0) _host.Echo("  (none)");
        foreach (var s in active) _host.Echo("  " + Line(s));
        if (inactive.Count > 0)
        {
            _host.Echo("Inactive (recently seen):");
            foreach (var s in inactive) _host.Echo("  " + s.Name);
        }
        return true;   // swallow — it's a console command, not a game command
    }

    // ── parsing ─────────────────────────────────────────────────────────────────

    private void ApplySpellLine(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return;

        string name;
        var active   = true;
        var duration = 0;
        var charge   = 0;
        var fading   = false;

        var m = SpellLineRe.Match(text);
        if (m.Success)
        {
            name = m.Groups[1].Value.Trim();
            var inside = m.Groups[2].Value.Trim();

            switch (_config.RuleFor(name))
            {
                case SpellParseRule.Charge:
                    var pcCharge = PercentRe.Match(inside);
                    if (pcCharge.Success) charge = int.Parse(pcCharge.Groups[1].Value);
                    break;
                case SpellParseRule.Percent:
                    var pcDur = PercentRe.Match(inside);
                    if (pcDur.Success) duration = int.Parse(pcDur.Groups[1].Value);
                    break;
                default:   // Roisaen (and Slivers, which normally has no parens)
                    var rs = RoisaenRe.Match(inside);
                    if (rs.Success)
                        duration = int.Parse(rs.Groups[1].Value);
                    else if (inside.Equals("OM", StringComparison.OrdinalIgnoreCase)
                          || inside.Equals("Indefinite", StringComparison.OrdinalIgnoreCase))
                        duration = Indefinite;
                    else if (inside.Equals("Fading", StringComparison.OrdinalIgnoreCase))
                        fading = true;          // active, but about to expire (duration 0)
                    break;
            }
        }
        else if (text.EndsWith("small orbiting slivers of lunar magic", StringComparison.Ordinal))
        {
            // Moonblade slivers report as a count phrase, not a (duration).
            if      (text.StartsWith("Many", StringComparison.Ordinal)) duration = 2;
            else if (text.StartsWith("No",   StringComparison.Ordinal)) { duration = 0; active = false; }
            else                                                          duration = 1;
            name = "Moonblade Slivers";
        }
        else
        {
            return;   // not a line we recognise
        }

        var spell = Get(name);
        spell.Active   = active;
        spell.Duration = duration;
        spell.Charge   = charge;
        spell.Fading   = fading;

        _poppedThisRound.Add(name);
        Publish(spell);
        _dirty = true;
    }

    /// <summary>A <c>clearStream</c> closes the previous refresh: any known spell the
    /// latest refresh did NOT list has dropped off — mark it inactive.</summary>
    private void FinishRound()
    {
        foreach (var spell in _spells.Values)
        {
            if (_poppedThisRound.Contains(spell.Name)) continue;
            if (!spell.Active && spell.Duration == 0 && spell.Charge == 0) continue;  // already clear
            spell.Active = false; spell.Duration = 0; spell.Charge = 0; spell.Fading = false;
            Publish(spell);
            _dirty = true;   // a spell dropped off → repaint the panel
        }
        _poppedThisRound.Clear();
    }

    private Spell Get(string name)
    {
        lock (_gate)
        {
            if (!_spells.TryGetValue(name, out var s))
                _spells[name] = s = new Spell { Name = name };
            return s;
        }
    }

    private void Publish(Spell s)
    {
        var v = _config.VarToken(s.Name);
        Set($"SpellTimer.{v}.active",   s.Active ? "1" : "0");
        Set($"SpellTimer.{v}.duration", s.Duration.ToString());
        if (_config.RuleFor(s.Name) == SpellParseRule.Charge)
            Set($"SpellTimer.{v}.charge", s.Charge.ToString());
    }

    private void Set(string name, string value)
    {
        if (!_host.Globals.TryGetValue(name, out var cur) || !string.Equals(cur, value, StringComparison.Ordinal))
            _host.Globals[name] = value;
    }

    // ── rendering ───────────────────────────────────────────────────────────────

    private static int DurationSort(Spell s) =>
        s.Duration == Indefinite ? int.MaxValue : (s.Fading ? -1 : s.Duration);

    private string Line(Spell s)
    {
        var when = s.Fading                 ? "fading"
                 : s.Duration == Indefinite ? "indefinite"
                 : $"{s.Duration} roisaen";
        if (_config.RuleFor(s.Name) == SpellParseRule.Charge && s.Charge > 0)
            when = $"{s.Charge}% charged";
        return $"{s.Name,-22} {when}";
    }

    private string Render()
    {
        List<Spell> active;
        lock (_gate) active = _spells.Values.Where(s => s.Active).OrderBy(DurationSort).ToList();

        var sb = new StringBuilder();
        sb.Append("Active: ").Append(active.Count).Append('\n');
        sb.Append("──────────────────────────────────────\n");
        foreach (var s in active) sb.AppendLine(Line(s));
        if (active.Count == 0) sb.Append("(no active spells)");
        return sb.ToString().TrimEnd();
    }
}
