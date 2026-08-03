# Architecture

A high-level map of how Genie 5 is put together — useful if you're contributing, writing a plugin, or just curious how a stream of bytes from DragonRealms becomes the room on your screen. For the deep internals, follow the links to the [developer docs](https://github.com/GenieClient/Genie5/tree/main/docs).

## One-way pipeline

Game data flows in exactly one direction. Nothing downstream writes back upstream — that constraint is what keeps the parser fast and the system predictable.

```
TCP bytes
  └─► GameConnection          (SGE auth, or a Lich proxy connection)
        └─► raw XML stream     (always on)
              ├─► parser ─► typed game events ─► game-state engine ─► live snapshot
              │                                     │
              │                                     ├─► scripts
              │                                     ├─► rule engines (triggers/highlights/…)
              │                                     └─► UI panels
              └─► AI branch (off by default, never blocks the parser)
```

A few consequences worth knowing:

- The **parser is never blocked** by anything downstream — the optional AI branch taps the raw stream on its own toggleable path. See [AI Advisor](AI-Advisor).
- **Roundtime** is captured from the server's absolute timestamps, so timing is correct regardless of the order tags arrive in — important for the script engine's [roundtime gate](Scripting-Reference#the-roundtime-gate).
- DragonRealms sends some content (speech, whispers) to multiple streams by design; **deduplication happens at the display layer**, not in the parser.

## Three projects

| Project | Role |
| --- | --- |
| **`Genie.Core`** | A pure class library with **zero UI dependencies**: connection, protocol parsing, game state, the script engine, the rule engines, the mapper, the plugin host, and the **built-in extensions** subsystem (the Experience, Spell Timer, Time Tracker, Circle Calculator, and Inventory View trackers live here as in-Core extensions). |
| **`Genie.App`** | The Avalonia GUI host. It binds to `Genie.Core` observables and owns no game-logic state. |
| **`Genie.Plugins.Abstractions`** | The small, UI-free plugin contract library (`IGeniePlugin` / `IPluginHost`) that plugin DLLs compile against. See [Plugins](Plugins). |

Keeping `Genie.Core` UI-free is deliberate. It makes the engine testable without a UI, lets the dev test harness run headless, and keeps the door open to **embedding** the engine elsewhere.

### The embedding / Mudlet angle

Because `Genie.Core` carries the SGE auth and the DragonRealms XML parser with no UI baggage, it could be packaged as a companion library for another client — [Mudlet](https://www.mudlet.org/), for instance, is a popular cross-platform MUD client with no native DragonRealms/SGE support. Bringing DR support to that user base is an explicit long-term possibility, and it's a big reason the core/UI split is kept clean. (This is a direction, not a shipped feature.)

## How it talks to the server

- **SGE login** — the Simutronics authentication flow at `eaccess.play.net`: exchange a key, send the encrypted password, list characters, select the game server. Genie connects over **TLS on port 7910 by default**, falling back automatically to plaintext port 7900 if the TLS handshake fails or stalls (a padlock in the title bar shows which you got). Documented in [SGE_PROTOCOL.md](https://github.com/GenieClient/Genie5/blob/main/docs/SGE_PROTOCOL.md).
- **The DragonRealms stream** — an XML-ish protocol the parser turns into typed events (`TextEvent`, `NavEvent`, room/vitals/roundtime updates, …). Documented in [dr-xml-protocol.md](https://github.com/GenieClient/Genie5/blob/main/docs/dr-xml-protocol.md), with the per-line flow in [line-pipeline.md](https://github.com/GenieClient/Genie5/blob/main/docs/line-pipeline.md).

## Compatibility constraints

Three formats are treated as **non-negotiable** so Genie 5 stays a drop-in for the Genie 4 ecosystem:

- **`.cmd` script language** — a faithful port of the Genie 4 interpreter. See [Scripting Reference](Scripting-Reference).
- **Zone map XML** — the Genie 4 map format, round-tripped without loss (24+ community map forks depend on it). See [The Mapper](Mapper).
- **`.cfg` rule files** — Genie 4-format config so settings move between clients. See [Configuration & Rules](Configuration).

## Where to read more

The [`docs/` folder](https://github.com/GenieClient/Genie5/tree/main/docs) holds the engineering docs. A good reading order: [SGE Protocol](https://github.com/GenieClient/Genie5/blob/main/docs/SGE_PROTOCOL.md) → [DR XML Protocol](https://github.com/GenieClient/Genie5/blob/main/docs/dr-xml-protocol.md) → [Line Pipeline](https://github.com/GenieClient/Genie5/blob/main/docs/line-pipeline.md) → [Scripting Engine](https://github.com/GenieClient/Genie5/blob/main/docs/scripting-engine.md) → [Mapper](https://github.com/GenieClient/Genie5/blob/main/docs/mapper.md).

## Related

- [Building from Source](Building-from-Source) — get the projects compiling and the test harness running.
- [Plugins](Plugins) — how the UI-free contract lets plugins stay UI-agnostic.
- [Policy Compliance](Policy-Compliance) — the design rules the architecture enforces.
