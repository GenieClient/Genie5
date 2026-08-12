# DragonRealms XML Protocol

Once [SGE auth](SGE_PROTOCOL.md) hands off and the FE handshake completes, the DragonRealms game server streams a continuous flow of **XML-ish markup interleaved with bare text**. This page documents that stream, the tag vocabulary Genie 5 recognises, and how [DrXmlParser](../src/Genie.Core/DrXmlParser.cs) turns it into strongly-typed [GameEvent](../src/Genie.Core/GameEvents.cs) objects.

For the login handshake that precedes any of this — the `eaccess.play.net` flow, the FE identifier, StormFront vs. Wizard mode — see [SGE_PROTOCOL.md](SGE_PROTOCOL.md). This page picks up after the socket is live.

## What the stream is (and isn't)

DR's wire format looks like XML but is **not a well-formed document**:

- There is no root element. Tags arrive in a stream, sometimes nested, sometimes self-closed.
- A single logical element can be split across socket reads at any byte boundary.
- Bare text (room descriptions, combat, speech) is interleaved freely between tags.
- Some "tags" the standard `XmlReader` rejects outright — most notably a bare `<d>` with no attributes.

The parser's job is to consume whatever arrives, recover from malformations, and emit a clean event for each thing that happened. It deliberately does **not** try to build a DOM.

## Pipeline position

```
GameConnection           raw socket bytes, split into chunks at the next '>' OR '\n', whichever comes first
    │  RawXmlStream (hot IObservable<string>)
    ▼
DrXmlParser.Feed(chunk)  accumulate → ProcessBuffer → ParseTag / AccumulateText
    │  GameEvents (Subject<GameEvent>)
    ▼
GenieCore subscribers    GameStateEngine · ScriptGlobalsSync · Plugins · Scripts · Triggers
                         GameTextViewModel (UI)
```

The chunker is `GameConnection.EmitChunks` ([GameConnection.cs](../src/Genie.Core/GameConnection.cs)): each chunk ends at a `>` or a `\n` — whichever comes first — so a blank line (a bare `\n`) survives as its own chunk. [GenieCore](../src/Genie.Core/GenieCore.cs) wires `connection.RawXmlStream.Subscribe(parser.Feed)` in `BuildConnection()`. Everything downstream consumes `GameEvents`, never the raw stream (except the plugin `DispatchXml` hook, the optional AI pipe, and the Session Recorder, which tap raw XML directly).

## How parsing works

[DrXmlParser](../src/Genie.Core/DrXmlParser.cs) is a streaming character scanner, not a DOM parser. The hot path in `ProcessBuffer`:

1. Append the incoming chunk to `_rawBuffer`.
2. Find the next `<`.
   - Text before it → `AccumulateText`.
   - No `<` at all → it's all text; accumulate and return.
3. At a `<`, find the matching `>`.
   - No `>` yet → the tag is split across reads; **return and wait** for more data. This is how mid-tag TCP splits are handled — the partial tag simply stays in `_rawBuffer`.
   - Otherwise slice out the full tag and hand it to `ParseTag`.

### Tag parsing with a fallback

`ParseTag` handles three cases:

- **End tags** (`</component>`) — `XmlReader` in fragment mode can't read standalone end tags, so the name is scraped directly and dispatched to `HandleEndElement`.
- **Start/self-closing tags** — parsed with an `XmlReader` in `ConformanceLevel.Fragment` mode.
- **Tags `XmlReader` rejects** — caught `XmlException` falls through to a regex attribute scraper (`ParseAttributesFallback`) wrapped in a minimal `RawAttrReader` shim, so `HandleElement` works unchanged. This is what keeps bare `<d>` links working.

### Text-line accumulation

Because `GameConnection` splits chunks at every `>` (or `\n`), inline formatting tags (`<pushBold/>`, `<d>…</d>`) fragment a single visible line into many pieces. `AccumulateText` buffers raw fragments in `_textLineBuffer` and only emits a `TextEvent` when it sees a `\n` or an explicit `FlushTextLine()` (triggered at logical boundaries like `<prompt>`, `<pushStream>`, `</inv>`).

`EmitLine` does the final cleanup: HTML-decode, strip any embedded XML/ANSI, and **trim trailing whitespace only** — leading whitespace is significant (DR uses it for column alignment in `info`/`exp` output). Whitespace-only lines are dropped. A bare-text prompt (`>`, `H>`, `HR>`) emits a `PromptEvent` instead of a `TextEvent`.

### Link and bold spans

Clickable `<d cmd="…">` and external `<a href="…">` tags don't break the text — the inner text flows through normally. The parser bookmarks the buffer offset on the open tag and, on close, commits a [LinkSpan](../src/Genie.Core/GameEvents.cs) (`IsUrl=true` for `<a>`) attached to the resulting `TextEvent`. `<pushBold/>`/`<popBold/>` work the same way, producing `BoldSpan`s, and so does the paired `<b>…</b>` element (DR uses it for header emphasis in help text). When the `cmd` attribute is missing, the inner text doubles as the command (DR convention: `<d>BANK DEBT</d>` sends `BANK DEBT`). Both use small defensive stacks for nesting even though DR doesn't nest them in practice.

Styling spans ride the same mechanism as `PresetSpan`s: a wrapping `<preset id='…'>…</preset>` pushes the buffer offset on open and commits a `PresetSpan` at close, and the self-closing `<style id='…'/>` **toggle** (non-empty id = on, empty id = reset) spans the text between the two markers — that's how the room-title line gets its colour (there is no `<preset>` around it). The renderer colours preset spans via the PresetEngine.

## Tag reference

Each recognised tag emits one or more `GameEvent`s. Tags in the `_settingsTags` skip-set — the initial Wrayth settings dump and UI-layout-only markup — are consumed silently.

### Vitals and resources

| Tag | Event | Notes |
| --- | --- | --- |
| `<progressBar id='health' value='80' text='…'/>` | `ProgressBarEvent(id, value, text)` | `id` ∈ health, mana, spirit, stamina, concentration, encumbrance. Dropped (logged) if `id` or `value` is missing. |
| `<resource id='…' value='N'/>` | `ResourceEvent(id, value)` | Absolute mana/spirit/stamina values. |
| `<resource picture='N'/>` | `RoomImageEvent(pictureId)` | DR room/scene art id. `"0"` = none — surfaced too, so the Scene panel can clear. Display + `showimages` gating live in the App layer. |

### Time

| Tag | Event | Notes |
| --- | --- | --- |
| `<roundTime value='N'/>` | `RoundTimeEvent(expiresAt)` | `value` is the absolute Unix epoch when RT expires. Stored as `Combat.RoundTimeEnd`. |
| `<castTime value='N'/>` | `CastTimeEvent(expiresAt)` | Same shape, for spell prep. Also mirrored raw into `$casttime` (Genie 4 keeps the epoch verbatim so scripts can compose `$casttime - $spellstarttime`). |
| `<spelltime value='N'/>` | `SpellTimeEvent(startsAt)` | Server-authoritative epoch of when the current spell's prep began (Genie 4 `Game.cs:2131`). Rare — the normal prep sequence carries no such tag (prep start is stamped at `<spell>` arrival); honored when sent, but only while a spell is actually held. `value='0'` is inert. |
| `<prompt time='N'>…</prompt>` | `PromptEvent(ServerTime, Indicator)` | The timestamp is captured at the **open** tag; the event fires at the **close** tag carrying the decoded body — the indicator string (`>`, `R>`, `HR>`, …). Also flushes any partial text line first, and — as a stream-routing backstop — resets the active stream to `main` and clears the stream stack. |

### Room and navigation

| Tag | Event | Notes |
| --- | --- | --- |
| `<streamWindow id='room' subtitle=' - [Foo, Bar] (12345)'/>` | `WindowEvent(id, title)` + synthetic `ComponentEvent("room title", "[Foo, Bar]")` + synthetic `NavEvent("12345")` | DR carries the **room title in the subtitle** of the room stream window — there is no `<component id='room title'>`. The parser bridges it so the mapper sees titles (nested brackets pair last-`[` with last-`]`). Modern StormFront also appends the **server room uid** as "(NNNNN)" after the title (when room numbers are enabled) — this subtitle is the *only* carrier of the uid (the bare `<nav/>` has none), so it's emitted as a synthetic `NavEvent`. "(**)" (unknown room) has no digits and is correctly ignored. `<openWindow>` shares this handler. |
| `<component id='room desc'>…</component>` | `ComponentEvent(ComponentId, Content, BoldNames, BoldSpans)` | Also: `room objs`, `room players`, `room exits`. Content accumulates between open/close; `BoldNames`/`BoldSpans` carry any bolded creature phrases. The id is passed through **verbatim** — lowercasing happens in [GameStateEngine](../src/Genie.Core/GameStateEngine.cs) (`ApplyComponent`). |
| `<component id='exp Climbing'>…</component>` | `ComponentEvent("exp Climbing", text)` | Skill rank ticks. [GameStateEngine](../src/Genie.Core/GameStateEngine.cs) parses the rank int into `LiveSkills` for the weighted pathfinder. |
| `<compass><dir value='n'/>…</compass>` | `CompassEvent(rawXml)` | Space-joined direction tokens. Surfaced as `$roomexits` and per-exit booleans. |
| `<nav rm='12345'/>` / `<nav/>` | `NavEvent(roomId)` — only when `rm` is non-empty | Server-assigned room id — the mapper's most reliable fingerprint. **Modern DR sends a bare `<nav/>`** with no `rm`; the parser emits nothing for it (an empty NavEvent would trigger a premature fingerprint re-resolve) — the authoritative uid arrives via the room `streamWindow` subtitle above. |

### Hands, spell, inventory

| Tag | Event | Notes |
| --- | --- | --- |
| `<right exist='29' noun='sword'>an iron sword</right>` | `HeldItemEvent(Hand, Noun, ExistId, Display)` | Attributes are stashed at the **open** tag; the event fires at the **close** tag with the body captured as `Display` — the display name ("an iron sword") that Genie 4 exposes as `$righthand`, while `$righthandnoun` is the `noun` attribute. A self-closing `<right/>` emits immediately from attributes (defensive; DR always sends the body form). Merge-seam recovery (`FindMergeSeam`): the server sometimes merges a response into the body with no separator (`<right noun='ledger'>black ledgerYou unlock and open…</right>`); the parser splits on the first lower→upper seam — prefix = display name, suffix re-emitted as game text on the active stream. `<left>` mirrors it. |
| `<spell>Fire Strike</spell>` | `SpellEvent(name)` | Prepared spell; content accumulates between tags. Same merge-seam recovery as the hands: a response concatenated onto the spell name is split off and re-emitted as game text. |
| `<inv id='stow'>a finely carved shortbow</inv>` | (routed to `inv` stream) | The parser treats `<inv>` as an implicit push to the `inv` stream so item lines don't leak into the main window, popping back on `</inv>`. Same merge-seam recovery at the close: the prefix flushes on the `inv` stream, an appended response is re-emitted on the stream the inv block interrupted. |
| `<container id='stow' title='My Backpack' target='#37666728'/>` | `ContainerEvent(logicalId, title, targetId)` | Lets the UI render `#NNNN` ids as human names in click-echoes. Skipped if `target` is empty. |

### Streams and styling

| Tag | Event | Notes |
| --- | --- | --- |
| `<pushStream id='familiar'/>` | `StreamPushEvent(id)` | Subsequent text routes to the named stream; flushes the current line first. |
| `<popStream/>` | `StreamPopEvent(from, to)` | Returns to the previous stream. A stray pop on an **empty stack** (push/pop desync) recovers by resetting the active stream to `main` — DR's stream model is flat, so a pop always means "back to main". |
| `<clearStream id='…'/>` | `ClearStreamEvent(id)` | Tells the UI to wipe a stream panel. |
| `<output class='mono'/>` | `OutputClassEvent(class)` | Monospace toggle (used during EXP dumps). |
| `<preset id='roomDesc'>…</preset>` | `PresetSpan` on the `TextEvent` (+ sets `_currentPresetId`) | Pushes a span stack entry at open and commits a `PresetSpan` over the wrapped text at close. Also drives a `FlushTextLine` on close for `roomDesc`/`inv` so the following exits/items land on their own line (never for `whisper`/`speech` — their quoted content continues the same line). |
| `<style id='roomName'/>` … `<style id=''/>` | `PresetSpan` on the `TextEvent` | Self-closing **toggle**, not a wrapping pair: non-empty id starts a styled span, empty id ends it. This is how the room-title line gets its colour. |
| `<d cmd='…'>…</d>`, `<a href='…'>…</a>` | `LinkSpan` on the next `TextEvent` | See [Link and bold spans](#link-and-bold-spans). |
| `<pushBold/>` … `<popBold/>` | `BoldSpan` on the next `TextEvent` | DR uses bold for unread news, emphasis, etc. |
| `<b>…</b>` | `BoldSpan` on the next `TextEvent` | A real paired element (header emphasis in help text such as PROFILE HELP), same span mechanism as pushBold/popBold. A stray self-closing `<b/>` is ignored. |

### Status indicators

| Tag | Event | Notes |
| --- | --- | --- |
| `<indicator id='IconWEBBED' visible='y'/>` | `IndicatorEvent(id, visible)` | `visible` is `y`/`n`. [GameStateEngine](../src/Genie.Core/GameStateEngine.cs) maps the icon id to a `CharacterStatus`; [ScriptGlobalsSync](../src/Genie.Core/Scripting/ScriptGlobalsSync.cs) mirrors it to `$webbed`/`$standing`/etc. |
| `<crtrStatus exist='91586721' hostile='0' disengaged='1' flying='1'/>` | `CreatureStatusEvent(exist, hostile, disengaged, flying)` | Per-creature combat status, keyed by exist id (public #202). Dropped if `exist` is missing. |
| `<dialogData id='injuries'>` + `<image id='rightLeg' name='Injury2'/>` | `InjuryEvent(area, kind, severity)` | Inside the injuries dialog, each `<image>` reads one body region: `Injury<N>` = wound, `Scar<N>` = scar, `Nsys<N>` = nerve damage, name echoing the region id = healthy. Severity 1–3. Outside the injuries dialog, `<image>` is UI layout only and dropped. |

### Session lifecycle

| Tag | Event | Notes |
| --- | --- | --- |
| `<endSetup/>` | `EndSetupEvent` | End of the settings burst. |
| `<settingsInfo/>` | `SettingsInfoEvent` | Authoritative "ready for input" signal. `GenieCore` sends `look` once on this. See [SGE_PROTOCOL.md](SGE_PROTOCOL.md#after-auth). |
| `<app char='Renucci' game='DR' title='…'/>` | `AppEvent(Character, Game, Title)` | The server's authoritative session identity — `game` is the instance code (DR/DRX/DRF/DRT) community scripts branch on via `$game`. Only fires when `char` is present; the settings-dump form `<app maximized='t'/>` stays ignored. |

### Text-derived events (no tag)

A few events are parsed out of plain game text rather than a tag:

| Source | Event | Notes |
| --- | --- | --- |
| `info` output (`Guild: X` line) | `GuildEvent(guild)` | DR doesn't push guild in a structured tag; fires only when the player runs `info`. |
| Lich ident reply | `CharacterNameEvent(name)` | Character name learned mid-session on a Lich-proxy attach (public #127), where the client never sees `<app char=…/>`. |
| Connect-time silent `flags` probe | `FlagsReportEvent(flags)` | Flag name → ON/OFF; the probe's output is suppressed from display, and GenieCore warns if a stream-affecting flag deviates from the verified baseline. See [DR_FLAGS.md](DR_FLAGS.md). |

### Anything unrecognised

Unknown tags emit an `UnknownTagEvent(name, rawXml)` and are logged at trace level. `GameStateEngine` logs these at debug — useful when DR introduces a tag we don't yet handle, and a feed for AI-training analysis.

## Quirks the parser handles

- **Mid-tag TCP splits.** A tag cut in half across socket reads is left in `_rawBuffer` until its `>` arrives. No carry gymnastics — the incomplete tag simply isn't parsed yet.
- **`XmlReader`-hostile tags.** Bare `<d>` and friends fall through to the regex attribute scraper rather than being dropped.
- **Leading whitespace preservation.** Required for `info`/`exp` column alignment; only trailing whitespace is trimmed.
- **Inline-tag line fragmentation.** Buffered and reassembled in `_textLineBuffer`, flushed at `\n` and logical boundaries.
- **Inventory bleed.** `<inv>` content is implicitly re-routed to the `inv` stream so it doesn't appear in the main window.
- **Server-merged responses.** The server sometimes concatenates a game response directly onto a `<left>`/`<right>`, `<spell>`, or `<inv>` body with no separator. `FindMergeSeam` splits at the first lower→upper adjacency (item/spell names always break that adjacency with a space or apostrophe) and re-emits the appended text so it isn't silently lost.
- **Stream desync recovery.** A stray `<popStream/>` on an empty stack resets the active stream to `main`; `<prompt>` does the same as a message-boundary backstop, so a lost pop can't strand all later main text on a side stream.

## Diagnostics

- **Session Recorder** (**File → Record Session (raw XML)**) captures the verbatim raw stream to disk — the fastest way to inspect exactly what the server sent. Replay it through the engine via DevReplay mode (see [DevReplayServer](../src/Genie.Core/DevReplayServer.cs)).
- **Trace logging** on `DrXmlParser` surfaces every unknown tag.

## Code references

- **[DrXmlParser.cs](../src/Genie.Core/DrXmlParser.cs)** — the scanner, tag dispatch, span tracking, fallback attribute parser.
- **[GameEvents.cs](../src/Genie.Core/GameEvents.cs)** — the `GameEvent` record hierarchy every consumer matches on.
- **[GameConnection.cs](../src/Genie.Core/GameConnection.cs)** — socket, chunking, `RawXmlStream`, FE handshake.
- **[GameStateEngine.cs](../src/Genie.Core/GameStateEngine.cs)** — turns events into the live `GameState` snapshot.
- **[GenieCore.cs](../src/Genie.Core/GenieCore.cs)** — wires the parser to every consumer.
