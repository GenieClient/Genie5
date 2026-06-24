# Contributing to Genie 5

Thanks for your interest. Genie 5 is alpha-stage software with a small contributor base; clear bug reports and focused PRs are the most useful things you can offer.

## Quick links

- 🐛 [File a bug](https://github.com/GenieClient/Genie5/issues/new?template=bug_report.md)
- 💡 [Request a feature](https://github.com/GenieClient/Genie5/issues/new?template=feature_request.md)
- 🔒 [Report a security issue](SECURITY.md) — **please don't file these as public issues**
- 💬 [Discuss in Discord](https://discord.gg/MtmzE2w) — shared community server with Genie 4. Drop by for design questions before opening a big PR; it'll save us both review-cycle time.

## Building locally

### Prerequisites

- **.NET 8 SDK** — [download](https://dotnet.microsoft.com/download/dotnet/8.0)
- A DragonRealms account if you want to test against a live server (free trial accounts work for most testing)

### Clone + build

```sh
git clone https://github.com/GenieClient/Genie5.git
cd Genie5
dotnet build
dotnet run --project src/Genie.App
```

### Project layout

```
Genie5/
├── src/
│   ├── Genie.Core/         # Pure class library — no UI deps
│   │   ├── Connection/     # GameConnection, SgeAuthClient
│   │   ├── Parser/         # DrXmlParser
│   │   ├── GameState/      # Live game state engine
│   │   ├── Scripting/      # .cmd script interpreter
│   │   ├── Triggers/       # Trigger engine
│   │   ├── Highlights/     # Highlight rules
│   │   ├── Mapper/         # Zone map + pathfinding
│   │   ├── Profiles/       # Per-character encrypted credential store
│   │   └── AI/             # AiContextBuffer (rolling buffer + AI-vendor pipeline)
│   └── Genie.App/          # Avalonia GUI host
│       ├── Views/          # AXAML windows + dialogs
│       ├── ViewModels/     # ReactiveUI MVVM
│       ├── Controls/       # Custom Avalonia controls (MapCanvas, etc.)
│       └── Diagnostics/    # Session recorder
├── docs/                   # Long-form docs (ROADMAP, POLICY, etc.)
└── .github/workflows/      # CI / release pipelines
```

### Running the test harness

The `Genie.Core` test harness exposes several useful dev modes — see `src/Genie.Core/TestHarness.cs` for the full list, but quick highlights:

```sh
# Live session, capture raw XML to test_results/raw_session_*.xml
dotnet run --project src/Genie.Core -- DR <account> <password> <char>

# Replay a recording through the parser stack
dotnet run --project src/Genie.Core -- REPLAY <file>

# Compare parser output vs tag-stripped baseline
dotnet run --project src/Genie.Core -- COMPARE <file>

# Cross-FE A/B compare (FE:GENIE vs FE:STORM XML)
dotnet run --project src/Genie.Core -- FE_DIFF <file-a> <file-b>

# Verb catalog scan over recordings
dotnet run --project src/Genie.Core -- VERBS
```

Test-harness output lands in `test_results/`. That directory is gitignored — your captures stay local.

## Code style

- **C#** — follows the `.editorconfig` at the repo root. Most important: 4-space indent, file-scoped namespaces, `var` for obvious types, `System` usings first.
- **XAML / AXAML** — 2-space indent.
- **Markdown** — 2-space indent inside lists; trailing whitespace allowed (markdown uses it for line breaks).

The build doesn't fail on style today, but PRs that significantly diverge will get review comments.

## Compatibility constraints (please respect)

These are **non-negotiable** for any PR touching the relevant subsystem:

### 1. Genie 4 `.cmd` script parity
The script engine must remain a faithful port of Genie 4's interpreter. If a script worked in Genie 4 and breaks here, that's a regression. Test against the [DR-Genie-Scripts](https://github.com/Tirost/DR-Genie-Scripts) collection — especially `GenieHunter/hunt.cmd` and `MC_Setup`.

### 2. Map data format
Genie 4 `.xml` zone files must round-trip without loss. 24+ community forks of the Maps repo depend on this format. Use `Genie4MapImporter` + `Genie4MapExporter` for any map I/O changes.

### 3. SGE protocol
The wire-level protocol is documented in [docs/SGE_PROTOCOL.md](docs/SGE_PROTOCOL.md). Don't change SGE handshake logic without verifying against the [Genie 4 source](https://github.com/GenieClient) — small mistakes silently break auth.

### 4. DragonRealms policy
DR's [Scripting Policy](https://elanthipedia.play.net/Policy:Scripting_policy) is about staying **responsive to the game** — it does not require window focus, and it's the *player's* responsibility, not something the client enforces. Genie's job is to be a good frontend. That said, the client itself stays clear of unattended automation. The following are **hard nevers** — PRs that introduce them will be closed:

- ❌ Auto-reconnect (silently resuming a session after a drop)
- ❌ Agentive AI mode (AI driving `Commands.ProcessInput` directly)
- ❌ Headless mode / running without a visible UI
- ❌ Shipping other players' speech (whisper / talk / thoughts / familiar / tells) to external AI services without per-player consent

Note: anything that constrains how a player runs the client (e.g. the optional auto-walk idle pause) must be **opt-in and off by default**. See [docs/POLICY.md](docs/POLICY.md) for the rationale. If you're not sure whether a feature fits, ask in an issue *before* writing the PR.

## Pull request workflow

1. **Open an issue first** for anything beyond a trivial fix. Saves both of us from a "this isn't quite what we wanted" PR rejection.
2. **Branch from `main`** — `feature/short-description` or `fix/short-description`.
3. **Write a focused PR** — one feature, one fix. Multi-feature PRs are hard to review.
4. **Include a test plan** in the PR description — what you did, what you verified, what regressions are possible.
5. **Update docs** if you change user-visible behaviour. README, CONTRIBUTING, and any relevant file under `docs/` should reflect the new state.
6. **Run the build** before pushing — `dotnet build -c Release` must succeed cleanly. Warnings are fine; errors aren't.

PRs that touch parser / scripting / mapper subsystems may want a smoke-test against one or more real recordings; the test harness REPLAY mode is the easiest path.

## Issue templates

- **Bug report** — please include the OS, .NET version, what you did, what you expected, what actually happened, and (if relevant) a session XML snippet captured via **File → Record Session**.
- **Feature request** — describe the use case first ("when I'm hunting and …"), then the proposed solution. Bonus points for noting how Genie 4 / Lich / Wrayth handle the same thing.
- **Parser gap report** — if you see weird game text or untyped XML, capture a recording, find the relevant section, and paste it into the issue. Parser-gap reports are some of the most valuable contributions.

## Writing your first `.cmd` script

Genie 5's script engine is a faithful port of Genie 4's Wizard-derived
`.cmd` language. If you've written scripts for Genie 4, Wizard, or
StormFront, the syntax is identical. New to scripting? Here's a quick
tour.

### Where scripts live

Per-character profile dir at `{AppData}/Genie5/Profiles/{Character}-{Account}/Scripts/`,
or the shared `{AppData}/Genie5/Scripts/` if you haven't logged in yet.
On Windows that's `%APPDATA%\Genie5\…`; on macOS it's `~/Library/Application Support/Genie5/…`;
on Linux it's `~/.local/share/Genie5/…`. Drop `.cmd` files there, no
restart needed.

### Hello world

Create `Scripts/hello.cmd`:

```
echo Hello, %1!
```

Run from the command bar:

```
.hello world
```

Output: `Hello, world!`.

### The vocabulary at a glance

```
# This is a comment (a # followed by whitespace or end-of-line).
# Lines starting with #command are meta-commands (e.g. #echo, #put, #stop).

# Variables: $name is read from globals (live game state + #var values).
#            %1, %2, ... are script arguments.
#            Set a local: var foo = bar
#            Read it back: echo $foo

# Send a command to the game:
put look
#put north                    # synonym; same thing

# Wait for game text matching a label / regex:
match RoundtimeEnd You take time to focus your mind.
matchwait

# Or block on a substring:
waitfor You can move again

# Pause for a number of seconds:
pause 2.5
waitpause                     # sleep until current roundtime expires

# Conditionals on live game state:
if_health < 50 then put cast 1101
if_stunned then echo I'm stunned, doing nothing
if def(myAlias) then echo Have a named alias

# Loops via labels + goto:
LOOP:
  put assess
  pause 1
  goto LOOP
```

### Roundtime safety

Scripts are RT-aware: by default a script call to `put` while you're in
roundtime queues, retries, and respects type-ahead budget. You don't
need to write `waitpause; put north` — the engine handles it. Use
`waitpause` explicitly when you need to *gate* on RT expiry before
proceeding past the line (e.g., before computing a derived value).

### Game-state variables

Every game-state field is exposed as a `$variable`. Common ones:

| Variable | What it holds |
|---|---|
| `$health`, `$mana`, `$spirit`, `$concentration`, `$fatigue` | Current vital %s (0-100) |
| `$roomtitle`, `$roomdesc`, `$compass` | Current room info |
| `$righthand`, `$lefthand` | Held item nouns |
| `$preparedspell` | Spell name + slots (or empty) |
| `$stance` | `off` / `adv` / `fwd` / `neu` / `grd` / `def` |
| `$kneeling`, `$prone`, `$sitting`, `$stunned`, `$webbed`, etc. | Status booleans |
| `$char` | Your character's first name |

Type `#vars` at the command bar to see the full list at any time.

### What's different from Genie 4

A few intentional divergences and gotchas:

- **Per-character profile dirs**: scripts you save while playing Renucci
  live separately from scripts saved while playing Naper. The migration
  on first launch copies your existing Genie 4 `Scripts/` dir into the
  shared root once — after that, anything you `#var save` etc. goes to
  the active character's folder. See `pre_publish_checklist.md` for the
  `Character-Account` path format.
- **No `goto` into a deeper-indented label**: while we accept Genie 4's
  syntax, jumping into a nested block isn't reliable. Use `gosub` for
  reusable sub-routines.
- **Comments**: `#` only counts as a comment when followed by whitespace
  or end-of-line. `#put north` is a meta-command, not a comment.
- **Undefined `$var` aborts the script**: rather than expanding to empty
  silently (a common source of bugs in Genie 4), Genie 5 aborts the
  script with a clear reason. Use `if def(name)` to test first.

### Example scripts to study

The [DR-Genie-Scripts](https://github.com/Tirost/DR-Genie-Scripts)
community repo has ~55 real-world scripts ranging from one-liners to
500+-line hunt loops. Start with the simpler ones (alchemy assistants,
foraging helpers, simple buffers) before tackling combat scripts.

If you've written a useful script, submit it as a PR — community
contributions are welcomed.

## Code of Conduct

Be decent. The MUD community is small and we all want it to be welcoming. This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md) — please read it. Anyone behaving badly in issues / PRs / Discord will be banned from the repo at maintainer discretion.

## License

By contributing, you agree your contributions will be licensed under [GPL-3.0](LICENSE), the same license as the rest of Genie 5.
