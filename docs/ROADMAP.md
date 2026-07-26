# Genie 5 Roadmap

This is the public roadmap — what's shipped in the current alpha, what's in
flight, what's planned, and what's deliberately deferred. The goal is to
keep contributors and alpha testers oriented on where the project is going
and where help is most welcome.

Items marked **🚧 In flight** have someone actively working on them. Items
marked **🗓 Planned** are scoped and ready for someone to pick up. Items
marked **💭 Considering** are open design questions where we'd welcome an
issue with ideas before code lands.

If you want to work on a 🗓 Planned item, open an issue first so we don't
end up with two parallel PRs.

## Alpha 1 — shipped

These work today, in the v5.0.0-alpha.5 build.

- **Connection layer** — SGE direct auth, Lich 5 proxy, dev-replay from
  recorded XML sessions
- **StormFront XML parser** with 22+ typed event records; handles all the
  edge cases documented in [SGE_PROTOCOL.md](SGE_PROTOCOL.md)
- **GameState engine** — live snapshot of room, vitals, hands, status,
  spell timer, stance
- **Script engine** — full Genie 4 `.cmd` compatibility:
  `#class`, `#alias`, `#var`, `#highlight`, `#trigger`, `#substitute`,
  `#gag`, `#macro` all with save/load + per-character `.cfg` persistence
- **JavaScript `.js` array scripts** — a Jint-based engine running in
  parallel to `.cmd`, dispatched by extension. `genie.*` bridge API
  (`put`/`send`, `waitFor`/`waitForRe`/`matchWait`, `pause`, timers,
  session/script vars) with memory + runaway-loop guards (#21)
- **Typed / scripted login** — `#connect` / `#reconnect` / `#lichconnect`
  (saved-profile, explicit, reconnect-last, and Lich variants; passwords
  masked in history), plus `--profile` / `--host` / `--port` / `--mode`
  command-line startup (#46)
- **Genie 4 settings importer** (File → Import from Genie 4…)
- **AutoMapper**
  - Click-to-walk with compliance gating
  - Skill-weighted Dijkstra pathfinding (the pathfinder filters out exits
    your character lacks ranks for)
  - Cross-zone routing via `ZoneConnections.xml` (baseline seed shipped;
    community-curated meta-graph)
  - Edit Exit dialog for per-arc skill / RT / wait / notes
- **Dockable panels** via Dock.Avalonia (3-column default layout)
- **Layout save/load** — named workspace presets (Layout menu → Save As…)
- **Script Bar** showing running scripts with stop/edit affordances
- **Tab-complete script names** in command bar
- **Session Recorder** (raw XML capture)
- **Per-tag visibility toggles** — Game / Echo / Script lines
- **Hands strip** with held items, prepared spell, status badges, stance
- **Vitals strip** with health / mana / stamina / spirit / concentration
- **Per-character profiles** with AES-256-GCM password encryption (machine-
  bound key)
- **Plugin system** — `Genie.Plugins.Abstractions` library (`IGeniePlugin`,
  `IPluginHost`, `IGameStateView`), `PluginManager` with per-plugin
  assembly-load-context isolation, `#plugin` command for load / unload,
  and `Plugin_EXPTrackerV5` as the first external plugin
- **Integrated updater** — in-process update system (architecturally
  distinct from Genie 4's external `Lamp.exe`). Updates dialog with
  Core / Maps / Plugins tabs and Help-menu badge; `CoreAppUpdater` via
  Velopack; pluggable `IFileListSource` / `IReleaseSource` (GitHub
  Contents + Releases); `MapsUpdater` (pulls from the community Maps
  repo) and `PluginUpdater` (pulls plugin DLLs from configured release
  feeds)
- **Code-signing pipeline (live)** — every tagged release submits the
  Windows binary to SignPath.io and, after per-release maintainer
  approval, ships it EV-signed under Shadow Realms LLC, the project's
  support partner (first signed release: v5.0.0-alpha.10; was #33).
  Installer (`Setup.exe`) signing is a planned follow-up
- **Multi-platform release artifacts** — every tagged release attaches
  Windows / macOS (Apple Silicon + Intel) / Linux builds plus the
  Velopack update feeds, built by `release.yml`

### Added since alpha.4

- **`#config` settings system** — `#config <key> <value>` / `<key>` /
  `list`, backed by `settings.cfg`, with ~20 Genie 4 settings wired
  (script recursion cap, abort-dup-script, triggers-on-input, per-profile
  connect script, external editor, roundtime offset, confirm-before-open
  web links, scrollback cap, …) plus a Configuration → Scripts tab
- **Game prompt rendering** — the `>` / `R>` / `H>` prompt now draws in the
  game window using your `prompt` string, with `promptbreak` (own-line vs
  suppressed) and `promptforce` (reconstruct the status letters from live
  indicators) honored
- **Scene panel** (`showimages`) — DR room/scene artwork, fetched from
  play.net's art CDN and shown in a dockable panel
- **Preset colouring** — room descriptions, whispers, speech and the rest
  of the preset palette render in their configured colours, with a
  Configuration → Presets editor
- **Sound** — SFX on triggers/highlights and a `#play` command
  (cross-platform: winmm / afplay / paplay)
- **Multi-level mapper view** — ghost rooms one level above/below
  (`automapperalpha`), and `#mapper reset` to re-resolve a lost location
- **Base scripting variables** — the full reserved vocabulary
  (`$health`, `$roomid`, `$zoneid`, status flags, hands, clock family, …);
  `#var` now lists the reserved/live-state set alongside user variables
- **Monster count** (`$monstercount` / `$monsterlist`), **AutoLog**
  (automatic rendered-text session log), **spell timer** (`$spelltime`),
  and **Condensed mode** (collapse blank lines)
- **Portable-first storage** — data resolves beside the executable
  (portable build) or in the per-user folder, with a first-run location
  prompt
- **Help → About dialog**

## In flight — 🚧

- **Discord community server** — channel layout designed, invite link
  pending publication
- **Mobile-routine workflow polish** — Discord webhook on PR open is
  designed but not wired (see [CONTRIBUTING.md](../CONTRIBUTING.md) for
  the mobile-routine pattern)

## Planned — 🗓

These are scoped well enough that someone could pick one up and ship it
without a deep architecture discussion first.

### macOS / Linux update channels

The integrated updater shipped on Windows via Velopack. The macOS and
Linux update channels are scoped but not yet wired:

- macOS: `~/Library/Application Support/Genie5/`
- Linux: XDG `$XDG_DATA_HOME/Genie5/` or `~/.local/share/Genie5/`

`Genie.Core.Runtime.AppPaths` already handles per-platform paths and the
`IUpdater` abstraction is platform-neutral — what's missing is a packaging
target for each OS (a `.app` bundle on macOS, an AppImage or similar on
Linux) and an `IReleaseSource` that knows how to pull the right artifact
for each platform.

### Themes / light mode

Currently single-themed dark UI. Avalonia 11 supports theme dictionaries;
the work is mostly designing the light palette and audited contrast levels.

> **Revisit here:** graphical status/posture icons (standing / kneeling /
> sitting / prone, plus stunned / webbed / bleeding / diseased / hidden /
> invisible / dead and a compass set) requested in Genie 4 issue
> [#76](https://github.com/GenieClient/Genie4/issues/76), where the
> community donated a ~24-icon pixel-art set (Thyon2377) plus an alternate
> posture set + `Icons-Sek.zip` (SekmehtDR). Deferred to the theming pass
> on purpose: several icons bake in a dark background that won't theme
> cleanly and will need re-cutting on transparency with a light-mode
> variant. Two gates before adopting: (1) confirm the original authors are
> OK shipping the assets under the repo licence and credit them; (2) cut
> theme-aware (transparent, light/dark) variants. Mapping is easy — posture
> ties to the existing stance/status state and the effect icons to status
> flags the parser already tracks.

### Visual trigger / flow designer

Pattern-and-action editor for `#trigger` rules that doesn't require typing
regexes by hand. Lower priority but a real onboarding helper.

## Considering — 💭

These need a design discussion before code lands. Open an issue with the
`design-question` label if you have thoughts.

- **AI-assisted advisor mode** — the AI surface is designed (see
  `AiContextBuffer.cs`) with five release gates (G1–G5). Gates G1, G2,
  G5 need product/UX work. Gate G3 (in-character advisor) stays disabled
  until there's an explicit ToS read.
- **Mudlet plugin / companion library** — `Genie.Core` is deliberately
  UI-free so it could be embedded in another client. Mudlet has a large
  cross-platform user base with no DR-XML support; a Genie.Core-backed
  plugin would bring DR compatibility to Mudlet. Design questions: API
  surface, packaging, who maintains.
- **Multi-character at-a-glance dashboard** — explicitly *not* multi-
  character orchestration (that's a hard never per DR policy); a passive
  read-only view of which of your characters are logged in elsewhere, if
  you can plumb it without violating policy.
- **Accessibility / text-to-speech** — text MUDs have a real blind /
  low-vision audience, and nothing in the client serves them yet. The
  parser already tags every line by stream (`main` / `talk` / `whispers`
  / `combat` / …), so this is mostly: screen-reader (Avalonia automation
  peer) labelling of the game window, vitals and hands strips, plus
  *selective* read-aloud per stream (speak whispers + combat, mute
  atmospherics). Inclusive and a clear differentiator vs. Genie 4 /
  Wrayth. Design questions: cross-platform TTS backend, per-stream voice
  config, performance under a busy combat stream.
- **Desktop notifications for unfocused events** — OS toast notifications
  when the window isn't focused (someone whispers you, you're attacked,
  you died, roundtime cleared). Policy-safe by construction — it *notifies*,
  it never *acts* (unlike auto-walk-while-unfocused, which is deferred).
  Complements the existing sound-on-trigger work and reuses the same
  stream/trigger classification. Design questions: which events default
  on, rate-limiting, cross-platform notification APIs.
- **Full-text search across scrollback + archived sessions** — the client
  already records sessions (Session Recorder + AutoLog); making that
  history searchable *in-app* ("find every time X whispered me", "all
  kills of Y") is mostly index + UI on data that's already on disk.
  Design questions: live-scrollback vs. on-disk-log scope, index format,
  redaction interplay with the analyst-capture policy.
- **Player-facing analytics dashboard** — `Plugin_EXPTrackerV5` captures a
  point-in-time view; persisting history and charting trends over time
  (XP/hour, kills/hour, skill-gain curves, deaths-by-creature) is the new
  part. Sticky for the hunting-optimization users who drive script usage.
  Design questions: storage/retention, whether this lives in core or as a
  first-party plugin.
- **Community content packaging + sharing** — extend the plugin
  signing/marketplace work beyond DLLs to a signed "Genie package"
  bundling scripts + triggers + highlights + map edits, with one-click
  import/export. The plugin-trust model is the security substrate; this is
  the distribution layer that grows the ecosystem (the Tirost script repo
  is a ready content source). Design questions: manifest format, trust/
  signing reuse, dependency handling between packages.
- **In-app script editor + debugger** — editing is currently delegated to
  an external editor. An integrated editor with syntax highlighting,
  breakpoints, and a live `$variable` / script-state inspector for both
  `.cmd` and `.js` would be a step beyond Genie 4 (which never had one);
  much of the runtime introspection already exists. Design questions:
  editor component choice, debugger model for the cooperative `.cmd`
  scheduler.
- **Lua scripting dialect** — directly serves the Mudlet-expansion thesis:
  Mudlet users' muscle memory is Lua, not the Wizard `.cmd` language.
  Offering Lua alongside `.cmd` / `.js` lowers migration cost in both
  directions. Large lift — flagged here as a design question, not scoped
  work. Design questions: which Lua runtime, host-API parity with the
  `genie.*` JS bridge, sandbox/runaway guards.
- **Cross-client config migration (Wrayth / Mudlet)** — the Genie 4
  importer widens the funnel; importing Wrayth/StormFront and Mudlet
  profiles would widen it further and reinforce "the client you switch
  *to*." Design questions: mapping each client's trigger/alias model onto
  Genie 5's, how much fidelity is achievable.

### Pre-scoped from the Genie 4 issue tracker

Sourced from open Genie 4 feature requests (June 2026 triage) that aren't
shipped or already planned in Genie 5. Listed here **pre-scoped** — each has
a clear shape and a Genie 4 issue for context, so they sit closer to
"ready to pick up" than the open design questions above. Most are small.
Credit the original Genie 4 reporters when implemented.

**Trigger / substitute quality**

- **Triggers ignore a leading timestamp when matching** — user-timestamped
  log lines still fire their triggers. Genie 4
  [#168](https://github.com/GenieClient/Genie4/issues/168).
- **Whole-word-only substitutes** — a per-rule toggle so `take` doesn't match
  inside `mistake`, without hand-writing `\b…\b`. Genie 4
  [#123](https://github.com/GenieClient/Genie4/issues/123).
- **Global variables in substitutes** — allow `$var` expansion in substitute
  replacement text. Genie 4
  [#91](https://github.com/GenieClient/Genie4/issues/91).

**Scripting vocabulary / routing**

- **`$scriptlistpaused` / `$scriptlistactive`** reserved vars, and `#script`
  operating on user-defined lists. Genie 4
  [#47](https://github.com/GenieClient/Genie4/issues/47). Builds on the
  reserved-variable vocabulary already shipped.
- **Route output to a named window** (`#shunt`-to-window) — send specific
  lines to a chosen dockable panel instead of the main game window; a natural
  fit for the Dock.Avalonia layout. Genie 4
  [#81](https://github.com/GenieClient/Genie4/issues/81).
- **Expose "Mapped exits:" as a parseable variable + toggle** for scripts.
  Genie 4 [#139](https://github.com/GenieClient/Genie4/issues/139).
- **Clickable URLs from chat + more `#link` uses** — extend the existing
  confirm-before-open web-link config to auto-linkify URLs in chat streams.
  Genie 4 [#99](https://github.com/GenieClient/Genie4/issues/99),
  [#45](https://github.com/GenieClient/Genie4/issues/45).

**UX**

- **Toggle: don't auto-select text** when scrolling up or switching windows.
  Genie 4 [#71](https://github.com/GenieClient/Genie4/issues/71).

**Script-engine compatibility regression tests** — port-fidelity guards, not
features:

- **`contains()` inside multi-variant evaluation** took the wrong branch in
  Genie 4 (root-caused in its `Script.Eval.ParseQueue`). Add a Genie 5 test
  asserting the evaluator handles it correctly. Genie 4
  [#145](https://github.com/GenieClient/Genie4/issues/145).
- **`unixtime` with `waiteval`** misbehaved in Genie 4 — add a Genie 5
  edge-case test. Genie 4
  [#179](https://github.com/GenieClient/Genie4/issues/179).

**Map format decision**

- **UTF-8 map files** (vs Genie 4's UTF-16 BOM LE), requested in Genie 4
  [#166](https://github.com/GenieClient/Genie4/issues/166). Tension with the
  "map format cannot change — 24 forks depend on it" constraint; likely
  resolution is **read both, write UTF-8** going forward. Needs a deliberate
  decision before any change.

> Graphical status/posture icons from Genie 4
> [#76](https://github.com/GenieClient/Genie4/issues/76) are tracked
> separately under **Themes / light mode** above.

> **Note — multi-session tabs.** A single client with per-character tabs is
> frequently requested (Genie 4 users ran multiple instances), but a single
> tool surfacing multiple characters sits on the DR-policy line between
> convenience and orchestration. It is intentionally *not* listed above:
> it needs an explicit policy read before it becomes a design question, not
> a feature we drift into. See the deferred "multi-character orchestration"
> entry below.

## Deferred — 🛑 not planned

- **Multi-character orchestration from one client instance** — DR policy
  doesn't allow one tool driving multiple characters.
- **Auto-reconnect with script resume** — see [POLICY.md](POLICY.md).
- **Headless / daemon mode** — see [POLICY.md](POLICY.md).
- **Auto-walk while window unfocused** — see [POLICY.md](POLICY.md).

## How this roadmap gets updated

Roadmap edits land via PRs same as code. If you're starting a 🗓 Planned
item, the same PR that adds the first commit should also move the item to
🚧 In flight. When it ships, move to the "shipped" list.
