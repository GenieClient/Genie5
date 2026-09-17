# Genie 5 Terminology

Shared vocabulary for talking about the project. Using these names
consistently keeps workflow descriptions from blurring into each other.
Last reviewed May 24, 2026.

## Two binaries

| Term | Means | Notes |
|---|---|---|
| **App** | `Genie.App` (Avalonia GUI) | What end-users play DR in. The thing that ships. |
| **Console** | `TestHarness` (`dotnet run --project tools/Genie.TestHarness`) | Dev-only CLI for parser work, analysis, offline automation. Never ships. |

## Three states of game data

| Term | Means |
|---|---|
| **Live session** | Real socket open to `play.net` (or `LichProxy`). Created by either App or Console. |
| **Recording** | The `.xml` file on disk that captured a live session's raw XML byte stream. Lands at `{AppData}/Genie5/Logs/raw_session_*.xml` when the App captures it, or `test_results/raw_session_*.xml` when the Console captures it. |
| **Replay** | Running a recording back through the engine. **Always reads, never writes.** Driven by `ConnectionMode.DevReplay` + `DevReplayServer`. |

## App features (menus you click)

| Feature | What it does |
|---|---|
| File → Connect / Disconnect | Open / close a live session |
| File → **Record Session** (checkbox) | While ON, captures the current live session's XML to `{AppData}/Genie5/Logs/`. Title bar shows 🔴 REC. ✅ Shipped May 24, 2026. |
| File → **Open Replay…** | **Does not exist yet.** Would let you visually scrub a recording in the App's GUI. On the backlog. |

## Console modes (`dotnet run -- <MODE>`)

| Mode | What it does |
|---|---|
| `DR / WIZ / LICH` | Create a recording from a live session (StormFront XML / Wizard plain-text / Lich proxy connections) |
| `REPLAY <file>` | Replay a recording through the engine — no GUI. Useful for parser stress-testing. |
| `COMPARE <file>` | Diff parser output vs tag-stripped baseline from the same recording |
| `ALIGN <xml> <txt>` | Diff XML-mode capture vs Wizard-mode capture (ground-truth alignment) |
| `VERBS [pattern]` | Offline scan of recordings → markdown verb catalog at `test_results/verb_catalog.md` |
| `LIST <acct> <pw>` | List characters on an account without logging in |

## Phrases to avoid (ambiguous)

- ❌ "Replay it in the app" — there's no in-App replay yet
- ❌ "Use replay to capture" — replay only reads, never writes
- ❌ "Dev mode" — too vague; say either **Console** or **DevReplay mode** (the engine's enum)

## Phrases that are clear

- ✅ "Record this in the App" — capture a new recording while playing
- ✅ "Run a Console replay over the recording" — re-run an existing recording through the engine
- ✅ "Open the recording in the App" — currently invalid; flags the gap (use the Console for now)
- ✅ "DevReplay mode" — specifically the `ConnectionMode.DevReplay` engine enum

## Common workflows

### "I want new game data to review"
1. Open the App
2. **File → Record Session** (toggle ON) — title bar should show 🔴 REC
3. **File → Connect** — log into live DR
4. Play through whatever you want to capture
5. Disconnect (auto-stops Record) — file lands in `{AppData}/Genie5/Logs/`

### "I want to analyze a recording offline"
- For verb / link inventory: `dotnet run --project tools/Genie.TestHarness -- VERBS`
- For parser-output diff: `dotnet run --project tools/Genie.TestHarness -- COMPARE <file>`

### "I want to re-run a recording through the engine without playing live"
- `dotnet run --project tools/Genie.TestHarness -- REPLAY <file> [speed]`
- Speed: 0 = max (parser-dev), 1.0 = real-time, 5.0 = 5×

## Trigger phrase to resume

**"check the terminology"** — pulls this file up.
