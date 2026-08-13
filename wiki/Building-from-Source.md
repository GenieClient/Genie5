# Building from Source

For contributors and the curious. End users only need [Installation](Installation); this page covers the project layout, the developer test harness, and where the engineering docs live. The canonical contributor guide is [CONTRIBUTING.md](https://github.com/GenieClient/Genie5/blob/main/CONTRIBUTING.md).

## Prerequisites

- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** (Windows, macOS, Linux).
- A DragonRealms account if you want to test against a live server (free trial accounts work for most testing).

## Clone and build

```bash
git clone https://github.com/GenieClient/Genie5.git
cd Genie5
dotnet build
dotnet run --project src/Genie.App
```

## Project layout

```
Genie5/
├── src/
│   ├── Genie.Core/         # Pure class library — no UI deps
│   │   ├── Connection/     # GameConnection, SgeAuthClient
│   │   ├── Parser/         # DrXmlParser
│   │   ├── GameState/      # Live game-state engine
│   │   ├── Scripting/      # .cmd script interpreter
│   │   ├── Triggers/  Highlights/  …   # Rule engines
│   │   ├── Mapper/         # Zone map + pathfinding
│   │   ├── Profiles/       # Per-character encrypted credential store
│   │   ├── Update/         # Integrated updater (Core/Maps/Plugins)
│   │   └── AI/             # AI context buffer (off by default)
│   └── Genie.App/          # Avalonia GUI host (Views / ViewModels / Controls)
├── docs/                   # Developer docs (architecture, protocol, scripting, mapper)
├── wiki/                   # This wiki's source (end-user docs)
└── .github/workflows/      # CI / release pipelines
```

See [Architecture](Architecture) for why `Genie.Core` is kept UI-free.

## The dev test harness (Console)

`Genie.Core` builds as an executable so a headless **Console** harness can drive the engine without the GUI — invaluable for parser work. Run modes:

```bash
# Live session, capturing raw XML to test_results/
dotnet run --project src/Genie.Core -- DR <account> <password> <char>

# Replay a recording through the parser stack (speed: 0=max, 1.0=real-time, 5.0=5x)
dotnet run --project src/Genie.Core -- REPLAY <file> [speed]

# Diff parser output vs a tag-stripped baseline from the same recording
dotnet run --project src/Genie.Core -- COMPARE <file>

# List characters on an account without logging in
dotnet run --project src/Genie.Core -- LIST <account> <password>
```

Test-harness output lands in `test_results/` (gitignored — your captures stay local).

### Terminology: Live session / Recording / Replay

To keep these straight:

- **Live session** — a real socket open to `play.net` (or a Lich proxy), created by the **App** or the **Console**.
- **Recording** — the `.xml` file capturing a live session's raw byte stream (App: `Logs/`; Console: `test_results/`).
- **Replay** — running a recording back through the engine. It **only reads, never writes** — it cannot touch a live server.

(There is no in-App replay viewer yet; use the Console for replay.)

## Compatibility constraints (please respect)

Non-negotiable for any PR touching the relevant subsystem:

1. **`.cmd` script parity** — the engine must remain a faithful port of Genie 4's interpreter. Test against the [DR-Genie-Scripts](https://github.com/Tirost/DR-Genie-Scripts) collection.
2. **Map data format** — Genie 4 zone XML must round-trip without loss (24+ community forks depend on it).
3. **SGE protocol** — don't change handshake logic without verifying against the [Genie 4 source](https://github.com/GenieClient); small mistakes silently break auth.
4. **DragonRealms policy** — the hard "nevers" in [Policy Compliance](Policy-Compliance) will close a PR that introduces them.

## Developer docs index

| Doc | Topic |
| --- | --- |
| [SGE_PROTOCOL.md](https://github.com/GenieClient/Genie5/blob/main/docs/SGE_PROTOCOL.md) | The Simutronics login flow. |
| [dr-xml-protocol.md](https://github.com/GenieClient/Genie5/blob/main/docs/dr-xml-protocol.md) | The DragonRealms XML stream and the parser. |
| [line-pipeline.md](https://github.com/GenieClient/Genie5/blob/main/docs/line-pipeline.md) | How one event fans out to state/scripts/UI. |
| [scripting-engine.md](https://github.com/GenieClient/Genie5/blob/main/docs/scripting-engine.md) | The script runtime. |
| [mapper.md](https://github.com/GenieClient/Genie5/blob/main/docs/mapper.md) · [multi-zone-travel.md](https://github.com/GenieClient/Genie5/blob/main/docs/multi-zone-travel.md) | The mapper and cross-zone routing. |
| [build-and-release.md](https://github.com/GenieClient/Genie5/blob/main/docs/build-and-release.md) | Per-platform publishing. |
| [GENIE4_VS_GENIE5.md](https://github.com/GenieClient/Genie5/blob/main/docs/GENIE4_VS_GENIE5.md) | Feature-by-feature comparison with Genie 4. |

## Contributing workflow

Open an issue first for anything non-trivial, branch from `main`, keep PRs focused, include a test plan, and update docs in the same PR. Full details in [CONTRIBUTING.md](https://github.com/GenieClient/Genie5/blob/main/CONTRIBUTING.md).

## Related

- [Architecture](Architecture) — the big-picture design.
- [Installation](Installation) — the end-user build path.
- [Plugins](Plugins) — extend Genie without modifying the core.
