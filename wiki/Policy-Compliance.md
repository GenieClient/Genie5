# Policy Compliance

Genie 5 is third-party software for [DragonRealms](https://www.play.net/dr). DR's [Scripting Policy](https://elanthipedia.play.net/Policy:Scripting_policy) is the relevant rulebook. This page explains the line the project holds and why — useful whether you're a player wondering "is this okay?" or a contributor weighing a feature.

## The principle

Simutronics permits third-party clients but draws a hard line at software that **plays the game for you**. The community shorthand is:

> **Responsive software is fine; agentive software is not.**

The policy asks you to stay **responsive to the gaming environment**. Notably, it does **not** require the client window to stay focused — working in another window while you travel, or running multiple characters, is a player-workflow question, not a focus rule. And staying within policy is the *player's* responsibility, not something the client enforces. Genie's job is to be a good frontend that amplifies an attentive player.

## Four hard "nevers" (about Genie's own behavior)

Genie holds these about *its own* behavior — the client should never act on its own while you're away. They will not ship, and contributions that add them are declined:

1. **No unattended reconnection.** Auto-reconnect only re-establishes a session *you* were actively driving — it is skipped entirely if you sent no commands since connecting, never fires on a manual disconnect or `quit`, and gives up after a bounded handful of attempts. Reconnecting restores the *connection* only: scripts and walks never auto-resume across it. (An auto-reconnect that resumes a long script after a drop is the classic "playing while away" pattern — that is exactly what the attendance gate and never-resume rule exist to prevent.) Turn it off entirely with `#config reconnect off`.
2. **No agentive AI.** Any AI features are **advisor-only** — they surface text you read; they never drive game commands. See [AI Advisor](AI-Advisor).
3. **No headless / daemon mode.** Genie requires its visible window. There is no flag to run it without a UI.
4. **No shipping other players' speech to external services.** If the optional AI ever sends context to an external service, other players' whispers/speech/thoughts/tells are stripped first, and it's opt-in behind a disclosure.

### Optional: idle pause for auto-walk

Click-to-walk and `#goto` are attended by design — Esc, any typed command, or a disconnect cancels the walk, and it never auto-resumes across a reconnect. For players who want an extra idle backstop, there's an **optional** setting that pauses an active walk after the window has been unfocused for a configurable interval. It's **off by default** (DR policy is about responsiveness, not focus); when on, you click Resume to continue. This is a convenience, not a requirement the client imposes.

## What Genie 5 *does* ship

The line is automation that's *responsive to your input*, not automation that *replaces* it:

- **Click-to-walk / `#goto`** — a click or `#goto` is direct intent; the step-by-step walker is responsive to it, and Esc / typed commands / disconnect all interrupt it cleanly (plus the optional unfocus pause above). See [The Mapper](Mapper).
- **`.cmd` scripts** — faithful to Genie 4. Scripts you consciously start are responsive to your intent; trigger-started scripts follow the same rules Genie 4's trigger system always did. See [Scripting](Scripting).
- **Triggers, aliases, highlights, substitutes, gags, macros** — pattern-driven *responses* to game text, not independent agents. See [Configuration & Rules](Configuration).
- **Cross-zone routing** — the pathfinder finds a route; the walker executes it under the same attended-mode gates as single-zone walking, and a boat countdown is a UI hint, not an actor. See [Cross-Zone Travel](Cross-Zone-Travel).

## Recording & credentials

- **Session recording** is **off by default**, shows a visible 🔴 REC indicator when on, and writes only to your machine.
- **Passwords** are stored locally, encrypted with **AES-256-GCM**, and sent only to Simutronics' official authentication servers.

## If you're unsure about a feature

Open an issue with the `policy-question` label *before* writing the PR (or ask in the Discord `policy-questions` channel). The useful framing: describe what the user does, what the app does in response, and whether the user is at the keyboard when each step happens. If "the user isn't at the keyboard" appears anywhere in the flow, the feature probably needs a rethink.

The full developer-facing compliance review is [POLICY.md](https://github.com/GenieClient/Genie5/blob/main/docs/POLICY.md).

## Related

- [AI Advisor](AI-Advisor) — the advisor-only design and its privacy gates.
- [The Mapper](Mapper) — attended-mode walking.
- [Connecting & Profiles](Connecting) — attended-only auto-reconnect, encrypted credentials.
