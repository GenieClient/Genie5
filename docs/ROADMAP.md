# Genie 5 Roadmap

This is the public roadmap — what's shipped in the current beta, what stands
between us and a stable `5.0.0`, and what's planned for after. The goal is to
keep contributors and testers oriented on where the project is going and where
help is most welcome.

Items are grouped into **horizons**, not hard dates:

- **Horizon 1 — Road to stable 5.0.0**: the short list that actually gates a
  stable release. This is where help moves the needle most.
- **Horizon 2 — Beta-window wins**: low-risk parity and quality-of-life items
  that can ship during the beta soak without an architecture debate.
- **Horizon 3 — Post-1.0 differentiators**: the features that make Genie 5 more
  than a faithful Genie 4 port. Scoped, but deliberately after stable.
- **Horizon 4 — Expansion bets**: the longer-arc "platform" ideas.

If you want to pick up an item, **open an issue first** so we don't end up with
two parallel PRs. Roadmap edits land via PR the same as code — when you start an
item, the same PR that adds the first commit should move it to the shipped list.

---

## Where we are — v5.0.0-beta.2

Genie 5 is a working, cross-platform DragonRealms client in **beta**. The core
experience is feature-complete; beta is about soak, polish, and closing the
last parity gaps. Highlights of what works today:

- **Connection** — SGE direct auth (TLS on 7910 by default, plaintext 7900
  fallback), Lich 5 proxy (with owned-Lich auto-launch and `#config lichdebug`
  diagnostics), and dev-replay from recorded XML sessions.
- **Parser** — stateful StormFront XML + bare-text parser with a large typed
  event vocabulary; handles the merge-seam, preset, hand-body, and stream-
  routing edge cases documented in [SGE_PROTOCOL.md](SGE_PROTOCOL.md) and
  [dr-xml-protocol.md](dr-xml-protocol.md).
- **Game state** — live snapshot of room, exits, vitals, hands, status, stance,
  spell timer, injuries, monster count, and creature status.
- **Script engine** — faithful Genie 4 `.cmd` compatibility (`#class`,
  `#alias`, `#var`, `#tvar`, `#highlight`, `#trigger`, `#action`, `#substitute`,
  `#gag`, `#macro`, `#preset`, `#names`, eval-triggers) with save/load and
  per-character `.cfg` persistence, plus a Script Manager panel.
- **JavaScript `.js` array scripts** — a Jint-based engine running beside the
  `.cmd` interpreter, with the `genie.*` bridge, `include foo.js` function
  libraries, and memory + runaway-loop guards.
- **AutoMapper** — click-to-walk with compliance gating, skill-weighted Dijkstra
  pathfinding, cross-zone routing, `#mapper` CLI parity, multi-level ghost
  rooms, and an AutoMapper Settings dialog.
- **UI** — Avalonia + Dock.Avalonia dockable/floating panels, named layout
  presets, per-window highlight scoping, right-click title-bar menus on floats,
  themes (light/dark base + per-stream colours), sound on triggers/highlights,
  scene artwork, monospace `<output>` rendering, and a first-class Inventory
  View window.
- **Plugins** — `Genie.Plugins.Abstractions` + `PluginManager` with per-plugin
  load-context isolation, `#plugin` load/unload, transform hooks
  (`OnGameText`/`OnEcho`/`OnInput`), and built-in extensions (EXP tracker,
  Inventory View, Spell Timer, Time Tracker, Circle Calc).
- **Updater** — in-process update system (Core via Velopack, Maps + Plugins via
  GitHub feeds) with a beta channel. **Windows self-update is live.**
- **Code signing** — every tagged release is EV-signed under Shadow Realms LLC
  (the project's support partner) via SignPath, after per-release maintainer
  approval. First signed release: `v5.0.0-alpha.10`.
- **Release artifacts** — every tag attaches Windows / macOS (Apple Silicon +
  Intel) / Linux builds plus the Velopack update feeds.

For the full per-release history see [RELEASE_NOTES.md](../RELEASE_NOTES.md).

---

## Horizon 1 — Road to stable 5.0.0 🎯

The honest short list. Three parallel tracks; none strictly blocks the others,
but all three should land before we cut a stable `5.0.0` off the beta channel.

### Track A — Security-hardening backlog 🔒

The beta security review produced a set of pre-public-release items that are
the real gate to a stable launch. Several are P2. Highlights:

- Plugin DLLs are loaded without signature/hash verification (RCE surface).
- Zip-slip path traversal from feed-controlled filenames in the Maps/Plugin
  updaters.
- Update download URLs are unvalidated (SSRF / no HTTPS + host pinning).
- Path traversal in `.cmd` include / script path resolution.
- Script-engine regexes can bypass the match-timeout (ReDoS).
- Stored-password key derivation uses a non-secret machine name — harden to a
  per-user PBKDF2/Argon2 derivation.
- Profile files with encrypted passwords are written without restrictive
  permissions.
- No dependency vulnerability scanning yet (Dependabot / `dotnet --vulnerable`).
- AI-pipeline redaction is documented but not yet enforced on the send path
  (must land before any AI surface ships — see Horizon 3).

This track is tracked privately during triage because some items are live
attack surface; the fixes ship in the open. **This is the single most important
thing standing between beta and a stable release.**

### Track B — Server-driven dialog windows

A generic renderer for the `<dialogData>` panels DR sends — bank, store, spell
prep, feats, character profile, TDP, and friends. The parser already captures
`dialogData`; the Injuries panel proves the pattern but was built bespoke. The
work is a generic `ServerDialogService` + a data-driven Avalonia view so any of
these panels renders without per-panel code. This is the **biggest remaining
Genie 4 parity gap.** Tracked as
[#156](https://github.com/GenieClient/Genie5/issues/156).

### Track C — macOS / Linux self-update

Cross-platform build artifacts already ship on every release, and
`Genie.Core.Runtime.AppPaths` already resolves per-platform data dirs
(`~/Library/Application Support`, XDG `~/.local/share`). What's missing is the
self-updater on those platforms — a packaging target (`.app` bundle / AppImage)
and an `IReleaseSource` that pulls the right per-platform artifact. Today mac
and Linux users install fresh builds manually. Tracked as
[#27](https://github.com/GenieClient/Genie5/issues/27).

### Track D — Beta soak

Burn down real-user reports on the beta builds before we call it stable:

- Cross-machine window/state consistency
  ([#203](https://github.com/GenieClient/Genie5/issues/203)).
- Hide the top bar on floating windows
  ([#181](https://github.com/GenieClient/Genie5/issues/181)).
- Nested-variable resolution edge case
  ([#180](https://github.com/GenieClient/Genie5/issues/180)).

Plus the installer (`Setup.exe`) signing follow-up (binary signing is already
live; the installer is next) and a docs cleanup pass (unsigned-build warnings,
SignPath setup notes).

---

## Parallel track — GemStone IV 🎲

*Runs alongside the horizons, not after them.* GemStone IV is the biggest growth
lever available and reuses almost the entire stack — the same **SGE** login and
the [Wrayth protocol](https://gswiki.play.net/Wrayth_protocol) the parser already
speaks — so it earns its own near-term track rather than a spot at the far
horizon.

**It does not gate stable 5.0** — DragonRealms ships first. Scoping starts as
stable comes into view, and the build proceeds *in parallel* with Horizons 2–3
rather than waiting behind them.

Why near-term and not "someday": the connection layer already speaks SGE with a
game code, and GemStone mirrors DR's instance shape — **Prime / Platinum /
Shattered / Test**, where Shattered is the Fallen analogue, so the
attendance-gated auto-reconnect carries straight over unchanged. What's genuinely new is
game-specific, and it phases cleanly:

1. **Connect** — game selection in the connect flow + GemStone's SGE game code;
   reach a live GemStone session over the existing transport.
2. **Parse** — a GemStone slice of the Wrayth parser: its own tags, streams, and
   the spell / experience / wounds systems that differ from DR. Decide
   share-vs-fork per subsystem.
3. **State** — a GemStone game-state model beside the DR one.
4. **Surface** — the panels that differ (GemStone vitals, spells, wounds).
5. **Instances** — Prime / Platinum / Shattered / Test parity.

Design questions: how much parser / state to share vs. fork, and script-community
fit (GemStone's scripting culture differs from DR's). Reference:
[play.net/gs4](https://www.play.net/gs4/play/).

---

## Horizon 2 — Beta-window wins 🔧

Low-risk items that can ship during the soak. Scoped well enough to pick up
without a deep architecture discussion.

### Multi-line regex matching (per-rule opt-in)

All four pattern engines (trigger / highlight / substitute / gag) match one line
at a time. Add a per-rule `MultiLine` flag that matches against a rolling buffer
of the last *N* lines. Genie 4 is also per-line, so this is a superset, not a
compat break. Tracked as
[#22](https://github.com/GenieClient/Genie5/issues/22).

### Trigger / substitute quality (Genie 4 parity)

Small, well-shaped items sourced from the Genie 4 tracker — credit the original
reporters when implemented:

- **Ignore a leading timestamp when matching** — user-timestamped log lines
  still fire their triggers. Genie 4
  [#168](https://github.com/GenieClient/Genie4/issues/168).
- **Whole-word-only substitutes** — a per-rule toggle so `take` doesn't match
  inside `mistake`. Genie 4
  [#123](https://github.com/GenieClient/Genie4/issues/123).
- **Global variables in substitute replacement text**. Genie 4
  [#91](https://github.com/GenieClient/Genie4/issues/91).
- **`$scriptlistpaused` / `$scriptlistactive`** reserved vars + `#script` over
  user-defined lists. Genie 4
  [#47](https://github.com/GenieClient/Genie4/issues/47).

### Route output to a named window (`#shunt`)

Send specific lines to a chosen dockable panel instead of the main game window
— a natural fit for the Dock layout. Genie 4
[#81](https://github.com/GenieClient/Genie4/issues/81).

### Compatibility regression tests

Port-fidelity guards, not features — stand up an assert-based test suite around
the documented parser / script-engine edge cases:

- `contains()` inside multi-variant evaluation. Genie 4
  [#145](https://github.com/GenieClient/Genie4/issues/145).
- `unixtime` with `waiteval`. Genie 4
  [#179](https://github.com/GenieClient/Genie4/issues/179).

### Map format decision

Genie 4 map files are UTF-16 BOM LE; UTF-8 was requested (Genie 4
[#166](https://github.com/GenieClient/Genie4/issues/166)). Tension with the
"map format cannot change — many forks depend on it" constraint; the likely
resolution is **read both, write UTF-8**. Needs a deliberate decision before any
change.

---

## Horizon 3 — Post-1.0 differentiators 🚀

The features that make Genie 5 more than a faithful port. All scoped; all
deliberately after stable. Ranked roughly by value-to-risk — items nearer the
top are cleaner wins.

Every AI-driven item is constrained by [POLICY.md](POLICY.md): the AI may
**suggest**, the user must **apply**; it must **never** drive
`Commands.ProcessInput`; and other players' speech is stripped before any
external send. The advisory/agentive line is a hard architectural wall.

### Accessibility / text-to-speech

Text MUDs have a real blind / low-vision audience and nothing in the client
serves them yet. The parser already tags every line by stream, so this is mostly
screen-reader (Avalonia automation-peer) labelling of the game window, vitals,
and hands strips, plus *selective* per-stream read-aloud (speak whispers +
combat, mute atmospherics). Inclusive, compliance-free, and a clear
differentiator. A strong first pick for Horizon 3.

### Plugin marketplace + signing / trust

Browse, install, and update plugins in-app, with a signed-package trust model as
the security substrate. Reference implementation is parked and milestoned for
v1.0. Depends on the plugin-signature work in Horizon 1 Track A.

### Hunting / combat analytics with history

The EXP tracker captures a point-in-time view; persisting history and charting
trends (XP/hour, kills/hour, skill-gain curves, deaths-by-creature) is the new
part. Sticky for the hunting-optimization users who drive script usage. Solo
analytics ship freely; any party/group analytics need explicit per-player
opt-in (other players' data).

### Community content packaging

Extend the signing/trust model beyond DLLs to a signed "Genie package" bundling
scripts + triggers + highlights + map edits, with one-click import/export. The
plugin-trust model is the substrate; this is the distribution layer that grows
the ecosystem.

### In-app script editor + debugger

Editing is currently delegated to an external editor. An integrated editor with
syntax highlighting, breakpoints, and a live `$variable` / script-state
inspector for both `.cmd` and `.js` would be a step beyond Genie 4. Much of the
runtime introspection already exists.

### Full-text search across archived sessions

In-session Find already exists. Making the on-disk session history (Session
Recorder + AutoLog) searchable in-app — "find every time X whispered me" — is
mostly index + UI over data that's already on disk.

### Desktop notifications for unfocused events

OS toast notifications when the window isn't focused (someone whispers you,
you're attacked, roundtime cleared). Policy-safe by construction — it *notifies*,
it never *acts*. Reuses the existing sound-on-trigger stream classification.

### AI-assisted advisor mode ⚠ compliance-gated

An advisory panel that surfaces suggestions (trigger/alias ideas from
highlighted text; "your buff expires in 90 seconds") the user reads and chooses
to apply. The Core AI foundation (`AiConfig`, `AiContextBuffer`) exists, but the
advisory/agentive wall and the redaction enforcement (Horizon 1 Track A) must
land **first**. Advisory-only ships behind a ToS read; the agentive path is a
hard never.

### Cloud sync / cross-device profiles ⚠ compliance-gated

Sync triggers / aliases / profiles / layouts / settings across machines. Local
passwords are already AES-256-GCM encrypted; the gap for sync is the
machine-bound key. The additive design is a user-passphrase KDF layer
(Argon2id / PBKDF2) on top, zero-knowledge — the passphrase never leaves the
device. The passphrase UX and sync server are the real lift, not the crypto.

---

## Horizon 4 — Expansion bets 🌍

Longer-arc ideas that treat `Genie.Core` as a platform, not just this app.
Design-first — open a `design-question` issue before code lands.

- **Multi-character support** — DragonRealms permits multiplaying as long as you
  stay responsive from each character's perspective (verified against the
  [Scripting Policy](https://elanthipedia.play.net/Policy:Scripting_policy)), so
  this is a *build*, not a never. The shape is **attended** multi-character
  sessions — e.g. per-character tabs in one window — that keep every character
  under the player's eye. Design questions: the session/tab model, per-character
  state isolation, and a UX that can never slide into unattended orchestration.
  The firm line stays: no *unattended* cross-character automation (see Deferred).
- **Mudlet companion plugin / library** — `Genie.Core` is deliberately UI-free
  so it can be embedded in another client. Mudlet has a large cross-platform
  user base with no DR-XML support; a Genie.Core-backed plugin would bring DR
  compatibility to Mudlet. Design questions: API surface, packaging, ownership.
- **Lua scripting dialect** — serves the Mudlet thesis directly (Mudlet muscle
  memory is Lua). Offer Lua alongside `.cmd` / `.js`. Large lift; design
  questions: runtime choice, host-API parity with the `genie.*` bridge, sandbox
  guards.
- **Cross-client config migration (Wrayth / Mudlet)** — the Genie 4 importer
  widens the funnel; importing Wrayth/StormFront and Mudlet profiles widens it
  further and reinforces "the client you switch *to*."
- **Mobile companion app** — notifications, market/chat alerts, remote
  *monitoring only*. Notifications-only is policy-safe; anything that lets an
  unattended device drive game actions is out.
- **iOS distribution** — TestFlight / AltStore / GPL-exception paths already
  scoped in [IPHONE_OPTIONS.md](IPHONE_OPTIONS.md).
- **Visual trigger / flow designer** — a node-graph editor for `#trigger` rules
  that doesn't require hand-writing regexes. A real onboarding helper.
- **Streamer mode** — overlay windows, transparent chat, OBS-friendly layouts.
- **Adaptive new-player mode** — guided setup, recommended plugins, tutorial
  overlays. Pure UX; critical for growth once the client is public-stable.

### Status / posture icon set (revisit at theming polish)

Graphical status/posture icons (standing / kneeling / sitting / prone, plus
stunned / webbed / bleeding / diseased / hidden / invisible / dead and a compass
set) were requested in Genie 4
[#76](https://github.com/GenieClient/Genie4/issues/76), where the community
donated a pixel-art set. Two gates before adopting: (1) confirm the original
authors are OK shipping the assets under the repo licence and credit them;
(2) cut theme-aware (transparent, light/dark) variants — several bake in a dark
background that won't theme cleanly. Mapping is easy — posture ties to the
existing stance/status state, effect icons to status flags the parser already
tracks.

---

## Deferred — 🛑 not planned

Guardrails, not backlog. These stay off the roadmap by policy — see
[POLICY.md](POLICY.md).

- **Unattended multi-character automation** — one input (or the client itself)
  driving several characters while you're not responsive to each. This is the
  bot / multi-box-automation line in DR's scripting policy. Note the split:
  *attended* multi-character support is a planned build (Horizon 4); it's the
  *unattended, unresponsive* orchestration that stays a never.
- **Headless / daemon mode.**
- **AI agentive mode** — an AI pressing verbs autonomously. The hard-never line
  behind the advisor wall.

> **Attendance-gated exception — auto-reconnect.** Auto-reconnect after a
> disconnect is *not* a blanket never: it ships **attendance-gated** — it only
> re-establishes a session the user was actively driving (≥1 command sent),
> never fires on manual disconnect or `quit`, retries a bounded 5 times, and
> never resumes scripts or walks across the reconnect. See
> [POLICY.md](POLICY.md) §1.

---

## How this roadmap gets updated

Roadmap edits land via PRs the same as code. If you're starting a Horizon 1–2
item, the same PR that adds the first commit should also note it here. When it
ships, move it up into the "Where we are" list.
