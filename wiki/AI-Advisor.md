# AI Advisor (planned)

> 🚧 **Roadmap / not enabled.** Genie 5 includes the scaffolding for an optional AI assistant, but it is **feature-flagged off** and makes **no external calls** in current builds. This page describes the design and — most importantly — the guarantees it must meet before any part of it ships on by default.

## What it is meant to be

An **advisor**, not an autopilot. The intended modes, in priority order:

1. **Parser assistance** — spotting unfamiliar game output and helping extend the parser (a development aid).
2. **Gameplay insight** — summarizing what's happening and surfacing suggestions in a panel you read.
3. **In-character advisor** — optional roleplay phrasing help (the most sensitive mode; see below).

In every case the output is **text you read and decide to act on**. It never types for you.

## The hard line: advisor-only, never agentive

The single most important rule: the AI **must not drive the game**. It does not call the command pipeline directly or by any indirect path. AI output is displayed; you choose what, if anything, to type. This is enforced as a release gate, and any change that wires AI output back into the command pipeline is rejected. See [Policy Compliance](Policy-Compliance) — agentive AI is one of the project's hard "nevers."

## How it's isolated

The AI path hangs off a **separate, toggleable branch** of the raw game stream. It can be turned on or off without touching the parser, and a slow response can never block the game loop — analysis runs fully asynchronously. There is currently **no way to enable it in the app** — no setting, checkbox, or `#config` key exposes it in any released build, so the rolling context buffer never feeds an external service. (Developers building from source can exercise the analysis pipe from the Console, which is a separate, never-shipped tool.) When it does ship, enabling it will be an explicit opt-in behind a plain-language disclosure.

```
raw game stream
  ├─► parser ─► game state / scripts / UI   (always on, never blocked)
  └─► AI branch (off by default) ─► context buffer ─► external AI service ─► suggestion panel
```

## Privacy guarantees (the release gates)

Before any external send is enabled by default, the design requires all of these:

- **Default OFF, opt-in.** A checkbox in Settings, behind a plain-language privacy disclosure.
- **Other players' words are stripped first.** Before anything is sent to an external service, the context buffer removes other players' utterances — whispers, speech, thoughts, familiar/group chatter, and tells. Those are content other players never consented to have forwarded.
- **In-character mode stays off** entirely until there's an explicit read of the relevant terms around player-generated content.

Your local **session recordings** (from **File → Record Session**) are a separate thing: they're written only to your machine and never leave it.

## Why so cautious

DragonRealms is a shared, social game with its own software policy, and other players' words aren't ours to ship to a third party. The conservative posture — off by default, opt-in, other-player content stripped, never agentive — is what lets an assistant exist at all without crossing those lines. The same reasoning runs through the rest of the client; see [Policy Compliance](Policy-Compliance).

## Related

- [Policy Compliance](Policy-Compliance) — the hard "nevers," including agentive AI and shipping others' speech.
- [Architecture](Architecture) — the one-way pipeline the AI branch taps without blocking.
