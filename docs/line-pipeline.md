# Line Pipeline

This page documents what happens to a unit of game output between the [DrXmlParser](../src/Genie.Core/DrXmlParser.cs) emitting a `GameEvent` and that event reaching the UI, scripts, game-state, and rule engines. Most "the line didn't appear where I expected" / "my trigger didn't fire" questions are answered by getting this fan-out right.

Unlike Genie 4 (and the earlier Kzin prototype), there is **no single `LineReceived` handler**. Genie 5 is event-driven: the parser publishes typed events on a [Reactive](https://github.com/dotnet/reactive) `IObservable<GameEvent>`, and each consumer subscribes to exactly the event types it cares about. The "order of operations" is therefore the set of subscriptions wired in [GenieCore](../src/Genie.Core/GenieCore.cs), plus the UI-side render path in [GameTextViewModel](../src/Genie.App/ViewModels/GameTextViewModel.cs).

## End-to-end view

```
GameConnection.RawXmlStream  (hot IObservable<string>)
    │
    ├─► DrXmlParser.Feed → parser GameEvents (IObservable<GameEvent>)
    │        │
    │        ├─► GameStateEngine.Apply           live GameState snapshot (vitals, room, RT, statuses)
    │        ├─► MapperGameStateAdapter          feeds AutoMapperEngine.OnStateChanged
    │        ├─► ScriptGlobalsSync.OnEvent       mirror state into Scripts.Globals ($health, $north, …)
    │        └─► GenieCore._gameEventSub:
    │                every event → Scripts.OnGameEvent  (built-in trackers, before the type switch)
    │                TextEvent   → ProcessGameTextEvent:
    │                                Plugins.DispatchGameText   (transform / gag — null stops the line here)
    │                                → Scripts.OnGameLine
    │                                → Triggers.ProcessLine     (gated by #config parsegameonly)
    │                                → _gameEventsRelay         (the public GenieCore.GameEvents)
    │                PromptEvent → Scripts.OnRoomChanged (if a room change is pending)
    │                              → Scripts.OnPrompt → Plugins.DispatchPrompt
    │                NavEvent / room-title component → flag the room change for the next prompt
    │
    ├─► Plugins.DispatchXml   (raw XML chunks — Genie 4 ParseXML parity)
    └─► AiRawStream (toggleable) → AiContextBuffer   (never blocks the parser)

GenieCore.GameEvents  (relay Subject, fed post-plugin by ProcessGameTextEvent)
    └─► GameTextViewModel (UI):
            TextEvent(stream=="main") → Substitutes → Gags → highlight render
            side-stream TextEvents     → their own stream VMs / tabs (IfClosed routing)
```

The engine-side state consumers — `GameStateEngine`, the `MapperGameStateAdapter`, and `ScriptGlobalsSync` — each subscribe **independently** to the parser's `GameEvents` in `GenieCore.BuildConnection()`, in that order, all before the `_gameEventSub` that drives plugins/scripts/triggers. Rx delivers events to subscribers in subscription order, so that wiring order is load-bearing — see below.

The UI does **not** subscribe to the parser directly: it subscribes to `GenieCore.GameEvents`, a persistent **relay** `Subject` (`_gameEventsRelay`) that survives reconnects and — for `TextEvent`s — is fed at the *end* of `ProcessGameTextEvent`. The UI therefore never sees a plugin-gagged line or the pre-transform text.

## Why the ordering matters

- **State and globals are applied before scripts see the line.** `GameStateEngine` and `ScriptGlobalsSync` subscribe before `_gameEventSub`. So by the time `Scripts.OnGameLine(text)` runs for a `TextEvent`, `$webbed`, `$health`, `$roomexits` etc. already reflect any indicator/vital/compass events that arrived earlier in the same burst. A script's `matchwait`/`waiteval` evaluates against current state, not last line's.
- **Plugins run first on every `TextEvent`** — before scripts, triggers, and the display. See [Plugins run first](#plugins-run-first) below.
- **Scripts run before triggers, both on every `TextEvent`.** `GenieCore.ProcessGameTextEvent` calls `Scripts.OnGameLine` then `Triggers.ProcessLine`. Scripts get first crack at a line (to satisfy a pending `matchwait`); triggers fire their command actions after.
- **Triggers see every stream by default.** The gate is `Config.ParseGameOnly` (default **false**, [GenieConfig](../src/Genie.Core/Config/GenieConfig.cs)); `#config parsegameonly on` restricts the trigger pass to the `main` stream (Genie 4 parity).
- **`PromptEvent` advances RT-gated scripts.** A prompt is the natural unblock for `wait`, decrements in-flight type-ahead, and lets the roundtime gate re-check. The DR server only prompts in response to commands — see the [scripting engine's roundtime gate](scripting-engine.md#the-roundtime-gate).
- **A room change unblocks `move` — on the next prompt.** `NavEvent` does **not** call `Scripts.OnRoomChanged` directly; it sets a `_roomChangedSincePrompt` flag, and so does the room-title `ComponentEvent` (which covers uid-less "(**)" rooms that emit no `NavEvent`, PR #92). The flag is consumed on the **next `PromptEvent`**, where `Scripts.OnRoomChanged` runs deliberately *before* `Scripts.OnPrompt` — a `move` issued by a script resumed during `OnPrompt` must wait for the *next* room change, not be unblocked by this turn's.
- **The AI pipe is a separate tap on raw XML**, gated by `AiPipeEnabled`, and is structured so it can never block the parser or the game.

## Plugins run first

`GenieCore.ProcessGameTextEvent` ([GenieCore.cs](../src/Genie.Core/GenieCore.cs)) is the per-line pipeline for every live `TextEvent`, and its first leg is `Plugins.DispatchGameText(text, stream)` — Genie 4 order (its `ParsePluginText` ran before `TriggerParse`), with a deliberate Genie 5 extension: the plugin's return value is **honored**.

- A **rewrite** feeds the modified text to scripts, triggers, and the display (spans are dropped — their offsets are meaningless in the new text).
- A **`null` return gags the line everywhere downstream** — early return: no scripts, no triggers, no relay, so the UI never sees it.
- `GameStateEngine`, `ScriptGlobalsSync`, the mapper, and the built-in extensions all run off the **raw parser events**, so a plugin gag can never corrupt game state — it is authoritative over what is *seen*, not what *happened*.

Plugins also hook the other legs of the loop:

- **`Plugins.DispatchEcho`** — every echoed display line (`#echo`, script output, echo-to-window) runs through the plugin chain before its event fires; a plugin can rewrite or gag echoes too.
- **`Plugins.DispatchXml`** — raw XML chunks, straight off `RawXmlStream` (Genie 4 `ParseXML` parity), for structured data the typed events don't surface.
- **`Plugins.DispatchPrompt`** — fired on every `PromptEvent`, after `Scripts.OnPrompt`.
- **`Plugins.DispatchInput`** — the user's typed line (see [Command path](#command-path-the-other-direction)); a plugin may transform it or swallow it entirely.

`Scripts.OnGameEvent(evt)` also runs for **every** event, before the per-type switch in `_gameEventSub` — the built-in trackers (Spell Timer, Experience) consume fully-parsed events there. `#parse` / `GenieCore.InjectParsedLine` replays the same per-line legs (plugins → scripts → triggers) for a synthetic line, minus echo/socket/type-ahead.

## Engine-side consumers

### GameStateEngine — the live snapshot

[GameStateEngine](../src/Genie.Core/GameStateEngine.cs) is the single source of truth for "what is the character doing right now." It matches on event type and writes into the shared [GameState](../src/Genie.Core/GameState.cs):

- `ProgressBarEvent` → `Vitals.*`
- `ComponentEvent` → `Room.*` (title/desc/exits/objs/players), `Combat.Stance`, `CharacterName`; `exp <skill>` → `LiveSkills`
- `RoundTimeEvent`/`CastTimeEvent` → `Combat.RoundTimeEnd`/`CastTimeEnd`
- `IndicatorEvent` → adds/removes a `CharacterStatus` in `ActiveStatuses`
- `HeldItemEvent` → `Inventory.LeftHand`/`RightHand` (display names; nouns in `LeftHandNoun`/`RightHandNoun`, + exist ids)
- `SpellEvent` → `Combat.PreparedSpell`, `NavEvent` → `Room.RoomId`, `CompassEvent` → `Room.CompassExits`

The UI binds to `GameState`; scripts read it indirectly through globals; the mapper reads it through an adapter.

### ScriptGlobalsSync — Genie 4 reserved variables

[ScriptGlobalsSync](../src/Genie.Core/Scripting/ScriptGlobalsSync.cs) mirrors state into `Scripts.Globals` so community scripts can read the Genie 4 vocabulary: `$health`, `$mana`, `$stamina`/`$fatigue`, `$righthand`/`$righthandnoun`/`$righthandid`, `$preparedspell`, `$stance`, the status flags (`$standing`, `$webbed`, …), the per-exit booleans (`$north`, `$up`, …), and the room fields (`$roomname`, `$roomdesc`, `$roomexits`, `$gameroomid`). It uses **per-event-type dispatch** (each callback touches only the 1–12 variables relevant to that event) and writes into a `ConcurrentDictionary` so the parser thread and script reads don't need a lock. Defaults are seeded at construction so a script launched the instant after connect sees usable values.

## UI render path — GameTextViewModel

The user-facing rule pipeline lives in [GameTextViewModel.Attach](../src/Genie.App/ViewModels/GameTextViewModel.cs) (which subscribes to the post-plugin `GenieCore.GameEvents` relay). For each `TextEvent` on the `main` stream, observed on the UI thread:

1. **Display filter** — skip if **Window → Game Text** is toggled off (`DisplaySettings.ShowGameText`).
2. **Name List Only** — when the right-click `NameListOnly` filter is on (and the Names list is non-empty), drop game lines that don't mention a tracked name.
3. **Substitutes** — `core.Substitutes.Apply(text)` rewrites the text. Genie 4 ordering: substitute first, then gag.
4. **Gags** — `core.Gags.ShouldGag(text)` drops the whole line if any enabled rule matches.
5. **Condensed** — `#config condensed` drops blank / whitespace-only lines from the main window (read live; Genie 4 parity).
6. **Span carry** — link, bold, **and preset** spans are kept **only if no substitute fired** (`ReferenceEquals(text, e.Text)`). A substitution shifts character offsets, so spans are dropped rather than remapped — clickable text is a UX bonus, not a correctness requirement.
7. **Render** — `AddLine` appends a `TextLine`. Highlighting is applied lazily by `TextLine.Inlines`, which tokenizes via [DefaultHighlights](../src/Genie.App/Highlighting/DefaultHighlights.cs) (user rules + link/bold spans). The scrollback cap comes from `#config scrollbacklines` — default 2000, clamped to [100, 100000] ([GenieConfig](../src/Genie.Core/Config/GenieConfig.cs)).

Echoes (typed commands, `#echo`, `[script]`/`[recorder]` diagnostics) arrive on the `EchoLine` event, not as `TextEvent`s. They render with the `System` colour and are gated by `ShowEchoText` (bare) or `ShowScriptText` (bracketed `[tag]` lines).

### Live re-highlighting

When the user adds or edits a highlight rule, `UserHighlights.RulesChanged` fires and `RetokenizeAllLines` replaces every existing `TextLine` with a fresh instance so already-rendered text repaints — not just future lines.

### Side streams

`TextEvent`s on non-`main` streams (`logons`, `talk`, `whispers`, `thoughts`, `familiar`, …) route to their own stream view-models / dock tabs. Note that side-stream lines **do** run the script and trigger passes by default — the engine-side pipeline is stream-agnostic unless `#config parsegameonly on` restricts triggers to `main` (see above). What follows is display routing only.

When a stream's panel is **closed**, delivery is decided by [IfClosedResolver](../src/Genie.Core/Layout/IfClosedResolver.cs) (shipped beta.3, public #211), called from [StreamTabsViewModel](../src/Genie.App/ViewModels/StreamTabsViewModel.cs). Each window's `IfClosed` setting resolves to one of:

- **`null` (default)** → fold into the main window via `AddStreamLine`, prefixed with `[stream]` — the classic behaviour, now just one outcome.
- **`""` (empty)** → **drop** the line entirely (the only value that drops).
- **Another window id** → deliver to that window's buffer. If the target is *also* closed, the resolver follows the chain (with a cycle guard — a cycle falls back to Main). An **unknown/unregistered id never drops** — it routes to Main, so a stale profile can't silently lose combat or talk text.

Independently, each stream has an **"Also show in Main window"** toggle (`WindowSettings.EchoToMain` → `GameTextViewModel.EchoStreamToMain`): the line renders inline in main *without* the `[stream]` prefix — span metadata rides along so it renders identically to a native main-stream line — while the stream's own panel still receives it.

## Class gating

[ClassEngine](../src/Genie.Core/Classes/ClassEngine.cs) holds boolean classes the user toggles. Triggers, highlights, substitutes, and gags can each be scoped to a class; if the class is off, the rule is skipped at evaluation time. State is live — toggling a class affects every associated rule on the next line. This is wired by assigning `Classes` onto each engine in the `GenieCore` constructor.

## Command path (the other direction)

User input flows the opposite way, through [CommandEngine](../src/Genie.Core/Commanding/CommandEngine.cs) via `GenieCore.ProcessInput`. Three consumers see the typed line **before** alias expansion — all at this genuine user-input boundary only (programmatic sends from scripts/aliases/mapper go straight to `Commands.ProcessInput` and bypass them):

```
ProcessInput(text)
  → Scripts.Extensions.DispatchSlashCommand   (built-in /commands, e.g. /spelltimer — handled, stops here)
  → Plugins.DispatchInput                      (IGeniePlugin.OnInput — may transform or swallow the line)
  → Triggers.ProcessLine                       (only if #config triggeroninput is on)
  → Commands.ProcessInput:
      alias expansion + separator split (`;`)
      → #cmd routing (#var, #trigger, #echo, #class, … — see CommandEngine)
      → ICommandHost.SendToGame → local echo (EchoLine) + AutoMapper.OnCommandSent + socket
```

`AutoMapper.OnCommandSent` lets the mapper observe outgoing movement verbs so it can correlate the next room change with the direction you moved. Mapper walking has meta-commands: `#goto` / `#go2` starts a walk to a room id, note label, or title, and `#mapper <sub>` (e.g. `#mapper reset`) drives the mapper host — both in [CommandEngine](../src/Genie.Core/Commanding/CommandEngine.cs); there is no `#travel`. The walk itself is driven by [AutoWalkService](../src/Genie.App/Services/AutoWalkService.cs); see [mapper.md](mapper.md).

## Diagnostics

- **Window → Game Text / Echo Lines / Script Lines** toggle what's rendered.
- **File → Record Session (raw XML)** captures the verbatim stream for replay.
- Trace/debug logging on `DrXmlParser` and `GameStateEngine` surfaces unknown tags.

## Code references

- **[GenieCore.cs](../src/Genie.Core/GenieCore.cs)** — all engine-side subscriptions; the `_gameEventSub` fan-out; `ProcessGameTextEvent` (the plugin-first per-line pipeline); the relay subjects; the command host.
- **[PluginManager.cs](../src/Genie.Core/Plugins/PluginManager.cs)** — the `DispatchGameText` / `DispatchEcho` / `DispatchXml` / `DispatchPrompt` / `DispatchInput` plugin chain.
- **[IfClosedResolver.cs](../src/Genie.Core/Layout/IfClosedResolver.cs)** — closed-stream delivery routing (Main / Drop / chain-follow).
- **[GameStateEngine.cs](../src/Genie.Core/GameStateEngine.cs)** — events → live `GameState`.
- **[ScriptGlobalsSync.cs](../src/Genie.Core/Scripting/ScriptGlobalsSync.cs)** — events → script globals.
- **[GameTextViewModel.cs](../src/Genie.App/ViewModels/GameTextViewModel.cs)** — UI substitute → gag → highlight render path.
- **[SubstituteEngine.cs](../src/Genie.Core/Substitutes/SubstituteEngine.cs)**, **[GagEngine.cs](../src/Genie.Core/Gags/GagEngine.cs)**, **[HighlightEngine.cs](../src/Genie.Core/Highlights/HighlightEngine.cs)**, **[NameHighlightEngine.cs](../src/Genie.Core/Highlights/NameHighlightEngine.cs)** — user-rule engines.
- **[TriggerEngineFinal.cs](../src/Genie.Core/Triggers/TriggerEngineFinal.cs)** — command-firing triggers.
- **[CommandEngine.cs](../src/Genie.Core/Commanding/CommandEngine.cs)** — input routing and `#cmd` dispatch.
