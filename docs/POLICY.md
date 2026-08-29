# DragonRealms policy compliance

Genie 5 is a third-party client for [DragonRealms](https://www.play.net/dr),
Simutronics' text-based MMO. This document captures the explicit boundaries
the project commits to staying within — both the absolutes ("never ship X")
and the design choices that keep automation on the responsive side of the
line.

If you're considering a feature that touches automation, AI, notifications,
or anything that runs while the user isn't actively at the keyboard, **read
this file first** and open an issue with the policy question if anything is
unclear.

## What the policy actually says

Simutronics permits third-party clients. DR's
[Scripting Policy](https://elanthipedia.play.net/Policy:Scripting_policy)
draws its line at being **responsive to the gaming environment** — you may
run scripts and automation as long as you remain attentive and able to react.
The community phrasing is *"responsive software is fine; agentive software is
not."*

A few things this policy does **not** say, and that Genie therefore does not
impose:

- It does **not** require the client window to stay focused, and it does
  **not** forbid running multiple characters. Multiplaying is a player-workflow
  question; the scripting policy's one condition is that if scripts or triggers
  drive multiple accounts *for benefit*, you stay responsive from each
  character's perspective. Genie plans to build **attended** multi-character
  support on exactly that line (see [ROADMAP.md](ROADMAP.md)); what stays off is
  *unattended* cross-character automation, covered by the hard nevers below.
- It is **not the client's job to enforce**. Staying within policy is the
  player's responsibility. Genie's job is to be a good frontend.

So Genie holds a small set of hard nevers about *its own* behavior — the
client should never act on its own while you're away — and keeps any feature
that would constrain *your* workflow strictly opt-in. Four hard nevers fall
out of "the client shouldn't play itself," plus one optional convenience and
one attendance-gated exception:

### 1. Auto-reconnect is attendance-gated

If the game socket drops unexpectedly, Genie 5 re-establishes the connection
**only for a session the user was actually driving** — at least one command
must have been typed since connecting. An untouched, unattended session must
never resurrect itself. Manual disconnects and `quit` never arm it; the retry
ladder is bounded (5 attempts — 2×8 s, then 3×30 s — then it stops and
surfaces the failure); and reconnecting restores only the socket — scripts and
walks never auto-resume across a reconnect.

`Reconnect` defaults to on (Genie 4 config-file parity); `#config reconnect
off` disables it entirely. Implementation:
`MainWindowViewModel.MaybeArmAutoReconnect` → `ScheduleReconnect` →
`CheckReconnect` (Genie 4 parity: `Game.cs CheckReconnect`); the attendance
gate is the "user input since connect" check, which echoes
"Auto-reconnect skipped — no commands were sent this session." when it holds.

Why this line: an auto-reconnect that resumes a multi-hour script after a
disconnect is the classic "playing while away" pattern. The attendance gate
plus the never-resume rule keeps reconnect a convenience for the player at the
keyboard, never an absence enabler.

### 2. No agentive AI control

The AI integration in `Genie.Core.AI.AiContextBuffer` reads the game stream
and produces text suggestions. It does **not** and **must not** call
`Commands.ProcessInput` directly or by any indirect path. AI responses are
displayed to the user, who decides what to type.

This is enforced as a release gate (G4) in `AiContextBuffer.cs` and any PR
that wires AI responses back into the command pipeline will be closed.

### 3. Auto-walk is attended — with an *optional* idle pause (off by default)

Click-to-walk and `#goto` are direct user intent: you click a room (or type
`#goto`), and the walker steps there under roundtime gating — *responsive* to
that intent, not a fire-and-forget command burst. Esc cancels the walk, any
typed command cancels it, and a disconnect cancels it (it never auto-resumes
across a reconnect — a fresh click is required).

For users who want an extra idle backstop, there's an **optional** pause that
suspends an active walk after the window has been unfocused for a configurable
interval (`AutoWalkService.OnWindowDeactivated`, gated on
`GenieConfig.AutoWalkPauseOnUnfocus`, minimum 60s). It is **off by default**:
DR policy is about responsiveness, not window focus, so the client does not
require focus to function. When enabled, the user clicks Resume to continue.

This is a convenience, not a compliance requirement — it exists because some
players asked for it, not because Genie decides how you're allowed to play.

### 4. No headless / daemon mode

Genie 5 requires an interactive Avalonia window. There is no `--headless`,
no `--service`, no command-line flag that starts the app without UI. This
is enforced architecturally — the app's startup path in `Program.cs` only
constructs an `AppBuilder` configured for desktop runtime.

### 5. No shipping other players' speech to external services

The `AiContextBuffer` is the only component that sends game-text content
off-machine, and it's:

- **Default OFF.** Opt-in checkbox in Settings with a one-page privacy
  disclosure (release gate G1).
- **Stripping other-player content before any external send** is a
  pre-release requirement (release gate G2): the buffer captures whisper
  / speech preset content and `pushStream` blocks from `talk`, `whispers`,
  `thoughts`, `familiar` — public-area player utterances that other
  players have not consented to having forwarded.
- **In-character advisor mode** stays feature-flagged off entirely until
  there's an explicit legal read on perpetual-license clauses around
  player content (release gate G3).

Recordings (`File → Record Session…`) write raw XML to a per-machine
folder under `%APPDATA%/Genie5/Logs/` and never leave the developer's
machine.

## What we DO ship

The line is automation that's responsive to user input, not automation
that replaces user input.

- **Click-to-walk** via the AutoMapper: a click is direct user intent;
  the step-by-step walker is *responsive* to that intent. Esc / typed
  commands / disconnect all cancel cleanly (and the separate opt-in
  unfocus backstop, §3 above, pauses with a Resume button).
- **`.cmd` scripts:** the script engine is faithful to Genie 4's
  interpreter. Scripts that worked in Genie 4 work here. Scripts you
  consciously start are responsive to your intent; scripts that auto-start
  on triggers fall under the same rules as Genie 4's trigger system.
- **Triggers, aliases, highlights, substitutes, gags, macros:** all
  carry over from Genie 4. These are pattern-driven *responses* to game
  text, not independent agents.
- **Cross-zone routing:** the Dijkstra pathfinder finds a route; the
  walker executes it step-by-step under the same compliance gates as
  single-zone walking. A boat-wait countdown is a UI hint, not an
  automation actor.

## If you're unsure about a feature

Open an issue with the `policy-question` label *before* writing the PR.
The pattern that's worked: describe what the user does, what the app
does in response, and whether the user is at the keyboard when each step
happens. If "the user isn't at the keyboard" appears anywhere in the
flow, the feature probably needs to be redesigned or dropped.

The `policy-questions` channel in our Discord is a faster, lower-friction
place to sanity-check an idea before formalizing it as an issue.
