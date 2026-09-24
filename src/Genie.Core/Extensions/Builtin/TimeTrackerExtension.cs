using System.Text;

namespace Genie.Core.Extensions.Builtin;

/// <summary>
/// Built-in Time Tracker. Surfaces the Elanthian date, moon/sun rise-set
/// countdowns, and the observed sky (conditions, Moon Mage influences) in the
/// "Time Tracker" dock panel — Genie 4-parity behavior:
///
/// <para><b>Computed, not polled.</b> Moon and sun state come from
/// <see cref="TimeCalc"/> (fixed cycles against the wall clock, exactly like
/// the Genie 4 plugin), refreshed by a 30-second heartbeat, so the panel is
/// always live without ever sending a command. DR's ambient moonrise/moonset
/// lines re-anchor the model as they scroll by (persisted per profile), and
/// each refresh publishes the Genie 4 <c>$Time.*</c> script globals
/// (<c>Time.isKatambaUp</c>, <c>Time.timeOfDay</c>, …) that community scripts
/// read. <c>obs sky</c> / <c>perceive</c> output is still parsed passively as
/// an observed overlay (cloud cover, influences). <c>/tt refresh</c> is the
/// one path that sends commands, and only when the user asks.</para>
/// </summary>
public sealed class TimeTrackerExtension : IGameExtension
{
    public string Name        => "TimeTracker";
    public string Version     => "2.1";
    public string Description => "Elanthian time, moon rise/set countdowns and sky in a dock panel.";

    private const string WindowName = "Time Tracker";
    private const int HeartbeatMs = 30_000;      // Genie 4 parity: refresh every 30 s
    private const long SoonSeconds = 360;        // Genie 4's red-warning threshold

    private readonly System.Threading.Lock _sync = new();
    private IExtensionHost _host = null!;
    private TimeTrackerOptions _opts = new();
    private TimeCalc _calc = new(0, 0, 0);
    private readonly SkyState _sky = new();
    private System.Threading.Timer? _heartbeat;
    private bool _optsLoaded;
    private bool _dirty = true;
    private string _lastRender = "";
    private long _lastDayLogged = -1;

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) _host?.SetWindow(WindowName, "(Time Tracker disabled)");
            else        { _lastRender = ""; _dirty = true; }
        }
    }

    public void Initialize(IExtensionHost host)
    {
        _host = host;
        _heartbeat = new System.Threading.Timer(_ => Heartbeat(), null, HeartbeatMs, HeartbeatMs);
    }

    public void Shutdown() => _heartbeat?.Dispose();

    public void OnCommandSent(string command) { }

    /// <summary>Per-character profile switch: re-read Time_Tracker.xml from the
    /// new profile dir on the next game line. The calc keeps its calibration —
    /// the moons are server-wide truth, not per-character.</summary>
    public void OnReset()
    {
        lock (_sync) _optsLoaded = false;
    }

    // Options (and the persisted calibration offsets) load lazily on first
    // in-game data: the host's ConfigDir isn't resolvable until after the
    // engine's Config is wired, which happens after Initialize. Callers hold _sync.
    private void EnsureOpts()
    {
        if (_optsLoaded) return;
        _optsLoaded = true;
        _opts = TimeTrackerOptions.Load(_host.ConfigDir);
        _calc = new TimeCalc(_opts.KatambaOffset, _opts.XibarOffset, _opts.YavashOffset);
    }

    private void Heartbeat()
    {
        // A timer-thread exception would take the whole process down.
        try
        {
            if (!_enabled) return;
            lock (_sync)
            {
                // Wait for the first game data (also loads options), and honour
                // UseGameTime=false ("repaint only on game activity").
                if (!_optsLoaded || !_opts.UseGameTime) return;
                RefreshNow(DateTime.UtcNow);
            }
        }
        catch { /* never let the heartbeat kill the app */ }
    }

    /// <summary>Recompute, publish the $Time.* globals, repaint. Callers hold
    /// <see cref="_sync"/> (internal seam for the heartbeat + tests).</summary>
    internal void RefreshNow(DateTime utcNow)
    {
        _calc.Recalculate(utcNow);
        PublishGlobals();
        LogDayRollover();
        Paint(utcNow);
    }

    public void OnGameLine(string line)
    {
        if (!_enabled || string.IsNullOrEmpty(line)) return;
        lock (_sync)
        {
            EnsureOpts();
            var hit = _sky.Feed(line, DateTimeOffset.UtcNow);
            hit |= TryCalibrate(line, DateTime.UtcNow);
            if (hit) _dirty = true;
        }
    }

    public void OnPrompt()
    {
        if (!_enabled) return;
        lock (_sync)
        {
            EnsureOpts();
            if (!_dirty) return;
            _dirty = false;
            RefreshNow(DateTime.UtcNow);
        }
    }

    // ── calibration ─────────────────────────────────────────────────────────────

    // DR's ambient sunrise/sunset lines (verbatim from the Genie 4 plugin). The
    // sun table is authoritative, so these only trigger a repaint.
    private static readonly string[] SunCues =
    {
        "heralding another fine day", "rises to create the new day",
        "as the sun rises, hidden", "as the sun rises behind it",
        "faintest hint of the rising sun", "The rising sun slowly",
        "The sun sinks below the horizon,", "night slowly drapes its starry banner",
        "sun slowly sinks behind the scattered clouds and vanishes",
        "grey light fades into a heavy mantle of black",
    };

    private bool TryCalibrate(string line, DateTime utcNow)
    {
        foreach (var cue in SunCues)
            if (line.Contains(cue, StringComparison.Ordinal))
                return true;

        foreach (var moon in SkyState.Moons)
        {
            var rose = line.Contains(moon + " slowly rises", StringComparison.Ordinal);
            var set  = !rose && line.Contains(moon + " sets", StringComparison.Ordinal)
                             && !line.Contains("moonbeam", StringComparison.Ordinal);
            if (!rose && !set) continue;

            var delta = _calc.Calibrate(moon, rose, utcNow);
            if (delta != 0)
            {
                _opts.KatambaOffset = _calc.KatambaOffsetTotal;
                _opts.XibarOffset   = _calc.XibarOffsetTotal;
                _opts.YavashOffset  = _calc.YavashOffsetTotal;
                _opts.Save(_host.ConfigDir);
                if (_opts.LogGameEvents)
                    _host.Log($"[TimeTracker] {moon} {(rose ? "moonrise" : "moonset")} shifted the model by {delta:+#;-#;0}s.");
            }
            return true;
        }
        return false;
    }

    // ── globals + logging ───────────────────────────────────────────────────────

    private void PublishGlobals()
    {
        var g = _host.Globals;
        g["Time.isKatambaUp"]    = _calc.KatambaIsUp ? "1" : "0";
        g["Time.isXibarUp"]      = _calc.XibarIsUp   ? "1" : "0";
        g["Time.isYavashUp"]     = _calc.YavashIsUp  ? "1" : "0";
        g["Time.isDay"]          = _calc.SunIsUp     ? "1" : "0";
        g["Time.timeOfDay"]      = _calc.TimeOfDay;
        g["Time.season"]         = _calc.Season;
        g["Time.katambaSeconds"] = _calc.KatambaCountdown.ToString();
        g["Time.xibarSeconds"]   = _calc.XibarCountdown.ToString();
        g["Time.yavashSeconds"]  = _calc.YavashCountdown.ToString();
        g["Time.sunSeconds"]     = _calc.SunCountdown.ToString();
    }

    private void LogDayRollover()
    {
        if (!_opts.LogGameEvents) return;
        var day = (long)_calc.Date.Year * 400 + _calc.Date.DaysSince;
        if (_lastDayLogged >= 0 && day > _lastDayLogged)
            _host.Log($"[TimeTracker] a new Elanthian day has dawned ({Ordinal(_calc.Date.Day)} of {TimeCalc.ShortMonth(_calc.MonthName)}).");
        _lastDayLogged = day;
    }

    // ── commands ────────────────────────────────────────────────────────────────

    public bool OnSlashCommand(string input)
    {
        var t = input.Trim();
        if (Is(t, "/tt") || Is(t, "/timetracker")) return TtCommand(Arg(t));
        if (Is(t, "/now"))      { lock (_sync) { EnsureOpts(); RefreshNow(DateTime.UtcNow); _host.Echo(_calc.DescriptiveText); } return true; }
        if (Is(t, "/timediff")) { TimeDiffCommand(Arg(t)); return true; }
        if (Is(t, "/time"))     { TimeCommand(Arg(t));     return true; }
        return false;

        static bool Is(string text, string cmd) =>
            text.Equals(cmd, StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith(cmd + " ", StringComparison.OrdinalIgnoreCase);

        static string Arg(string text)
        {
            var sp = text.IndexOf(' ');
            return sp < 0 ? "" : text[(sp + 1)..].Trim();
        }
    }

    private bool TtCommand(string arg)
    {
        lock (_sync) EnsureOpts();
        switch (arg.ToLowerInvariant())
        {
            case "refresh":
                // The one path that talks to the game — user-initiated only.
                _host.SendCommand("time");
                _host.SendCommand("obs sky");
                _host.Echo("[Time Tracker] requested time + obs sky.");
                break;
            case "help":
                _host.Echo("Time Tracker — /tt (repaint) · /tt refresh (poll time + obs sky) · /now (current Elanthian time)");
                _host.Echo("  /time YYYY-MM-DD HH:mm:ss [-long] — local → Elanthian · /time YYY-MM-DD [AA:RR] — Elanthian → local");
                _host.Echo("  /timediff <either format> — how far away that moment is");
                _host.Echo("  The panel refreshes itself every 30s and self-calibrates from moonrise/moonset messages.");
                break;
            default:
                lock (_sync)
                {
                    _lastRender = "";   // force a repaint even if nothing changed
                    RefreshNow(DateTime.UtcNow);
                }
                _host.Echo("[Time Tracker] window updated (Window → Time Tracker to show it).");
                break;
        }
        return true;
    }

    private void TimeCommand(string arg)
    {
        if (arg.Length == 0)
        {
            _host.Echo("/time YYYY-MM-DD HH:mm:ss [-long]  → Elanthian YYY-MM-DD AA:RR");
            _host.Echo("/time YYY-MM-DD [AA:RR]            → local date/time");
            return;
        }
        var longFmt = arg.EndsWith("-long", StringComparison.OrdinalIgnoreCase);
        if (longFmt) arg = arg[..^5].Trim();

        if (IsGregorian(arg))
        {
            if (!DateTime.TryParse(arg, out var local))
            {
                _host.Echo($"*** {arg} is not a valid local date/time.");
                return;
            }
            var calc = new TimeCalc(0, 0, 0);
            calc.Recalculate(local.ToUniversalTime());
            _host.Echo($"{arg} is {calc.Date} {calc.AnlasName}");
            if (longFmt) _host.Echo("This is " + calc.DescriptiveText);
        }
        else
        {
            try
            {
                var local = TimeCalc.LocalDateTimeFrom(DRDateTime.Parse(arg));
                _host.Echo($"{arg} is {local:yyyy-MM-dd HH:mm:ss}");
            }
            catch (FormatException)
            {
                _host.Echo($"*** {arg} is not a valid Elanthian date/time (YYY-MM-DD [AA:RR]).");
            }
        }
    }

    private void TimeDiffCommand(string arg)
    {
        if (arg.Length == 0)
        {
            _host.Echo("/timediff YYYY-MM-DD HH:mm:ss  → Elanthian time difference");
            _host.Echo("/timediff YYY-MM-DD [AA:RR]    → local time difference");
            return;
        }
        if (IsGregorian(arg))
        {
            if (!DateTime.TryParse(arg, out var local))
            {
                _host.Echo($"*** {arg} is not a valid local date/time.");
                return;
            }
            var target = local.ToUniversalTime();
            var span = TimeCalc.DurationBetween(target, DateTime.UtcNow).ToString();
            _host.Echo(span.Length == 0
                ? $"{arg} is right now"
                : $"{arg} {(target >= DateTime.UtcNow ? "is " + span + "from now." : "was " + span + "before now.")}");
        }
        else
        {
            try
            {
                var target = TimeCalc.LocalDateTimeFrom(DRDateTime.Parse(arg));
                var diff = (target - DateTime.Now).Duration();
                var span = (diff.Days    != 0 ? $"{diff.Days} days "       : "")
                         + (diff.Hours   != 0 ? $"{diff.Hours} hours "     : "")
                         + (diff.Minutes != 0 ? $"{diff.Minutes} minutes " : "");
                _host.Echo(span.Length == 0
                    ? $"{arg} is right now"
                    : $"{arg} is {span}{(target >= DateTime.Now ? "from now." : "before now.")}");
            }
            catch (FormatException)
            {
                _host.Echo($"*** {arg} is not a valid Elanthian date/time (YYY-MM-DD [AA:RR]).");
            }
        }
    }

    /// <summary>A 4-digit-year local date ("2026-07-05 …") vs an Elanthian
    /// 3-digit-year one ("447-06-34 …") — Genie 4 keyed off the dash position.</summary>
    private static bool IsGregorian(string arg) => arg.Length > 4 && arg[4] == '-';

    // ── rendering ───────────────────────────────────────────────────────────────

    private void Paint(DateTime utcNow)
    {
        var text = Render(utcNow);
        if (text == _lastRender) return;
        _lastRender = text;
        _host.SetWindow(WindowName, text);
    }

    private string Render(DateTime utcNow)
    {
        var sb = new StringBuilder();
        if (_opts.ShowElanthiaTime) RenderTime(sb);
        RenderMoons(sb);
        RenderSky(sb, utcNow);
        return sb.ToString().TrimEnd();
    }

    private void RenderTime(StringBuilder sb)
    {
        var d = _calc.Date;
        var month = _opts.ShowLongNames ? _calc.MonthName : TimeCalc.ShortMonth(_calc.MonthName);

        sb.Append("Elanthian Time\n");
        sb.Append("──────────────────────────────────────\n");
        sb.Append($" {Ordinal(d.Day)} of {month}\n");
        sb.Append($" {_calc.YearName}");
        if (_opts.IncludeTimeOfDay)
            sb.Append($"  ({_calc.Season}, {_calc.TimeOfDay})");
        sb.Append('\n');
        sb.Append($" {d.Year} years, {d.DaysSince} days since the Victory of Lanival\n");
        if (_opts.IncludeAnlasName)
            sb.Append($" ~{30 - d.Rois} roisaen before the Anlas of {_calc.NextAnlasName}\n");
        sb.Append($" {d}\n");
        sb.Append('\n');
    }

    private void RenderMoons(StringBuilder sb)
    {
        sb.Append("Moons\n");
        sb.Append("──────────────────────────────────────\n");
        MoonRow(sb, "Katamba", _calc.KatambaIsUp, _calc.KatambaCountdown);
        MoonRow(sb, "Xibar",   _calc.XibarIsUp,   _calc.XibarCountdown);
        MoonRow(sb, "Yavash",  _calc.YavashIsUp,  _calc.YavashCountdown);
        MoonRow(sb, "Sun",     _calc.SunIsUp,     _calc.SunCountdown);

        if (_sky.InfluenceLine.Length > 0)
            sb.Append($"\n Influence: {_sky.InfluenceLine}\n");
        if (_sky.FavoredLine.Length > 0)
            sb.Append($" Favored:   {_sky.FavoredLine}\n");
        sb.Append('\n');
    }

    private void MoonRow(StringBuilder sb, string name, bool isUp, long countdown)
    {
        var span = TimeSpan.FromSeconds(countdown);
        var warn = countdown < SoonSeconds ? " !" : "";
        // Observed obs-sky overlay, when we have one (moons only).
        var obs = SkyState.Describe(_sky.MoonVisibility(name)) switch
        {
            "up (clear)"        => "  (clear)",
            "up (cloudy)"       => "  (cloudy)",
            "below the horizon" => "  (below horizon)",
            _                   => "",
        };
        sb.Append($" {name,-9} {(isUp ? "SET " : "RISE")} in {span.Hours:0}:{span.Minutes:00}{warn}{obs}\n");
    }

    private void RenderSky(StringBuilder sb, DateTime utcNow)
    {
        if (_sky.SkyCapturedAt is null) return;

        sb.Append("Sky\n");
        sb.Append("──────────────────────────────────────\n");
        if (_sky.Conditions.Length > 0)
            sb.Append($" {_sky.Conditions}\n");
        var up = _sky.BodiesUp();
        if (up > 0)
            sb.Append($" {up} heavenly bodies above the horizon\n");
        sb.Append($" (sky read {Ago(utcNow, _sky.SkyCapturedAt.Value.UtcDateTime)})\n");
    }

    private static string Ordinal(int n) => $"{n}{TimeCalc.Suffix(n)}";

    private static string Ago(DateTime now, DateTime then)
    {
        var s = (int)(now - then).TotalSeconds;
        if (s < 0) s = 0;
        return s < 90   ? $"{s}s ago"
             : s < 3600 ? $"{s / 60}m ago"
             :            $"{s / 3600}h ago";
    }
}
