# Contributing to Genie 5

Thanks for your interest. Genie 5 is beta-stage software with a small contributor base; clear bug reports and focused PRs are the most useful things you can offer.

## Quick links

- 🐛 [File a bug](https://github.com/GenieClient/Genie5/issues/new?template=bug_report.md)
- 💡 [Request a feature](https://github.com/GenieClient/Genie5/issues/new?template=feature_request.md)
- 👍 [Vote on priorities](https://github.com/GenieClient/Genie5/issues?q=is%3Aissue+is%3Aopen+sort%3Areactions-%2B1-desc) — no account setup, no comment; just a reaction
- 🔒 [Report a security issue](SECURITY.md) — **please don't file these as public issues**
- 💬 [Discuss in Discord](https://discord.gg/MtmzE2w) — shared community server with Genie 4. Drop by for design questions before opening a big PR; it'll save us both review-cycle time.

## Building locally

### Prerequisites

- **.NET 10 SDK** — [download](https://dotnet.microsoft.com/download/dotnet/10.0)
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
│   ├── Genie.Core/         # Core library — no UI deps (builds as exe for the TestHarness)
│   │   │                   # DrXmlParser.cs, GameState.cs, GameConnection.cs,
│   │   │                   # SgeAuthClient.cs, AiContextBuffer.cs live at the root
│   │   ├── Scripting/      # .cmd script interpreter + Jint .js runtime
│   │   ├── Commanding/     # #commands (CommandEngine)
│   │   ├── Config/         # #config settings (GenieConfig)
│   │   ├── Triggers/       # Trigger engine
│   │   ├── Highlights/     # Highlight rules
│   │   ├── Mapper/         # Zone map + pathfinding
│   │   ├── Profiles/       # Per-character encrypted credential store
│   │   ├── Plugins/        # Plugin host (PluginManager)
│   │   ├── Extensions/     # Built-in trackers (EXP, SpellTimer, TimeTracker, …)
│   │   ├── Update/         # In-app updater (Core/Maps/Plugins/Scripts)
│   │   └── …               # Aliases, Capture, Layout, Import, and more
│   ├── Genie.App/          # Avalonia GUI host
│   │   ├── Views/          # AXAML windows + dialogs
│   │   ├── ViewModels/     # ReactiveUI MVVM
│   │   ├── Controls/       # Custom Avalonia controls (MapCanvas, etc.)
│   │   └── Diagnostics/    # Session recorder
│   └── Genie.Plugins.Abstractions/  # Public plugin contract (IGeniePlugin, IPluginHost)
├── tests/Genie.Core.Tests/ # Unit test suite
├── docs/                   # Long-form docs (ROADMAP, POLICY, etc.)
├── wiki/                   # End-user documentation
└── .github/workflows/      # CI / release pipelines
```

### Running the Console

The `Genie.Core` Console (the dev-only CLI harness) exposes several useful dev modes — see `src/Genie.Core/TestHarness.cs` for the full list, but quick highlights:

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

Console output lands in `test_results/`. That directory is gitignored — your captures stay local.

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
6. **Run the build and tests** before pushing — `dotnet build -c Release` must succeed cleanly (warnings are fine; errors aren't), and `dotnet test tests/Genie.Core.Tests` must pass for the subsystems you touched.

PRs that touch parser / scripting / mapper subsystems may want a smoke-test against one or more real recordings; the test harness REPLAY mode is the easiest path.

## Issue templates

- **Bug report** — please include the OS, .NET version, what you did, what you expected, what actually happened, and (if relevant) a session XML snippet captured via **File → Record Session**.
- **Feature request** — describe the use case first ("when I'm hunting and …"), then the proposed solution. Bonus points for noting how Genie 4 / Lich / Wrayth handle the same thing.
- **Parser gap report** — if you see weird game text or untyped XML, capture a recording, find the relevant section, and paste it into the issue. Parser-gap reports are some of the most valuable contributions.

## Voting on priorities

Genie 5 is built by one person, so the *order* things get built in matters as
much as the list. Two ways to weigh in, neither of which requires writing code:

- **👍 a reaction on any open issue.** That is the vote. React to as many as you
  like, change your mind whenever. A [pinned priority
  board](https://github.com/GenieClient/Genie5/issues?q=is%3Aissue+is%3Aopen+label%3Apriority-board)
  rebuilds itself daily from those counts, and you can always sort the raw list
  [by votes](https://github.com/GenieClient/Genie5/issues?q=is%3Aissue+is%3Aopen+sort%3Areactions-%2B1-desc).
- **Polls in [Discussions](https://github.com/GenieClient/Genie5/discussions/categories/polls).**
  Before each beta, a poll goes up with a short list of candidates for that
  cycle. One vote per person, and it closes when the beta is cut.

Votes are input, not a contract. They show where demand is and they break ties,
but a crash affecting three people still outranks a convenience feature fifty
people want, and some popular requests are simply large — the `est:` label on
each issue is an honest guess at the size. If something well-voted keeps not
shipping, ask on the board and you'll get a straight answer about what's in the
way.

## Writing your first `.cmd` script

Genie 5's script engine is a faithful port of Genie 4's Wizard-derived
`.cmd` language. If you've written scripts for Genie 4, Wizard, or
StormFront, the syntax is identical. New to scripting? Here's a quick
tour.

### Where scripts live

All scripts live in a single shared folder at the data root:
`{AppData}/Genie5/Scripts/`. On Windows that's `%APPDATA%\Genie5\Scripts\`;
on macOS it's `~/Library/Application Support/Genie5/Scripts/`; on Linux
it's `~/.local/share/Genie5/Scripts/`. Drop `.cmd` files there, no
restart needed. (Scripts are deliberately *not* per-character — what's
per-character is rule files and saved variables, under
`Profiles/{Character}-{Account}/`.)

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
# This is a comment. Any script line whose first non-whitespace
# character is # is ALWAYS a comment — including lines like "#echo foo".
# Meta-commands never run as bare script lines; a script runs one by
# sending it to the command line, e.g.:  put #echo done

# Variables: $name is read from globals (live game state + #var values).
#            %1, %2, ... are script arguments.
#            Set a local: var foo bar
#            Read it back: echo %foo

# Send a command to the game:
put look
put north

# Wait for game text matching a label / regex:
match RoundtimeEnd You take time to focus your mind.
matchwait

# Or block on a substring:
waitfor You can move again

# Pause for a number of seconds (pure timer, default 1):
pause 2.5

# Conditionals on live game state:
if $health < 50 then put cast 1101
if $stunned = 1 then echo I'm stunned, doing nothing
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
need to guard every `put` yourself — the engine handles it. When you do
need to explicitly wait out roundtime before proceeding past a line
(e.g., before computing a derived value), use the Genie 4 idiom
`if ($roundtime > 0) then pause $roundtime`. Note that `waitpause` is a
plain alias of `pause` (a pure timer, one second by default) — it does
**not** wait for roundtime.

### Game-state variables

Every game-state field is exposed as a `$variable`. Common ones:

| Variable | What it holds |
|---|---|
| `$health`, `$mana`, `$spirit`, `$concentration`, `$fatigue` | Current vital %s (0-100) |
| `$roomname`, `$roomdesc`, `$roomexits` | Current room info (plus per-direction booleans like `$north`, `$out`) |
| `$righthand`, `$lefthand` | Held item display names ("razor-edged scimitar"); the bare nouns are `$righthandnoun` / `$lefthandnoun` |
| `$preparedspell` | Spell name + slots (or empty) |
| `$stance` | Full lowercase words: `offensive` / `neutral` / `defensive` / … |
| `$kneeling`, `$prone`, `$sitting`, `$stunned`, `$webbed`, etc. | Status booleans |
| `$charactername` | Your character's first name |

Type `#vars` at the command bar to see the full list at any time.

### What's different from Genie 4

A few intentional divergences and gotchas:

- **Per-character profile dirs**: rule files (`.json`, with Genie 4-style
  `.cfg` twins kept in sync) and saved variables
  live per character under `Profiles/{Character}-{Account}/`, so two
  characters keep separate highlights, triggers, and `#var save` state.
  `Scripts/` is a single shared folder at the data root — scripts are not
  per-character. See the wiki's Application Folders page for the layout.
- **No `goto` into a deeper-indented label**: while we accept Genie 4's
  syntax, jumping into a nested block isn't reliable. Use `gosub` for
  reusable sub-routines.
- **Comments** (a gotcha, not a divergence): a script line starting with
  `#` is *always* a comment, exactly as in Genie 4 — `#put north` in a
  script does nothing. Meta-commands run from a script only by sending
  them (`put #echo done`).
- **Undefined `$var` stays literal** (also Genie 4 parity): an
  unresolved `$name` is left in the text verbatim — it never aborts the
  script and never silently expands to empty. Use `if def(name)` to
  test before relying on one.

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
