# Genie 5

Genie 5 is a cross-platform client for **[DragonRealms](https://www.play.net/dr)**, Simutronics' text MMO. It's a from-scratch rewrite of [Genie 4](https://github.com/GenieClient) on **.NET 10** and **[Avalonia](https://avaloniaui.net/)**, keeping Genie 4's scripting, mapping, highlighting, and triggering while running natively on **Windows, macOS, and Linux**.

![Genie 5 in play — dockable panels around the game text, with the Mapper floating](images/interface-default-layout.png)

> ⚠️ **Beta.** Genie 5 is in active development — feature-complete for daily play, expect rough edges. Pages here describe current behavior and flag 🚧 items that are still on the roadmap.

> ⬇️ **Pre-built downloads are here.** Native builds ship for **Windows, macOS (Apple Silicon + Intel), and Linux**, with an in-app updater on each. Grab the [latest release](https://github.com/GenieClient/Genie5/releases/latest) — see [Installation](Installation) to pick the right one, and [Releases & Changelog](Releases) for what's new.

This wiki is organized **crawl → walk → run** — the further down you read, the more detail you get. New here? Start at the top and stop when you have what you need.

---

## 🐤 Crawl — get it running

Everything you need for a first successful login.

| Page | What it covers |
| --- | --- |
| **[Installation](Installation)** | Download a pre-built build for Windows, macOS, or Linux (or build from source). |
| **[Quick Start](Quick-Start)** | Your first 5 minutes: connect, play, save a profile, run a script. |
| **[Releases & Changelog](Releases)** | The latest build, what changed, and which download to grab. |
| **[Importing from Genie 4](Importing-Genie4-Config)** | Bring your aliases, triggers, highlights, macros, and variables across. |
| **[Application Folders](Application-Folders)** | Where Genie 5 keeps scripts, maps, logs, and config on each OS. |

## 🚶 Walk — everyday use

Day-to-day playing, customizing, and automating.

| Page | What it covers |
| --- | --- |
| **[Connecting & Profiles](Connecting)** | The three connection modes (SGE, Lich, replay), the Connect dialog, and encrypted per-character profiles. |
| **[The Interface](The-Interface)** | The dockable panels — game text, vitals, hands, room, stream tabs, mapper — plus the command bar, clickable links, themes, and saved layouts. |
| **[Text-to-Speech](Text-to-Speech)** | Natural neural voices that read the game aloud — offline, free, and private, with per-stream read-aloud and spoken alerts. |
| **[Configuration & Rules](Configuration)** | Aliases, triggers, highlights, substitutes, gags, macros, variables, and classes — set from the command bar or the Configuration dialog. |
| **[Scripting](Scripting)** | Write and run `.cmd` scripts — a friendly tour from "hello world" up. |
| **[The Mapper](Mapper)** | Room tracking, click-to-walk, Less Obvious Paths, and the attended-mode walking rules. |
| **[Updating Maps & Scripts](Updating-Maps-and-Scripts)** | Pull the latest community zone maps and keep them merged with your own progress. |
| **[Lich 5 Integration](Lich-5-Integration)** | Run Genie behind a [Lich 5](https://github.com/elanthia-online/lich-5) proxy with your Ruby scripts intact. |

## 🏃 Run — power user & contributor

The deep end: the full scripting language, the mapper's internals, plugins, and how the engine is put together.

| Page | What it covers |
| --- | --- |
| **[Scripting Reference](Scripting-Reference)** | The complete `.cmd` vocabulary, `%`/`$` variable scopes, the roundtime gate, type-ahead, and where Genie 5 diverges from Genie 4. |
| **[JavaScript Scripting](JavaScript-Scripting)** | Write scripts in JavaScript (`.js`) alongside `.cmd` — the `genie.*` API, `include`, and `js`/`jscall` interop. |
| **[Cross-Zone Travel](Cross-Zone-Travel)** | The `ZoneConnections.xml` transit graph, the multi-zone pathfinder, and the connection editor. |
| **[Plugins](Plugins)** | The plugin contract, the `#plugin` command, the Experience-tracker example, and the trust model. |
| **[Keeping Up to Date](Updates)** | The integrated updater — Core app, maps, plugins, and scripts — and the per-kind auto-update settings. |
| **[AI Advisor (planned)](AI-Advisor)** 🚧 | The advisor-only AI design and its privacy guarantees — opt-in, never agentive. |
| **[Policy Compliance](Policy-Compliance)** | How Genie 5 stays a responsive, good-citizen frontend — and the hard "nevers" it holds about its own behavior. |
| **[Architecture](Architecture)** | The one-way pipeline, the `Genie.Core` / `Genie.App` split, and the embedding story. |
| **[Building from Source](Building-from-Source)** | Project layout, the dev test harness, and the developer docs index. |
| **[Troubleshooting & FAQ](Troubleshooting)** | Common problems and how to fix them. |

---

## Three ways to connect

Genie 5 reaches DragonRealms three ways — all chosen from **File → Connect…**:

- **Simutronics SGE login** — the standard "log in with your DragonRealms account" flow. Genie 5 authenticates and finds the right game server itself — over TLS by default, with a 🔒 padlock in the title bar to show it.
- **Lich proxy** — point Genie at a running [Lich 5](https://github.com/elanthia-online/lich-5) on `127.0.0.1:8000`; your Ruby scripts keep working.
- **Dev replay** — replay a recorded session through the engine (development and testing).

See **[Connecting & Profiles](Connecting)** for the walkthrough.

## Community & links

- **Discord** — [discord.gg/MtmzE2w](https://discord.gg/MtmzE2w) — the long-running Genie community server (shared with Genie 4). Alpha-tester chat, scripting help, mapper questions.
- **Issues** — [report a bug or request a feature](https://github.com/GenieClient/Genie5/issues).
- **Releases** — [download builds](https://github.com/GenieClient/Genie5/releases) as they ship.
- **Contributing** — [CONTRIBUTING.md](https://github.com/GenieClient/Genie5/blob/main/CONTRIBUTING.md). PRs welcome.
- **Developer docs** — the [`docs/` folder](https://github.com/GenieClient/Genie5/tree/main/docs) covers the parser, scripting engine, and mapper internals.
- **Maintainer** — Genie 5 is maintained by the Genie community and open-source contributors. **Shadow Realms LLC** is the project's support partner and signs the Windows release binaries. See [CREDITS.md](https://github.com/GenieClient/Genie5/blob/main/CREDITS.md).

> Genie 5 is third-party software for DragonRealms. DR's [Scripting Policy](https://elanthipedia.play.net/Policy:Scripting_policy) asks players to stay responsive to the game — it's the player's call, not the client's. See **[Policy Compliance](Policy-Compliance)** for what that means in practice.
