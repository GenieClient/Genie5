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

## Where we are — v5.0.0-beta.9 "Nothing Lost"

Genie 5 is a working, cross-platform DragonRealms client in **beta**. The core
experience is feature-complete; beta is about soak, polish, and closing the
last parity gaps. Recent releases: **beta.5** moved the whole solution to
.NET 10; beta.5.1 was built almost entirely from player reports; **beta.6**
put every timer on the server's clock (#261), hardened the script engine
(#242), landed the mapper-tracking batch, and brought Alteration Buddy
in-house; **beta.7** makes scrolled-back reading hold still with honest copy
(#293, #298) and teaches the Experience window the classic EXPTracker options
(#272); and **beta.8** lands the server-dialog capture groundwork with the
`#dialogs` reporting flow (#156), layers per-character rules over the shared
global set (#257), fixes `#eval` composition in value position (#300), brings
SGE-over-TLS to Linux and macOS (#316), and blinks inactive tabs on activity;
**beta.8.2** gives every Configuration list panel a live search box and
teaches the automapper named-object hidden exits; **beta.8.3** moves the
whole game pipeline onto its own thread so a wedged script can no longer
freeze the client (#251), runs Genie 4's embedded `<% %>` JavaScript blocks
again (#322), and makes a plugin's `/commands` work from scripts, aliases and
triggers alike (#325, #326); and **beta.9** gives the dialogs DragonRealms has
always sent somewhere to appear — they render as real dockable panels built
from whatever controls the server sent (#156 Phase 1), and their stream text
stops leaking into the main window (#324). It also gives the room's contents
an Objects window (#329), lets dock groups hide their banner (#320), and
carries a batch of durability fixes: one bad line of game text can no longer
stop your output, an interrupted `profiles.json` write can no longer lock you
out, panels re-opened into a squeezed-out column appear again (#331),
long-running `.js` scripts stop hitting a phantom memory cap (#330), and
`waiteval` follows a variable that changes while it waits (#332).
Self-update is now **verified end-to-end on all three platforms** (#27 —
thanks @dylb0t for the macOS validation).

Highlights of what works today:

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
- **Runtime** — .NET 10, the current LTS (supported to November 2028). Shipped
  in beta.5; every download still bundles the runtime it needs.
- **Updater** — in-process update system (Core via Velopack, Maps + Plugins via
  GitHub feeds) with a beta channel. Self-update is **live and verified
  end-to-end on Windows, macOS, and Linux** (#27), including the AppImage
  self-replace leg and delta feeds on all four platform channels.
- **Code signing** — every tagged release is EV-signed under Shadow Realms LLC
  (the project's support partner) via SignPath, after per-release maintainer
  approval. First signed release: `v5.0.0-alpha.10`.
- **Release artifacts** — every tag attaches Windows / macOS (Apple Silicon +
  Intel) / Linux builds plus the Velopack update feeds.

For the full per-release history see [RELEASE_NOTES.md](../RELEASE_NOTES.md).

---

## Horizon 1 — Road to stable 5.0.0 🎯

The honest short list. Several parallel tracks; none strictly blocks the
others, but all should land before we cut a stable `5.0.0` off the beta channel.

**Rough order of attack.** The tracks are independent, but if you're looking for
where the next commit does the most good:

1. **Track A (security)** — the actual gate, and the one with the longest
   tail. (The P1 script-correctness bugs that used to lead this list — rule
   layering #257 and eval composition #300 — shipped in beta.8; roundtime on
   the server clock and script-engine thread safety shipped in beta.6.)
2. **The script validator**
   ([#239](https://github.com/GenieClient/Genie5/issues/239)) before the in-app
   editor (Track E). It's the cheaper half and a whole-corpus scan is a far
   better regression signal for the script engine than waiting for a specific
   line to execute.
3. **Track B** (server-driven dialogs) and **Track E** (editor) — the two
   multi-week builds. Track B's capture groundwork shipped in beta.8; player
   `#dialogs` reports during the soak feed the renderer design directly.
4. **Track C is done** — self-update verified end-to-end on all three
   platforms (#27).

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

### Track B — Server-driven dialog windows ✅ *renderer shipped in beta.9*

A generic renderer for the `<dialogData>` panels DR sends — bank, store, spell
prep, feats, character profile, TDP, and friends. The parser already captures
`dialogData`; the Injuries panel proves the pattern but was built bespoke. The
work is a generic `ServerDialogService` + a data-driven Avalonia view so any of
these panels renders without per-panel code. This was the **biggest remaining
Genie 4 parity gap.** Tracked as
[#156](https://github.com/GenieClient/Genie5/issues/156).

**Beta.8 shipped the capture half**: typed dialog events, a first-sighting
journal (`Logs/dialog_journal.xml`), and the `#dialogs list` / `#dialogs
report` flow that turns a sighting into a pre-filled GitHub issue. These
dialogs are situational (profile `/edit`, feat removal, spell choices), so
**player reports are how the renderer's fixture library gets built** — if you
see a `[dialogs] … captured` line in play, reporting it is direct roadmap
help.

**The renderer half shipped in beta.9.** A server
dialog opens as an ordinary panel you can dock, float, or send to its own
window, built from whatever controls DR sent rather than from per-panel code —
which was the whole point of the track. Its arrangement is derived from the
coordinates in the message instead of fixed pixels, so a dialog laid out for a
small fixed window reflows into the space your panel actually has; a control
type Genie doesn't know yet renders as a labelled placeholder rather than
vanishing, so gaps stay visible and reportable. Where each dialog lives is
remembered per character profile, asked once the first time it appears.

#156 stays open for the bespoke remainder — the aim timer, and injuries for
other players.

### Track C — macOS / Linux self-update ✅ *done*

**Verified end-to-end on all three platforms** and closed
([#27](https://github.com/GenieClient/Genie5/issues/27)). The Linux leg ran
the full loop on a deliberately minimal environment — AppImage launch, live
game session, delta update, self-replace in place, relaunch on the new
version with the data root intact — and the macOS legs were validated by
@dylb0t. Fallout fixed along the way: app-local ICU bundling for minimal
distros (#314, shipped in beta.8) and the one-cipher-suite TLS handshake on
Linux/macOS (#316, shipped in beta.8); per-distro first-run notes live on the
wiki.

### Track D — Beta soak

Burn down real-user reports on the beta builds before we call it stable. The
current set, sourced from player reports (the two P1 script-correctness bugs
that led this list — rule layering #257 and eval composition #300 — shipped
in beta.8):

- Astral Plane: seven missing Map999 conduit nodes, plus a pathfinder guard so
  the built-in walker never routes through `script X` arcs it can't execute
  ([#253](https://github.com/GenieClient/Genie5/issues/253)).

### Track E — In-app script editor

Editing is still delegated to an external editor (`#edit` opens the OS default;
the App's editor-host seam — `ICommandHost.EditScript` /
`GenieCore.EditScriptRequested` — is otherwise unwired). An in-app editor with
`.cmd` / `.js` syntax highlighting, wired into the existing Script Manager
panel, brings editing inside the client so users aren't round-tripping to
Notepad to touch a script. The pieces are already in place: AvaloniaEdit is a
dependency (it backs the opt-in editor Game window), the `#edit` command and
edit-request seam exist, and the Script Manager panel is shipped. Scope for 1.0
is the **editor**; breakpoints and the live `$variable` / script-state
**debugger** stay a post-1.0 differentiator (Horizon 3).

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

> The **.NET 10 runtime retarget** that sat here shipped in beta.5 — see
> "Where we are" above, and [NET10_UPGRADE.md](NET10_UPGRADE.md) for the
> reasoning and contributor impact.

### Genie 4 plugin catalogue — parity audit and ports

An audit of all 21 Genie 4 plugins, tracking for each one whether it's already
built into Genie 5, worth porting, or safe to retire. The umbrella and its
disposition table live in
[#271](https://github.com/GenieClient/Genie5/issues/271); the individual ports
are filed beneath it — Crutch
([#263](https://github.com/GenieClient/Genie5/issues/263), the Empath healing
console, and the largest of them), Combat Tracker
([#265](https://github.com/GenieClient/Genie5/issues/265)), Bank Tracker
([#266](https://github.com/GenieClient/Genie5/issues/266)), SpellInfo
([#267](https://github.com/GenieClient/Genie5/issues/267)), BestiaryQuery
([#269](https://github.com/GenieClient/Genie5/issues/269)), and per-window
logging ([#270](https://github.com/GenieClient/Genie5/issues/270)). ExpEcho
retired: its behaviour shipped built-in with the beta.7 Experience window.

Several carry a `design-question` label rather than a spec: the Genie 5 plugin
contract is deliberately UI-free, so a plugin whose whole value is a clickable
window needs a decision on built-in panel vs. plugin before code. **If you used
one of these in Genie 4, saying so on its issue is the most useful input we can
get** — a few are open questions about whether anyone still wants them at all.

Related: the classic ExpTracker sort/echo/rested options shipped built-in on
the Experience window in beta.7.

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

### Compatibility regression tests ✅ *both cases closed*

Port-fidelity guards, not features — an assert-based suite around the documented
parser / script-engine edge cases. Both listed cases are now closed, but they
closed in opposite ways, which is worth recording:

- `unixtime` with `waiteval` — Genie 4
  [#179](https://github.com/GenieClient/Genie4/issues/179). **Reproduced.** It
  was a real defect in Genie 5, and `$unixtime` turned out to be only the
  visible edge of it: `waiteval` stored its expression already
  variable-substituted, so *any* variable was frozen at the moment the wait
  armed. Fixed and tracked as
  [#332](https://github.com/GenieClient/Genie5/issues/332); the guard now stops
  it regressing.
- `contains()` inside multi-variant evaluation — Genie 4
  [#145](https://github.com/GenieClient/Genie4/issues/145). **Does not
  reproduce**, and structurally cannot. Genie 5's evaluator is recursive
  descent: `ParseIdentOrCall` consumes a call's argument list through a nested
  `ParseOr` and then requires its own `)`, so a call's closing parenthesis can
  never close the group around it. Genie 4 hit this because its flat-queue
  evaluator returned on `SectionEndType`. Covered by a guard so a future rewrite
  that abandons recursive descent doesn't quietly reintroduce it.

Both guards shipped in beta.9. The pattern is worth repeating: each one pins a
behaviour Genie 4 documented, so a future refactor that diverges fails a test
instead of arriving as a field report — and a case that *can't* happen is worth
pinning for the same reason. New port-fidelity cases belong here as they turn up.

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

### Accessibility — screen-reader support

Text MUDs have a real blind / low-vision audience. The text-to-speech half of
this already **shipped in alpha.7.5** — offline neural voices, `#speak` /
`#tts`, selective per-stream read-aloud (speak whispers + combat, mute
atmospherics), and per-rule Speak. What remains is the screen-reader half:
Avalonia automation-peer support and labelling of the game window, vitals, and
hands strips so assistive tech (NVDA, Narrator, VoiceOver) can navigate the
client itself. Inclusive, compliance-free, and a clear differentiator. A
strong first pick for Horizon 3.

### Avalonia 12 migration

The UI-framework major upgrade, decoupled from the .NET 10 runtime retarget
(Horizon 2) that lands first. Avalonia 12 targets .NET 10 and its headline
wins map directly onto this roadmap: a native Linux screen-reader backend
(AT-SPI2 — feeds the accessibility item above), Wayland groundwork, and
rendering performance. It's bigger than a version bump: the
`Avalonia.ReactiveUI` integration package is discontinued in 12, so the move
carries a swap to `ReactiveUI.Avalonia` plus a major ReactiveUI upgrade (and
retiring the Fody weaver for source generators). No deadline pressure — the
Avalonia 11.3 line still receives patch releases in parallel with 12.x.

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

### Script debugger — breakpoints + live state inspector

The in-app **editor** graduated to a 1.0 item (Horizon 1, Track E). The step
beyond Genie 4 that stays post-1.0 is the **debugger** layered on top of it:
breakpoints and a live `$variable` / script-state inspector for both `.cmd` and
`.js`. Much of the runtime introspection already exists.

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
