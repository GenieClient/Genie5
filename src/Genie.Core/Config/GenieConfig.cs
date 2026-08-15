using Genie.Core.Runtime;
using Genie.Core.Utility;
using Genie.Core.Parsing;

namespace Genie.Core.Config;

public sealed class GenieConfig
{
    private readonly LocalDirectoryService _localDirectory;

    public GenieConfig(LocalDirectoryService localDirectory)
    {
        _localDirectory = localDirectory;
    }

    public event Action<ConfigFieldUpdated>? ConfigChanged;

    public char ScriptChar { get; set; } = '.';
    public char SeparatorChar { get; set; } = ';';
    public char CommandChar { get; set; } = '#';
    public bool TriggerOnInput { get; set; } = true;

    /// <summary>Warn when a command sent to the game still contains an unexpanded
    /// <c>%var</c>/<c>$var</c> — an undefined variable that leaked through
    /// substitution and is going out literally (e.g. a script's
    /// <c>put go %offtransport</c> sent verbatim because <c>%offtransport</c> was
    /// never set, which DR answers with "What were you referring to?"). Default
    /// on; <c>#config warnrawvars false</c> silences it.</summary>
    public bool WarnRawVars { get; set; } = true;

    /// <summary>Genie 4 <c>mycommandchar</c> (Config.cs:17, default '/'):
    /// input starting with this char is echoed and run through the
    /// trigger/action pipeline (see <see cref="TriggerOnInput"/>) but NEVER
    /// sent to the game — "for trigger systems and such". Menu scripts that
    /// capture typed replies (mm_train's <c>~value</c> convention) pair with
    /// <c>#config mycommandchar ~</c> so the reply doesn't reach DR and draw
    /// a "Please rephrase that command."</summary>
    public char MyCommandChar { get; set; } = '/';

    /// <summary>Master enables for the five user rule engines (File ▸ Master
    /// Toggles / <c>#config triggers off</c> …). Rule sets stay loaded and
    /// editable while off — the engines just skip applying them. All default ON
    /// (Genie 4 parity).</summary>
    public bool EnableHighlights { get; set; } = true;
    /// <inheritdoc cref="EnableHighlights"/>
    public bool EnableTriggers { get; set; } = true;
    /// <inheritdoc cref="EnableHighlights"/>
    public bool EnableSubstitutes { get; set; } = true;
    /// <inheritdoc cref="EnableHighlights"/>
    public bool EnableGags { get; set; } = true;
    /// <inheritdoc cref="EnableHighlights"/>
    public bool EnableAliases { get; set; } = true;
    /// <summary>Game-window scrollback cap — how many rendered lines to keep
    /// before trimming the oldest. Genie 5 setting (the Genie 4 <c>maxrowbuffer</c>
    /// was a WinForms paint-batch knob with no Avalonia equivalent). Clamped to
    /// [100, 100000] on set.</summary>
    public int ScrollbackLines { get; set; } = 2000;
    /// <summary>Render the main Game window with AvaloniaEdit instead of the
    /// per-line ItemsControl. Experimental; default off. The legacy renderer
    /// stays the shipped path until parity is proven. Read ONCE, when the dock
    /// layout is built — swapping renderers under a populated buffer is not
    /// worth the complexity, so changing it needs a restart.</summary>
    public bool UseEditorGameWindow { get; set; }
    public bool ShowSpellTimer { get; set; } = true;
    /// <summary>Built-in Experience tracker ($Skill.* / $TDPs globals + "Experience"
    /// dock panel). Default on. Was the external Plugin_EXPTrackerV5, now in Core.</summary>
    public bool ShowExperience { get; set; } = true;
    /// <summary>Experience-window line density (Genie 4 EXPTracker parity). Higher =
    /// shorter line. 0 = Full (rank, %, learning word, count); 1 = drop the (n/34)
    /// count; 2 = numbers only (rank + %); 3 = short skill names + rank + %;
    /// 4 = Brief (short name + rank). Read by
    /// <see cref="Extensions.Builtin.ExperienceExtension"/> on render.</summary>
    public int ExperienceDensity { get; set; }
    /// <summary>Experience-window rank-gain tracking (Genie 4 EXPTracker parity, #144).
    /// When on, each learning row shows the ranks gained since the session started and
    /// the panel adds a session total. Default off. Read by
    /// <see cref="Extensions.Builtin.ExperienceExtension"/> on render.</summary>
    public bool ExperienceTrackGain { get; set; }
    /// <summary>Experience-window summary placement (Genie 4 EXPTracker parity). When on,
    /// the "Learning Skills: N / Locked / Session" summary drops to a footer beneath the
    /// skill list — the classic G4 EXPTracker look — instead of the G5 header up top.
    /// Default off (keep the current G5 layout). Read by
    /// <see cref="Extensions.Builtin.ExperienceExtension"/> on render.</summary>
    public bool ExperienceG4Layout { get; set; }
    /// <summary>Experience-window config bar (the Density slider / Track gain /
    /// G4 layout strip across the top of the panel). Default on. Pure UI —
    /// hides the controls to reclaim the row, the settings behind them still
    /// apply. Toggled from the Experience window's right-click menu.</summary>
    public bool ExperienceConfigBar { get; set; } = true;
    /// <summary>Built-in Time Tracker (Elanthian time / sky "Time Tracker" dock panel).
    /// Default on. Was the external Plugin_TimeTrackerV5, now in Core.</summary>
    public bool ShowTimeTracker { get; set; } = true;
    public bool AutoLog { get; set; } = true;
    public bool ClassicConnect { get; set; } = true;
    public string Editor { get; set; } = "notepad.exe";
    public string Prompt { get; set; } = "> ";
    public bool PromptBreak { get; set; } = true;
    public bool PromptForce { get; set; } = true;
    public bool Condensed { get; set; }
    /// <summary>Genie 4 ships this ignore list out of the box — dead creatures
    /// don't count toward <c>$monstercount</c>. The Mobs-panel editor's
    /// "Restore defaults" resets to this.</summary>
    public const string DefaultIgnoreMonsterList = "appears dead|(dead)";
    public string IgnoreMonsterList { get; set; } = DefaultIgnoreMonsterList;

    /// <summary>
    /// Split a pipe-joined regex (the <see cref="IgnoreMonsterList"/> format)
    /// on its TOP-LEVEL <c>|</c> alternatives only — a naive Split('|') would
    /// break apart groups like <c>(rat|hog)</c>. Tracks escapes, character
    /// classes, and group nesting; empty alternatives are dropped. The inverse
    /// of <c>string.Join("|", …)</c>, used by the Mobs-panel ignore-list
    /// editor to show one alternative per row.
    /// </summary>
    public static List<string> SplitTopLevelAlternatives(string pattern)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(pattern)) return parts;

        var current = new System.Text.StringBuilder();
        var depth   = 0;
        var inClass = false;
        var escaped = false;

        foreach (var ch in pattern)
        {
            if (escaped) { current.Append(ch); escaped = false; continue; }
            switch (ch)
            {
                case '\\':                                  current.Append(ch); escaped = true; break;
                case '[' when !inClass:                     current.Append(ch); inClass = true; break;
                case ']' when inClass:                      current.Append(ch); inClass = false; break;
                case '(' when !inClass:                     current.Append(ch); depth++; break;
                case ')' when !inClass && depth > 0:        current.Append(ch); depth--; break;
                case '|' when !inClass && depth == 0:
                    if (current.Length > 0) parts.Add(current.ToString());
                    current.Clear();
                    break;
                default:                                    current.Append(ch); break;
            }
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }
    public int ScriptTimeout { get; set; } = 5000;
    public int MaxGoSubDepth { get; set; } = 50;
    public bool Reconnect { get; set; } = true;
    public bool IgnoreCloseAlert { get; set; }
    public bool PlaySounds { get; set; } = true;
    public bool KeepInput { get; set; }
    public bool AbortDupeScript { get; set; } = true;
    /// <summary>Suppress non-fatal script-engine warnings (bad-condition / malformed-if
    /// advisories) — Genie 4 <c>ignorescriptwarnings</c> (#151). Default off (warnings
    /// shown). Hard errors and script-abort notices are always shown.</summary>
    public bool IgnoreScriptWarnings { get; set; }
    public bool ParseGameOnly { get; set; }

    /// <summary>Silently probe the DR <c>flags</c> verb once at connect and warn
    /// if any stream-affecting flag differs from the parser's verified baseline
    /// (issue #29). Default on; the probe is suppressed from display and skipped
    /// in Wizard/plain-text mode. Toggle with <c>#config flagscheck on|off</c>.</summary>
    public bool FlagsCheck { get; set; } = true;

    public bool AutoMapper { get; set; } = true;
    public int AutoMapperAlpha { get; set; } = 255;

    /// <summary>When <c>true</c> (default), <c>#goto</c> hands the walk to the
    /// community <c>automapper.cmd</c> script whenever one exists in the Scripts
    /// (or repo-scripts) directory — Genie 4 parity, so map special-move
    /// directives (<c>script ggbypass</c>, <c>ice nw</c>, …) and the script's
    /// pacing globals are honored. When the script is absent the built-in
    /// walker drives the path; <c>#config automapperscript false</c> forces the
    /// built-in walker always. Cross-zone gotos always use the built-in walker.</summary>
    public bool AutoMapperScript { get; set; } = true;

    /// <summary>When <c>true</c>, the connection sequence emits its granular
    /// per-step SGE protocol marks (<c>→K sent</c>, <c>←32-byte key</c>,
    /// <c>→auth</c>, TCP/TLS handshake timings…) into the game window. The
    /// high-level connect status lines show regardless. Default OFF — a normal
    /// login stays quiet; flip on to capture a full trace when diagnosing a
    /// connection stall (<c>#config conndebug true</c>). Complementary to
    /// <see cref="LichDebug"/> for Lich-proxy diagnosis.</summary>
    public bool ConnDebug { get; set; }

    /// <summary>Seconds of total server silence before the connection is declared
    /// dead (the app-level activity watchdog in <c>GameConnection</c>). DR's
    /// server never goes quiet for more than ~30s on a healthy link (prompt/vitals
    /// heartbeats), but it can end a session WITHOUT closing the TCP socket — the
    /// peer keeps ACKing keepalive probes while the game stream is dead, so TCP
    /// keepalive never fires and the client shows Connected (and <c>$connected</c>
    /// stays 1) indefinitely. Default 300 (5 min, &gt;10× the observed worst-case
    /// gap); clamped to 60–3600; <c>0</c> disables the watchdog entirely.
    /// <c>#config activitytimeout</c>.</summary>
    public int ActivityTimeout { get; set; } = 300;

    /// <summary>
    /// Optional safeguard for click-to-walk / <c>#goto</c> traversal: when
    /// <c>true</c>, an in-progress auto-walk pauses itself after the window
    /// has been unfocused for <see cref="AutoWalkUnfocusSeconds"/> seconds
    /// (the user clicks Resume to continue). <strong>Default OFF</strong> —
    /// DR's Scripting Policy asks players to be *responsive to the game*, not
    /// to keep the client window focused, so this is purely opt-in for users
    /// who want an extra idle backstop. Genie's job is to be a good frontend;
    /// staying within policy is the player's call.
    /// </summary>
    public bool AutoWalkPauseOnUnfocus { get; set; }

    /// <summary>Seconds of window-unfocus before <see cref="AutoWalkPauseOnUnfocus"/>
    /// pauses an active auto-walk. Minimum 60; only consulted when the toggle
    /// is on.</summary>
    public int AutoWalkUnfocusSeconds { get; set; } = 60;
    public double RoundTimeOffset { get; set; }

    /// <summary>
    /// Injuries auto-refresh (#18): seconds between silent <c>health</c> polls
    /// that refine the nervous-system reading (the injuries dialog's Nsys image
    /// can't say wound vs scar — only the <c>health</c> text can). 0 = off (the
    /// default; Genie never sends unprompted commands unless the user opts in).
    /// Non-zero values are floored at 10 s so a typo can't spam the server.
    /// Set from the Injuries panel's Auto-refresh picker or
    /// <c>#config injuriespoll N</c>. Polls additionally require the Injuries
    /// panel to be open (<c>GenieCore.InjuriesPanelVisible</c> gate) — a
    /// closed window has no reason to refresh.
    /// </summary>
    public int InjuriesPollSeconds { get; set; }
    /// <summary>Injuries panel layout: true = assembled body figure, false =
    /// the 4×4 part grid (default). Set from the panel's Figure-layout toggle
    /// or <c>#config injurieslayout figure|grid</c>.</summary>
    public bool InjuriesFigureLayout { get; set; }
    public bool ShowLinks { get; set; } = true;
    /// <summary>MonsterBold (#131): render DR's &lt;pushBold&gt; creature names /
    /// combat hits in bold + the <c>creatures</c> preset colour, in every window
    /// that shows them. On by default (Wrayth / Genie 3-4 parity).</summary>
    public bool MonsterBold { get; set; } = true;
    public bool ShowImages { get; set; } = true;
    public bool WebLinkSafety { get; set; } = true;
    public bool SizeInputToGame { get; set; }
    public bool UpdateMapperScripts { get; set; }
    /// <summary>Keep the main window above all other applications (Genie 4's
    /// <c>alwaysontop</c> key). The App binds <c>Window.Topmost</c> to its
    /// display.json copy of this flag (so it applies at launch, before the core
    /// exists) and keeps the two in sync: <c>#config alwaysontop on|off</c> /
    /// <c>#config load</c> update the window live via
    /// <see cref="ConfigFieldUpdated.AlwaysOnTop"/>, and the Layout-menu toggle
    /// writes back here. When the stores disagree at core build, display.json
    /// wins.</summary>
    public bool AlwaysOnTop { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public bool AutoUpdate { get; set; }
    public string ScriptExtension { get; set; } = "cmd";
    public string ConnectScript { get; set; } = string.Empty;

    /// <summary>
    /// Front-end identifier sent in the post-auth FE handshake (e.g.
    /// <c>FE:GENIE</c> or <c>FE:WRAYTH</c>). DR gates some features on this —
    /// server-side toggles like <c>EXPBRIEF</c> and richer <c>&lt;d cmd&gt;</c>
    /// click markup are only granted to clients identifying as Wrayth. For DR the
    /// value is <c>WRAYTH</c> (Genie 4's own DR handshake, Core/Game.cs:3218) —
    /// NOT <c>STORM</c>/<c>STORMFRONT</c>, which is the GemStone-side name and does
    /// NOT unlock DR's Wrayth gate (verified live 2026-08-01). Default matches
    /// Genie 4 (<c>GENIE</c>). Set via <c>#config frontend wrayth</c>, then reconnect.
    /// </summary>
    public string FrontEndIdentifier { get; set; } = "GENIE";

    // ── Lich auto-launch (#lc / #lconnect / #lichconnect) ────────────────────
    // Genie 4 parity: its #lc / #lichconnect starts the Lich Ruby proxy for you
    // (cmd → ruby → lich.rbw), waits, then connects. Genie 5's #lichconnect
    // historically only *attached* to an already-running Lich. These keys add
    // the "start Lich for me" half — opt-in and idempotent: with the toggle on,
    // a LichProxy connect launches Lich ONLY if the proxy port isn't already
    // listening (so a manually-started Lich is just attached to, unchanged).

    /// <summary>Master opt-in for auto-starting Lich on a <c>#lc</c> /
    /// <c>#lconnect</c> / <c>#lichconnect</c> connect. Default OFF — with it off,
    /// those verbs behave as before (attach to an already-running Lich). When on,
    /// the connect launches Lich (via <see cref="LichRubyPath"/> +
    /// <see cref="LichPath"/> + <see cref="LichArguments"/>) if nothing is already
    /// listening on the proxy host/port, waits up to <see cref="LichStartPause"/>
    /// seconds for it to come up, then connects. <c>#config lichautolaunch on</c>.</summary>
    public bool LichAutoLaunch { get; set; }

    /// <summary>Path to the Ruby executable used to run Lich. Empty = rely on
    /// <c>ruby</c> being on the system PATH (the common case on macOS/Linux).
    /// Only validated (must exist) when non-empty. <c>#config lichruby {path}</c>.</summary>
    public string LichRubyPath { get; set; } = string.Empty;

    /// <summary>Path to the Lich script to launch (e.g. <c>lich.rbw</c> on
    /// Windows, <c>lich.rb</c> elsewhere). Required for auto-launch. Genie 4's
    /// <c>LichPath</c>. <c>#config lichpath {path}</c>.</summary>
    public string LichPath { get; set; } = string.Empty;

    /// <summary>Extra arguments passed to Lich after the script path (e.g.
    /// <c>--login MyChar --without-frontend --detachable-client=8000</c>).
    /// Supports <c>{character}</c> and <c>{port}</c> placeholders, expanded at
    /// auto-launch from the Lich-proxy profile's Character field and proxy port
    /// (e.g. <c>--login {character} --dragonrealms --genie --headless {port}</c>).
    /// Genie 4's <c>LichArguments</c>. <c>#config lichargs {args}</c>.</summary>
    public string LichArguments { get; set; } = string.Empty;

    /// <summary>Maximum seconds to wait for the Lich proxy port to open after
    /// launching it, before giving up. Clamped 1–120. Genie 4's
    /// <c>LichStartPause</c> (a fixed sleep); Genie 5 polls the port and returns
    /// as soon as it's up, so this is an upper bound. <c>#config lichstartpause N</c>.</summary>
    public int LichStartPause { get; set; } = 8;

    /// <summary>When <c>true</c> and Genie auto-launched (owns) Lich, new lines
    /// from that session's <c>temp/debug-*.log</c> are mirrored into the game
    /// window with a <c>[lich-debug]</c> prefix for the whole owned session.
    /// Default OFF. Independent of <see cref="ConnDebug"/> — enable both for a
    /// full Lich-proxy connection diagnosis. <c>#config lichdebug true</c>.</summary>
    public bool LichDebug { get; set; }

    public string ScriptDirRaw { get; set; } = "Scripts";

    /// <summary>Where the Scripts updater pulls repo scripts to (issue #221).
    /// Empty (the default) = pull straight into <see cref="ScriptDir"/>, the
    /// pre-#221 behavior. When set, repo pulls land here instead and the script
    /// engine resolves names from <see cref="ScriptDir"/> first, then falls
    /// back to this folder — so a locally-edited copy in ScriptDir shadows the
    /// repo copy and survives updates. <c>#config reposcriptdir</c>.</summary>
    public string RepoScriptDirRaw { get; set; } = "";
    public string SoundDirRaw { get; set; } = "Sounds";
    public string TtsVoiceDirRaw { get; set; } = "Voices";
    public string ArtDirRaw { get; set; } = "Art";
    public string PluginDirRaw { get; set; } = "Plugins";
    public string MapDirRaw { get; set; } = "Maps";
    public string ConfigDirRaw { get; set; } = "Config";
    public string ProfileConfigDirRaw { get; set; } = "Config";
    public string LogDirRaw { get; set; } = "Logs";

    public string ScriptDir => _localDirectory.Current.ResolvePath(ScriptDirRaw);

    /// <summary>Resolved repo-scripts pull target — <see cref="ScriptDir"/>
    /// when <see cref="RepoScriptDirRaw"/> is blank (feature off), else the
    /// configured folder resolved the same way ScriptDir is.</summary>
    public string RepoScriptDir => string.IsNullOrWhiteSpace(RepoScriptDirRaw)
        ? ScriptDir
        : _localDirectory.Current.ResolvePath(RepoScriptDirRaw);
    public string SoundDir => _localDirectory.Current.ResolvePath(SoundDirRaw);
    /// <summary>Local dir holding sherpa-onnx Piper voice models for TTS
    /// (one subfolder per voice: <c>.onnx</c> + <c>tokens.txt</c> +
    /// <c>espeak-ng-data/</c>). Backs <c>#speak</c> and per-stream read-aloud.</summary>
    public string TtsVoiceDir => _localDirectory.Current.ResolvePath(TtsVoiceDirRaw);

    /// <summary>Selected TTS voice — the folder name under
    /// <see cref="TtsVoiceDir"/> (e.g. <c>vits-piper-en_US-lessac-medium</c>).
    /// Empty = use the first installed voice found. Set by <c>#tts use</c>.</summary>
    public string TtsVoice { get; set; } = "";

    /// <summary>Master switch for per-stream read-aloud (auto-speak game text).
    /// Off by default — opt-in. <c>#speak</c> works regardless. <c>#tts read</c>.</summary>
    public bool TtsRead { get; set; }

    /// <summary>Comma-separated streams read aloud when <see cref="TtsRead"/> is
    /// on (e.g. <c>whispers,talk,thoughts,death</c>). Default excludes floods
    /// (combat/atmospherics/logons) and the streams DR duplicates a line onto —
    /// <c>main</c> and <c>ooc</c> both carry a second copy of a whispers line
    /// (public #256), so reading either alongside <c>whispers</c> double-speaks.
    /// Edited by <c>#tts read/mute &lt;stream&gt;</c>.</summary>
    public string TtsReadStreamsRaw { get; set; } = "whispers,talk,thoughts,death";

    /// <summary>True when <paramref name="stream"/> should be read aloud now
    /// (master on AND stream in the allowlist).</summary>
    public bool TtsReadsStream(string stream) =>
        TtsRead && !string.IsNullOrEmpty(stream) &&
        TtsReadStreamsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => s.Equals(stream, StringComparison.OrdinalIgnoreCase));

    /// <summary>Per-stream read-aloud urgency overrides — CSV of
    /// <c>stream:low|normal|high</c> pairs (e.g. <c>talk:high,combat:low</c>).
    /// Empty = built-in defaults (whispers/death High; logons/atmospherics/
    /// familiar Low; everything else Normal). Malformed pairs are ignored,
    /// duplicates last-wins. Edited by <c>#tts priority</c>.</summary>
    public string TtsStreamPriorityRaw { get; set; } = "";

    /// <summary>Read-aloud urgency for <paramref name="stream"/> — the
    /// <see cref="TtsStreamPriorityRaw"/> override when present, else the
    /// built-in default map. Never throws on malformed input.</summary>
    public TtsUrgency TtsUrgencyFor(string stream)
    {
        string s = (stream ?? "").Trim().ToLowerInvariant();
        if (TtsStreamPriorityOverrides().TryGetValue(s, out var u)) return u;
        return TtsDefaultUrgencyFor(s);
    }

    /// <summary>The built-in urgency map with no overrides applied —
    /// whispers/death barge in; the chatty background streams stay Low;
    /// everything else Normal.</summary>
    public static TtsUrgency TtsDefaultUrgencyFor(string stream) =>
        (stream ?? "").Trim().ToLowerInvariant() switch
        {
            "whispers" or "death" => TtsUrgency.High,
            "logons" or "atmospherics" or "familiar" => TtsUrgency.Low,
            _ => TtsUrgency.Normal,
        };

    /// <summary>Parsed view of <see cref="TtsStreamPriorityRaw"/> — lowercased
    /// stream → urgency. Malformed pairs are skipped, duplicates last-wins.</summary>
    public IReadOnlyDictionary<string, TtsUrgency> TtsStreamPriorityOverrides()
    {
        var map = new Dictionary<string, TtsUrgency>(StringComparer.Ordinal);
        foreach (var pair in TtsStreamPriorityRaw.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = pair.IndexOf(':');
            if (i <= 0 || i == pair.Length - 1) continue;
            string name = pair[..i].Trim().ToLowerInvariant();
            TtsUrgency? u = pair[(i + 1)..].Trim().ToLowerInvariant() switch
            {
                "low" => TtsUrgency.Low,
                "normal" => TtsUrgency.Normal,
                "high" => TtsUrgency.High,
                _ => null,
            };
            if (name.Length > 0 && u is not null) map[name] = u.Value;
        }
        return map;
    }

    /// <summary>TTS speaking rate multiplier — 1.0 = the voice's natural pace,
    /// higher = faster. Clamped 0.5–3.0. <c>#tts rate</c>.</summary>
    public double TtsRate { get; set; } = 1.0;

    /// <summary>TTS output volume, 0–100 percent of the voice's full level
    /// (attenuation only, so it can never clip). <c>#tts volume</c>.</summary>
    public int TtsVolume { get; set; } = 100;
    /// <summary>Local cache dir for DR room/scene art (downloaded JPGs). Backs
    /// <c>showimages</c> / the Scene panel.</summary>
    public string ArtDir => _localDirectory.Current.ResolvePath(ArtDirRaw);

    /// <summary>
    /// Maps and Plugins are shared resources — community map data and installed
    /// plugin binaries — so they always resolve against the shared root, even
    /// when a profile uses its own per-profile data folder. This keeps one copy
    /// for every profile instead of an empty folder per override. See
    /// <see cref="Runtime.LocalDirectoryService.Shared"/>.
    /// </summary>
    public string MapDir => _localDirectory.Shared.ResolvePath(MapDirRaw);

    /// <inheritdoc cref="MapDir"/>
    public string PluginDir => _localDirectory.Shared.ResolvePath(PluginDirRaw);

    /// <summary>The shared data root itself (the Genie5 folder — per-user data dir
    /// or the portable dir), for files shared across characters and profiles, e.g.
    /// the Genie-4-compatible <c>InventoryView.xml</c> catalog.</summary>
    public string DataRoot => _localDirectory.Shared.ResolvePath("");

    public string ConfigDir => _localDirectory.Current.ResolvePath(ConfigDirRaw);
    public string ConfigProfileDir => _localDirectory.Current.ResolvePath(ProfileConfigDirRaw);

    // ── Analytics (local skill-history recording) ───────────────────────────

    /// <summary>Master switch for local skill-history recording (the Analytics
    /// window's data). On by default — local-only JSONL under
    /// <see cref="AnalyticsDir"/>, never uploaded. <c>#config analytics</c>.</summary>
    public bool Analytics { get; set; } = true;

    /// <summary>Seconds between skill-history snapshot flushes (clamped 10–600).
    /// <c>#config analyticsinterval</c>.</summary>
    public int AnalyticsInterval { get; set; } = 60;

    /// <summary>Days of raw snapshot history to keep before folding into daily
    /// rollups (0 = keep raw forever). <c>#config analyticsretentiondays</c>.</summary>
    public int AnalyticsRetentionDays { get; set; } = 90;

    /// <summary>Record DevReplay sessions too (their rows are marked replay and
    /// hidden from charts by default) — a development/testing aid, off by
    /// default because replay timestamps are fake. <c>#config analyticsreplay</c>.
    /// Read at connect time; toggle then reconnect.</summary>
    public bool AnalyticsReplay { get; set; }

    /// <summary>True once the one-time "skill history is being recorded"
    /// advisory has been acknowledged for this profile — the dialog never
    /// repeats after either Keep-enabled or Turn-off.</summary>
    public bool AnalyticsNoticeShown { get; set; }

    public string AnalyticsDirRaw { get; set; } = "Analytics";

    /// <summary>Root folder for skill-history data (one subfolder per
    /// character slug). <c>#config analyticsdir</c>.</summary>
    public string AnalyticsDir => _localDirectory.Current.ResolvePath(AnalyticsDirRaw);
    public string LogDir => _localDirectory.Current.ResolvePath(LogDirRaw);

    /// <summary>
    /// Switch <see cref="ConfigProfileDir"/> to a per-character path of the form
    /// <c>Profiles/{Char}-{Acct}/</c>. When either name is empty (LIST mode,
    /// replay sessions with no auth) this falls back silently to the legacy
    /// shared <c>Config</c> directory.
    ///
    /// On first use for a given character — i.e. the per-character directory
    /// holds none of the <see cref="CfgFiles"/> yet — any legacy
    /// <c>Config/*.cfg</c> files are copied into the new directory as a seed.
    /// (The trigger is cfg-file absence, not directory absence: the host UI
    /// may pre-create the folder for windows.json / Layouts before the
    /// character's first connect, and that must not defeat the seed.) This
    /// means the first character a user logs in as after upgrading inherits
    /// their old shared settings; subsequent characters start as copies of
    /// the same baseline. From there each character's rule sets diverge
    /// independently.
    ///
    /// Returns the resolved absolute path so callers can log it.
    /// </summary>
    public string ApplyCharacterProfile(string? characterName, string? accountName)
    {
        if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(accountName))
        {
            // Fall back to the shared Config dir — leaves ProfileConfigDirRaw
            // at its default of "Config" so non-character sessions still
            // round-trip with the historical layout.
            ProfileConfigDirRaw = ConfigDirRaw;
            return ConfigProfileDir;
        }

        var slug = CharacterSlug(characterName, accountName);
        var rel  = Path.Combine("Profiles", slug);
        ProfileConfigDirRaw = rel;

        var full = ConfigProfileDir;
        Directory.CreateDirectory(full);
        // One-time seed from legacy Config/*.cfg so the user's existing
        // aliases / classes / etc. carry into their first character. "First
        // time" = no cfg file in the dir yet; once any rule set is saved
        // per-character the seed never runs again for this character.
        var legacy = ConfigDir;
        if (Directory.Exists(legacy) && !legacy.Equals(full, StringComparison.OrdinalIgnoreCase)
            && !CfgFiles.Any(n => File.Exists(Path.Combine(full, n))))
        {
            foreach (var name in CfgFiles)
            {
                var src = Path.Combine(legacy, name);
                var dst = Path.Combine(full, name);
                if (File.Exists(src) && !File.Exists(dst))
                {
                    try { File.Copy(src, dst); } catch { /* best-effort seed */ }
                }
            }
        }
        return full;
    }

    /// <summary>Filesystem-safe per-character folder slug — <c>{Char}-{Acct}</c>
    /// with illegal characters stripped. Shared by the <c>Profiles</c> config
    /// dirs and the <c>Analytics</c> history folders so one character maps to
    /// one identity everywhere.</summary>
    public static string CharacterSlug(string characterName, string accountName) =>
        $"{Sanitize(characterName)}-{Sanitize(accountName)}";

    /// <summary>
    /// Pure-path version of <see cref="ApplyCharacterProfile"/>: the absolute
    /// per-character config directory for <paramref name="dataRoot"/> —
    /// <c>{root}/Profiles/{Char}-{Acct}/</c>, falling back to the shared
    /// <c>{root}/Config/</c> when either name is missing (mirroring the
    /// engine's fallback for LIST / unauthenticated sessions). No side
    /// effects: nothing is created and <see cref="ProfileConfigDirRaw"/> is
    /// untouched. This is THE path contract between Core (which loads
    /// <c>*.cfg</c> from here at connect) and any host UI that persists
    /// per-character files — hosts must write where this points or their
    /// files are never read back. Assumes the default <c>Config</c> dir name;
    /// a <c>configdir</c> override in <c>settings.cfg</c> only shifts the
    /// no-character fallback, which hosts don't use for per-character data.
    /// </summary>
    public static string ProfileDirFor(string dataRoot, string? characterName, string? accountName)
    {
        var root = Path.GetFullPath(dataRoot);
        return string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(accountName)
            ? Path.Combine(root, "Config")
            : Path.Combine(root, "Profiles", CharacterSlug(characterName, accountName));
    }

    /// <summary>Files that are character-specific and should follow the profile dir.</summary>
    private static readonly string[] CfgFiles =
    {
        "classes.cfg", "aliases.cfg", "variables.cfg",
        "highlights.cfg", "triggers.cfg", "substitutes.cfg", "gags.cfg",
        "macros.cfg",
    };

    /// <summary>
    /// Strip filesystem-illegal characters from a character or account name.
    /// DR character/account names are practically always alphanumeric, but
    /// this guards against rogue input.
    /// </summary>
    private static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb  = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        return sb.ToString().Trim();
    }

    public bool Save(string fileName = "settings.cfg")
    {
        try
        {
            var path = Path.IsPathRooted(fileName) ? fileName : Path.Combine(ConfigDir, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var pairs = ToConfigPairs();
            var lines = new string[pairs.Count];
            for (var i = 0; i < pairs.Count; i++)
                lines[i] = $"#config {{{pairs[i].Key}}} {{{pairs[i].Value}}}";
            File.WriteAllLines(path, lines);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The complete settings.cfg key→value map as ordered (key, value) pairs.
    /// Single source of truth shared by <see cref="Save"/> (writes one
    /// <c>#config {key} {value}</c> line each) and <see cref="GetSetting"/>
    /// (reads a current value). Keys match the <see cref="SetSetting"/> cases.
    /// </summary>
    public IReadOnlyList<(string Key, string Value)> ToConfigPairs() => new (string, string)[]
    {
        ("alwaysontop", AlwaysOnTop.ToString()),
        ("classicconnect", ClassicConnect.ToString()),
        ("scriptchar", ScriptChar.ToString()),
        ("separatorchar", SeparatorChar.ToString()),
        ("commandchar", CommandChar.ToString()),
        ("mycommandchar", MyCommandChar.ToString()),
        ("triggeroninput", TriggerOnInput.ToString()),
        ("warnrawvars", WarnRawVars.ToString()),
        ("highlights", EnableHighlights.ToString()),
        ("triggers", EnableTriggers.ToString()),
        ("substitutes", EnableSubstitutes.ToString()),
        ("gags", EnableGags.ToString()),
        ("aliases", EnableAliases.ToString()),
        ("scrollbacklines", ScrollbackLines.ToString()),
        ("useeditorgamewindow", UseEditorGameWindow.ToString()),
        ("spelltimer", ShowSpellTimer.ToString()),
        ("showexperience", ShowExperience.ToString()),
        ("experiencedensity", ExperienceDensity.ToString()),
        ("experiencetrackgain", ExperienceTrackGain.ToString()),
        ("experienceg4layout", ExperienceG4Layout.ToString()),
        ("experienceconfigbar", ExperienceConfigBar.ToString()),
        ("showtimetracker", ShowTimeTracker.ToString()),
        ("autolog", AutoLog.ToString()),
        ("automapper", AutoMapper.ToString()),
        ("automapperalpha", AutoMapperAlpha.ToString()),
        ("automapperscript", AutoMapperScript.ToString()),
        ("conndebug", ConnDebug.ToString()),
        ("activitytimeout", ActivityTimeout.ToString()),
        ("editor", Editor),
        ("prompt", Prompt),
        ("promptbreak", PromptBreak.ToString()),
        ("promptforce", PromptForce.ToString()),
        ("condensed", Condensed.ToString()),
        ("monstercountignorelist", IgnoreMonsterList),
        ("scripttimeout", ScriptTimeout.ToString()),
        ("maxgosubdepth", MaxGoSubDepth.ToString()),
        ("roundtimeoffset", RoundTimeOffset.ToString()),
        ("injuriespoll", InjuriesPollSeconds.ToString()),
        ("injurieslayout", InjuriesFigureLayout ? "figure" : "grid"),
        ("scriptdir", ScriptDirRaw),
        ("reposcriptdir", RepoScriptDirRaw),
        ("sounddir", SoundDirRaw),
        ("ttsvoicedir", TtsVoiceDirRaw),
        ("ttsvoice", TtsVoice),
        ("ttsread", TtsRead.ToString()),
        ("ttsreadstreams", TtsReadStreamsRaw),
        ("ttsstreampriority", TtsStreamPriorityRaw),
        ("ttsrate", TtsRate.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("ttsvolume", TtsVolume.ToString()),
        ("analytics", Analytics.ToString()),
        ("analyticsinterval", AnalyticsInterval.ToString()),
        ("analyticsretentiondays", AnalyticsRetentionDays.ToString()),
        ("analyticsreplay", AnalyticsReplay.ToString()),
        ("analyticsnoticeshown", AnalyticsNoticeShown.ToString()),
        ("analyticsdir", AnalyticsDirRaw),
        ("artdir", ArtDirRaw),
        ("mapdir", MapDirRaw),
        ("plugindir", PluginDirRaw),
        ("configdir", ConfigDirRaw),
        ("logdir", LogDirRaw),
        ("updatemapperscripts", UpdateMapperScripts.ToString()),
        ("reconnect", Reconnect.ToString()),
        ("ignoreclosealert", IgnoreCloseAlert.ToString()),
        ("keepinputtext", KeepInput.ToString()),
        ("sizeinputtogame", SizeInputToGame.ToString()),
        ("muted", (!PlaySounds).ToString()),
        ("abortdupescript", AbortDupeScript.ToString()),
        ("ignorescriptwarnings", IgnoreScriptWarnings.ToString()),
        ("parsegameonly", ParseGameOnly.ToString()),
        ("flagscheck", FlagsCheck.ToString()),
        ("autowalkpauseonunfocus", AutoWalkPauseOnUnfocus.ToString()),
        ("autowalkunfocusseconds", AutoWalkUnfocusSeconds.ToString()),
        ("showlinks", ShowLinks.ToString()),
        ("monsterbold", MonsterBold.ToString()),
        ("showimages", ShowImages.ToString()),
        ("weblinksafety", WebLinkSafety.ToString()),
        ("connectscript", ConnectScript),
        ("lichautolaunch", LichAutoLaunch.ToString()),
        ("lichruby", LichRubyPath),
        ("lichpath", LichPath),
        ("lichargs", LichArguments),
        ("lichstartpause", LichStartPause.ToString()),
        ("lichdebug", LichDebug.ToString()),
        ("autoupdate", AutoUpdate.ToString()),
        ("checkforupdates", CheckForUpdates.ToString()),
        ("scriptextension", ScriptExtension),
        ("frontend", FrontEndIdentifier),
    };

    /// <summary>
    /// Alternate names <see cref="SetSetting"/> accepts for existing keys,
    /// mapped to the canonical settings.cfg key. <see cref="GetSetting"/>
    /// resolves through this so any name that can be set can also be read
    /// (<c>#config fe</c> ≡ <c>#config frontend</c>). Aliases stay out of
    /// <see cref="ToConfigPairs"/> — <see cref="Save"/> writes canonical
    /// keys only.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SettingAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fe"] = "frontend",
        };

    /// <summary>
    /// Current value of a settings.cfg key (case-insensitive), or <c>null</c>
    /// if the key isn't a recognized setting. The read counterpart of
    /// <see cref="SetSetting"/>; backs <c>#config {key}</c> get + script reads.
    /// Accepts the same <see cref="SettingAliases"/> as the setter.
    /// </summary>
    public string? GetSetting(string key)
    {
        if (SettingAliases.TryGetValue(key, out var canonical))
            key = canonical;
        foreach (var (k, v) in ToConfigPairs())
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    /// <summary>
    /// settings.cfg keys that persist + can be edited (Scripts tab / <c>#config</c>)
    /// but are <b>not yet acted on</b> by Genie 5 — the feature they configure
    /// isn't built. <c>#config list</c> and the Scripts tab flag these as
    /// "(reserved)" so users aren't misled. <b>Remove a key here when you wire
    /// its behaviour.</b>
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="key"/> is persisted/editable but not yet
    /// wired to any behaviour — see <see cref="ReservedKeys"/>.</summary>
    public static bool IsReserved(string key) => ReservedKeys.Contains(key);

    /// <summary>
    /// Display grouping for <c>#config list</c> — ordered category headers, each
    /// naming the settings.cfg keys that print under it (keys are alphabetised
    /// within a section by the listing). Keys are matched against
    /// <see cref="ToConfigPairs"/>; any key not named here falls into a trailing
    /// "Other" bucket, so a newly-added setting is never silently dropped from
    /// the listing. <b>When you add a key to <see cref="ToConfigPairs"/>, add it
    /// to the right section here too.</b> The order of the sections is the order
    /// they print in.
    /// </summary>
    public static readonly IReadOnlyList<(string Category, string[] Keys)> ConfigCategories = new (string, string[])[]
    {
        ("Connection",       new[] { "activitytimeout", "classicconnect", "conndebug", "connectscript", "flagscheck", "frontend", "reconnect" }),
        ("Lich",             new[] { "lichautolaunch", "lichruby", "lichpath", "lichargs", "lichstartpause", "lichdebug" }),
        ("Window / Input",   new[] { "alwaysontop", "ignoreclosealert", "keepinputtext", "sizeinputtogame", "scrollbacklines", "useeditorgamewindow" }),
        ("Display / Parser", new[] { "spelltimer", "showexperience", "experiencedensity", "experiencetrackgain", "experienceg4layout", "experienceconfigbar", "showtimetracker", "prompt", "promptbreak", "promptforce", "condensed", "monstercountignorelist", "monsterbold", "parsegameonly", "roundtimeoffset", "showlinks", "showimages", "weblinksafety", "injuriespoll", "injurieslayout" }),
        ("Master Toggles",   new[] { "highlights", "triggers", "substitutes", "gags", "aliases" }),
        ("Scripting",        new[] { "scriptchar", "separatorchar", "commandchar", "mycommandchar", "triggeroninput", "warnrawvars", "scripttimeout", "maxgosubdepth", "abortdupescript", "ignorescriptwarnings", "scriptextension", "editor" }),
        ("Mapper",           new[] { "automapper", "automapperalpha", "automapperscript", "updatemapperscripts" }),
        ("Auto-Walk",        new[] { "autowalkpauseonunfocus", "autowalkunfocusseconds" }),
        ("Sound / TTS",      new[] { "muted", "ttsvoice", "ttsvoicedir", "ttsread", "ttsreadstreams", "ttsstreampriority", "ttsrate", "ttsvolume" }),
        ("Logging",          new[] { "autolog" }),
        ("Analytics",        new[] { "analytics", "analyticsinterval", "analyticsretentiondays", "analyticsreplay", "analyticsnoticeshown", "analyticsdir" }),
        ("Updates",          new[] { "autoupdate", "checkforupdates" }),
        ("Directories",      new[] { "scriptdir", "reposcriptdir", "sounddir", "artdir", "mapdir", "plugindir", "configdir", "logdir" }),
    };

    public bool Load(string fileName = "settings.cfg")
    {
        var path = Path.IsPathRooted(fileName) ? fileName : Path.Combine(ConfigDir, fileName);
        if (!File.Exists(path)) return false;
        foreach (var line in File.ReadLines(path))
        {
            var parts = ArgumentParser.ParseArgs(line);
            if (parts.Count == 3)
                SetSetting(parts[1], parts[2], showException: false);
        }
        return true;
    }

    public IReadOnlyList<string> SetSetting(string key, string value = "", bool showException = true)
    {
        var messages = new List<string>();
        try
        {
            switch (key.ToLowerInvariant())
            {
                case "scriptchar": ScriptChar = FirstCharOrDefault(value, ScriptChar); break;
                case "separatorchar": SeparatorChar = FirstCharOrDefault(value, SeparatorChar); break;
                case "commandchar": CommandChar = FirstCharOrDefault(value, CommandChar); break;
                case "mycommandchar": MyCommandChar = FirstCharOrDefault(value, MyCommandChar); break;
                case "triggeroninput": TriggerOnInput = ToBool(value); break;
                case "warnrawvars": WarnRawVars = ToBool(value); break;
                case "highlights": EnableHighlights = ToBool(value); Notify(ConfigFieldUpdated.MasterToggles); break;
                case "triggers": EnableTriggers = ToBool(value); Notify(ConfigFieldUpdated.MasterToggles); break;
                case "substitutes": EnableSubstitutes = ToBool(value); Notify(ConfigFieldUpdated.MasterToggles); break;
                case "gags": EnableGags = ToBool(value); Notify(ConfigFieldUpdated.MasterToggles); break;
                case "aliases": EnableAliases = ToBool(value); Notify(ConfigFieldUpdated.MasterToggles); break;
                case "scrollbacklines": ScrollbackLines = Math.Clamp(UtilityCore.StringToInteger(value), 100, 100000); break;
                case "useeditorgamewindow": UseEditorGameWindow = ToBool(value); break;
                case "spelltimer": ShowSpellTimer = ToBool(value); Notify(ConfigFieldUpdated.Trackers); break;
                case "showexperience": ShowExperience = ToBool(value); Notify(ConfigFieldUpdated.Trackers); break;
                case "experiencedensity": ExperienceDensity = Math.Clamp(UtilityCore.StringToInteger(value), 0, 4); Notify(ConfigFieldUpdated.Trackers); break;
                case "experiencetrackgain": ExperienceTrackGain = ToBool(value); Notify(ConfigFieldUpdated.Trackers); break;
                case "experienceg4layout": ExperienceG4Layout = ToBool(value); Notify(ConfigFieldUpdated.Trackers); break;
                case "experienceconfigbar": ExperienceConfigBar = ToBool(value); Notify(ConfigFieldUpdated.Trackers); break;
                case "showtimetracker": ShowTimeTracker = ToBool(value); Notify(ConfigFieldUpdated.Trackers); break;
                case "autolog": AutoLog = ToBool(value); Notify(ConfigFieldUpdated.Autolog); break;
                case "classicconnect": ClassicConnect = ToBool(value); Notify(ConfigFieldUpdated.ClassicConnect); break;
                case "editor": Editor = value; break;
                case "prompt": Prompt = NormalizePrompt(value); break;
                case "promptbreak": PromptBreak = ToBool(value); break;
                case "promptforce": PromptForce = ToBool(value); break;
                case "condensed": Condensed = ToBool(value); break;
                case "monstercountignorelist": IgnoreMonsterList = value; Notify(ConfigFieldUpdated.MonsterIgnore); break;
                case "scripttimeout": ScriptTimeout = (int)UtilityCore.StringToDouble(value); break;
                case "maxgosubdepth": MaxGoSubDepth = int.TryParse(value, out var mgd) ? mgd : MaxGoSubDepth; break;
                case "roundtimeoffset": RoundTimeOffset = UtilityCore.StringToDouble(value); break;
                case "injuriespoll":
                    // 0 (off) or ≥10 s — floor non-zero values so a typo like
                    // "1" can't hammer the server with health commands.
                    var ips = (int)UtilityCore.StringToDouble(value);
                    InjuriesPollSeconds = ips <= 0 ? 0 : Math.Max(10, ips);
                    break;
                case "injurieslayout":
                    // "figure" or "grid"; anything else falls back to grid.
                    InjuriesFigureLayout = value.Trim().Equals("figure", StringComparison.OrdinalIgnoreCase);
                    break;
                case "scriptdir": ScriptDirRaw = SetDir(value); break;
                // Blank is meaningful here (= pull into scriptdir, feature
                // off), so don't run it through SetDir's create-the-folder
                // validation like the always-set directory keys.
                case "reposcriptdir": RepoScriptDirRaw = string.IsNullOrWhiteSpace(value) ? "" : SetDir(value); break;
                case "sounddir": SoundDirRaw = SetDir(value); break;
                case "ttsvoicedir": TtsVoiceDirRaw = SetDir(value); break;
                case "ttsvoice": TtsVoice = value.Trim(); break;
                case "ttsread": TtsRead = ToBool(value); break;
                case "ttsreadstreams": TtsReadStreamsRaw = value.Trim(); break;
                case "ttsstreampriority": TtsStreamPriorityRaw = value.Trim().ToLowerInvariant(); break;
                case "ttsrate":
                    // Ignore garbage (StringToDouble returns -1) rather than
                    // stomping the current rate; clamp real values to sane speech.
                    var ttsRate = UtilityCore.StringToDouble(value);
                    if (ttsRate > 0) TtsRate = Math.Clamp(ttsRate, 0.5, 3.0);
                    break;
                case "ttsvolume":
                    var ttsVol = (int)UtilityCore.StringToDouble(value);
                    if (ttsVol >= 0) TtsVolume = Math.Clamp(ttsVol, 0, 100);
                    break;
                case "analytics": Analytics = ToBool(value); break;
                case "analyticsinterval":
                    var ai = (int)UtilityCore.StringToDouble(value);
                    if (ai > 0) AnalyticsInterval = Math.Clamp(ai, 10, 600);
                    break;
                case "analyticsretentiondays":
                    var ar = (int)UtilityCore.StringToDouble(value);
                    if (ar >= 0) AnalyticsRetentionDays = Math.Clamp(ar, 0, 3650);
                    break;
                case "analyticsreplay": AnalyticsReplay = ToBool(value); break;
                case "analyticsnoticeshown": AnalyticsNoticeShown = ToBool(value); break;
                case "analyticsdir": AnalyticsDirRaw = SetDir(value); break;
                case "artdir": ArtDirRaw = SetDir(value); break;
                case "mapdir": MapDirRaw = SetDir(value); break;
                case "plugindir": PluginDirRaw = SetDir(value); break;
                case "configdir": ConfigDirRaw = SetDir(value); break;
                case "logdir": LogDirRaw = SetDir(value); Notify(ConfigFieldUpdated.LogDir); break;
                case "reconnect": Reconnect = ToBool(value); Notify(ConfigFieldUpdated.Reconnect); break;
                case "ignoreclosealert": IgnoreCloseAlert = ToBool(value); break;
                case "muted": PlaySounds = !ToBool(value); Notify(ConfigFieldUpdated.Muted); break;
                case "keepinputtext": KeepInput = ToBool(value); Notify(ConfigFieldUpdated.KeepInput); break;
                case "abortdupescript": AbortDupeScript = ToBool(value); break;
                case "ignorescriptwarnings": IgnoreScriptWarnings = ToBool(value); break;
                case "parsegameonly": ParseGameOnly = ToBool(value); break;
                case "flagscheck": FlagsCheck = ToBool(value); break;
                case "automapper": AutoMapper = ToBool(value); Notify(ConfigFieldUpdated.AutoMapper); break;
                case "automapperalpha": AutoMapperAlpha = ClampAlpha(value); Notify(ConfigFieldUpdated.AutoMapper); break;
                case "automapperscript": AutoMapperScript = ToBool(value); break;
                case "conndebug": ConnDebug = ToBool(value); break;
                case "activitytimeout":
                    // 0 = watchdog off; anything else clamps to 60s–1h so a typo
                    // can't declare a healthy-but-quiet link dead (server heartbeat
                    // gaps up to ~30s are normal) or park detection out past useful.
                    var at = (int)UtilityCore.StringToDouble(value);
                    if (at >= 0) ActivityTimeout = at == 0 ? 0 : Math.Clamp(at, 60, 3600);
                    break;
                case "showlinks": ShowLinks = ToBool(value); break;
                case "monsterbold": MonsterBold = ToBool(value); break;
                case "showimages": ShowImages = ToBool(value); Notify(ConfigFieldUpdated.ImagesEnabled); break;
                case "sizeinputtogame": SizeInputToGame = ToBool(value); Notify(ConfigFieldUpdated.SizeInputToGame); break;
                case "updatemapperscripts": UpdateMapperScripts = ToBool(value); Notify(ConfigFieldUpdated.UpdateMapperScripts); break;
                case "alwaysontop": AlwaysOnTop = ToBool(value); Notify(ConfigFieldUpdated.AlwaysOnTop); break;
                case "weblinksafety": WebLinkSafety = ToBool(value); break;
                case "autowalkpauseonunfocus": AutoWalkPauseOnUnfocus = ToBool(value); break;
                case "autowalkunfocusseconds":
                    AutoWalkUnfocusSeconds = Math.Max(60, (int)UtilityCore.StringToDouble(value));
                    break;
                case "connectscript": ConnectScript = value; break;
                case "lichautolaunch": LichAutoLaunch = ToBool(value); break;
                case "lichruby": LichRubyPath = value.Trim(); break;
                case "lichpath": LichPath = value.Trim(); break;
                case "lichargs": LichArguments = value.Trim(); break;
                case "lichstartpause": LichStartPause = Math.Clamp((int)UtilityCore.StringToDouble(value), 1, 120); break;
                case "lichdebug": LichDebug = ToBool(value); Notify(ConfigFieldUpdated.LichDebug); break;
                case "autoupdate": AutoUpdate = ToBool(value); Notify(ConfigFieldUpdated.AutoUpdate); break;
                case "checkforupdates": CheckForUpdates = ToBool(value); Notify(ConfigFieldUpdated.CheckForUpdates); break;
                case "scriptextension": ScriptExtension = string.IsNullOrWhiteSpace(value) ? "cmd" : value; break;
                case "frontend":
                case "fe":
                    // Normalize to uppercase since the FE handshake string uses upper.
                    FrontEndIdentifier = string.IsNullOrWhiteSpace(value) ? "GENIE" : value.ToUpperInvariant();
                    break;
                default:
                    if (showException) throw new InvalidOperationException($"Config {key} was not recognized.");
                    break;
            }
            messages.Add($"Set {key.ToLowerInvariant()}: {value}");
            messages.Add("Don't forget to persist settings after edits.");
            return messages;
        }
        catch { if (showException) throw; return messages; }
    }

    private string SetDir(string value)
    {
        _localDirectory.ValidateDirectory(value);
        return value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void Notify(ConfigFieldUpdated field) => ConfigChanged?.Invoke(field);

    private static bool ToBool(string value) => value.ToLowerInvariant() switch
    {
        "on" or "true" or "1" => true,
        _ => false
    };

    private static char FirstCharOrDefault(string value, char defaultValue) =>
        string.IsNullOrEmpty(value) ? defaultValue : value[0];

    private static string NormalizePrompt(string value) => value.ToLowerInvariant() switch
    {
        "on" or "true" or "1" => "> ",
        "off" or "false" or "0" => string.Empty,
        _ => value
    };

    private static int ClampAlpha(string value)
    {
        if (!int.TryParse(value, out var parsed)) return 255;
        return Math.Clamp(parsed, 0, 255);
    }
}
