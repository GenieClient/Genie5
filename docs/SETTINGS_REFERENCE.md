# Settings Reference (`settings.cfg`)

> Generated from [`src/Genie.Core/Config/GenieConfig.cs`](../src/Genie.Core/Config/GenieConfig.cs)
> (`ToConfigPairs` / `ConfigCategories` / `SetSetting`) on **2026-08-03**. When a
> key is added or its default changes in `GenieConfig`, regenerate this page.

Every key persists in `{ConfigDir}/settings.cfg` as a `#config {key} {value}`
line and can be read (`#config {key}`) or set (`#config {key} {value}`) at
runtime. Grouping below matches the `#config list` categories. Boolean keys
accept `on` / `true` / `1` for true; anything else is false.

`fe` is an accepted **set-alias** of `frontend` (both names hit the same
setting; only `frontend` appears in `settings.cfg` and `#config list`).

## Connection

| Key | Default | What it does |
| --- | --- | --- |
| `classicconnect` | `True` | Use the classic connect dialog flow. |
| `conndebug` | `False` | Emit granular per-step SGE connection trace marks (TCP/TLS timings, `→K sent`, `→auth`, …) into the game window while connecting. |
| `connectscript` | *(empty)* | Script to run automatically after connecting. |
| `frontend` | `GENIE` | Front-end identifier sent in the post-auth FE handshake (e.g. `GENIE`, `WRAYTH`); DR gates some features (EXPBRIEF, richer click markup) on Wrayth. Uppercased on set; alias: `fe`. |
| `reconnect` | `True` | Enable auto-reconnect after a dropped connection. |

## Lich

| Key | Default | What it does |
| --- | --- | --- |
| `lichautolaunch` | `False` | Opt-in: a `#lc` / `#lconnect` / `#lichconnect` connect launches Lich itself if nothing is listening on the proxy port yet. |
| `lichruby` | *(empty)* | Path to the Ruby executable used to run Lich; empty = rely on `ruby` on PATH. |
| `lichpath` | *(empty)* | Path to the Lich script to launch (e.g. `lich.rbw`); required for auto-launch. |
| `lichargs` | *(empty)* | Extra arguments passed to Lich; supports `{character}` and `{port}` placeholders. |
| `lichstartpause` | `8` | Max seconds to wait for the Lich proxy port to open after launching (clamped 1–120; returns early once the port is up). |
| `lichdebug` | `False` | Mirror an owned (auto-launched) Lich session's `temp/debug-*.log` lines into the game window with a `[lich-debug]` prefix. |

## Window / Input

| Key | Default | What it does |
| --- | --- | --- |
| `alwaysontop` | `False` | Keep the main window above all other applications. |
| `ignoreclosealert` | `False` | Skip the confirmation alert when closing the app. |
| `keepinputtext` | `False` | Keep the typed command in the input box after sending instead of clearing it. |
| `sizeinputtogame` | `False` | Size the input box to match the game window width. |
| `scrollbacklines` | `2000` | Game-window scrollback cap — rendered lines kept before trimming the oldest (clamped 100–100000). |
| `useeditorgamewindow` | `False` | Experimental: render the main Game window with AvaloniaEdit instead of the per-line ItemsControl. Read once at layout build — needs a restart. |

## Display / Parser

| Key | Default | What it does |
| --- | --- | --- |
| `spelltimer` | `True` | Show the spell prep timer. |
| `showexperience` | `True` | Enable the built-in Experience tracker (`$Skill.*` / `$TDPs` globals + the Experience dock panel). |
| `experiencedensity` | `0` | Experience-window line density, 0–4 (0 = Full … 4 = Brief; higher = shorter line). |
| `experiencetrackgain` | `False` | Experience window shows ranks gained this session per skill plus a session total. |
| `experienceg4layout` | `False` | Move the Experience summary to a footer beneath the skill list (classic Genie 4 EXPTracker look). |
| `showtimetracker` | `True` | Enable the built-in Time Tracker (Elanthian time / sky dock panel). |
| `prompt` | `> ` | Prompt text shown in the game window; `on`/`off` toggle between `> ` and empty. |
| `promptbreak` | `True` | Break the line at a prompt. |
| `promptforce` | `True` | Force a prompt display. |
| `condensed` | `False` | Condensed output mode. |
| `monstercountignorelist` | `appears dead\|(dead)` | Pipe-joined regex of creature text excluded from `$monstercount` (dead creatures don't count). |
| `parsegameonly` | `False` | Only parse game output (skip processing of non-game text). |
| `roundtimeoffset` | `0` | Seconds added to server roundtime to compensate for latency. |
| `showlinks` | `True` | Render clickable links in game text. |
| `showimages` | `True` | Show DR room/scene art (the Scene panel). |
| `weblinksafety` | `True` | Confirm before opening web links from game text. |

## Master Toggles

| Key | Default | What it does |
| --- | --- | --- |
| `highlights` | `True` | Master enable for the highlights rule engine (rules stay loaded while off). |
| `triggers` | `True` | Master enable for the triggers rule engine. |
| `substitutes` | `True` | Master enable for the substitutes rule engine. |
| `gags` | `True` | Master enable for the gags rule engine. |
| `aliases` | `True` | Master enable for the aliases rule engine. |

## Scripting

| Key | Default | What it does |
| --- | --- | --- |
| `scriptchar` | `.` | Prefix character that launches a script. |
| `separatorchar` | `;` | Character that separates chained commands on one line. |
| `commandchar` | `#` | Prefix character for client `#commands`. |
| `mycommandchar` | `/` | Input starting with this char is echoed and run through the trigger pipeline but never sent to the game (Genie 4 parity). |
| `triggeroninput` | `True` | Run typed input through the trigger/action pipeline. |
| `scripttimeout` | `5000` | Script match/wait timeout in milliseconds. |
| `maxgosubdepth` | `50` | Maximum `GOSUB` nesting depth in `.cmd` scripts. |
| `abortdupescript` | `True` | Starting a script aborts an already-running copy of the same script. |
| `ignorescriptwarnings` | `False` | Suppress non-fatal script-engine warnings (hard errors are always shown). |
| `scriptextension` | `cmd` | Default file extension for scripts. |
| `editor` | `notepad.exe` | External editor used to open scripts/logs. |

## Mapper

| Key | Default | What it does |
| --- | --- | --- |
| `automapper` | `True` | Enable the AutoMapper. |
| `automapperalpha` | `255` | Mapper window opacity, 0–255. |
| `updatemapperscripts` | `False` | Let mapper updates also update mapper support scripts. |

## Auto-Walk

| Key | Default | What it does |
| --- | --- | --- |
| `autowalkpauseonunfocus` | `False` | Opt-in safeguard: pause an in-progress auto-walk after the window has been unfocused for `autowalkunfocusseconds` (user clicks Resume to continue). |
| `autowalkunfocusseconds` | `60` | Seconds of window-unfocus before the pause fires (minimum 60; only used when the toggle is on). |

## Sound / TTS

| Key | Default | What it does |
| --- | --- | --- |
| `muted` | `False` | Mute all sound playback (the inverse of the internal PlaySounds flag). |
| `ttsvoice` | *(empty)* | Selected TTS voice — the folder name under `ttsvoicedir`; empty = first installed voice found. Set by `#tts use`. |
| `ttsvoicedir` | `Voices` | Local dir holding sherpa-onnx Piper voice models (backs `#speak` and per-stream read-aloud). |
| `ttsread` | `False` | Master switch for per-stream read-aloud (auto-speak game text); `#speak` works regardless. |
| `ttsreadstreams` | `whispers,talk,thoughts,death` | Comma-separated streams read aloud when `ttsread` is on. |
| `ttsstreampriority` | *(empty)* | Per-stream read-aloud urgency overrides — CSV of `stream:low\|normal\|high` pairs; empty = built-in defaults. |
| `ttsrate` | `1` | TTS speaking rate multiplier (1.0 = natural; clamped 0.5–3.0). |
| `ttsvolume` | `100` | TTS output volume, 0–100 percent (attenuation only). |

## Logging

| Key | Default | What it does |
| --- | --- | --- |
| `autolog` | `True` | Automatically log the session to the log directory. |

## Analytics

| Key | Default | What it does |
| --- | --- | --- |
| `analytics` | `True` | Master switch for local skill-history recording (Analytics window data; local-only JSONL, never uploaded). |
| `analyticsinterval` | `60` | Seconds between skill-history snapshot flushes (clamped 10–600). |
| `analyticsretentiondays` | `90` | Days of raw snapshot history kept before folding into daily rollups (0 = keep raw forever). |
| `analyticsreplay` | `False` | Also record DevReplay sessions (rows marked replay, hidden from charts by default). |
| `analyticsnoticeshown` | `False` | Internal flag: the one-time "skill history is being recorded" advisory has been acknowledged. |
| `analyticsdir` | `Analytics` | Root folder for skill-history data (one subfolder per character slug). |

## Updates

| Key | Default | What it does |
| --- | --- | --- |
| `autoupdate` | `False` | Automatically apply found updates. |
| `checkforupdates` | `True` | Check for updates at startup. |

## Directories

All directory keys are relative to the data root unless an absolute path is
given; `mapdir` and `plugindir` resolve against the **shared** root (one copy
across all profiles).

| Key | Default | What it does |
| --- | --- | --- |
| `scriptdir` | `Scripts` | Scripts folder. |
| `sounddir` | `Sounds` | Sound files folder. |
| `artdir` | `Art` | Local cache dir for DR room/scene art. |
| `mapdir` | `Maps` | Map data folder (shared across profiles). |
| `plugindir` | `Plugins` | Plugin DLLs folder (shared across profiles). |
| `configdir` | `Config` | Config folder (settings.cfg + shared `.cfg` files). |
| `logdir` | `Logs` | Session log folder. |

## Other

Keys not (yet) named in a `#config list` category — they print under the
trailing "Other" bucket.

| Key | Default | What it does |
| --- | --- | --- |
| `flagscheck` | `True` | Silently probe the DR `flags` verb once at connect and warn if a stream-affecting flag differs from the parser's verified baseline. |
| `injuriespoll` | `0` | Injuries auto-refresh: seconds between silent `health` polls while the Injuries panel is open. 0 = off; non-zero floored at 10. |
| `injurieslayout` | `grid` | Injuries panel layout: `figure` (assembled body) or `grid` (4×4 part grid). |
| `monsterbold` | `True` | Render DR's `<pushBold>` creature names / combat hits in bold + the `creatures` preset colour. |
