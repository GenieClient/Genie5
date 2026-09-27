# Text-to-Speech

Genie 5 can read the game aloud using natural neural voices that run **entirely
on your computer** — offline, free, and private. No game text ever leaves your
machine. It's built for blind and low-vision players, and useful for anyone who'd
rather listen than watch the screen.

Everything is **opt-in**: nothing is spoken until you turn it on.

## Quick start

```
#tts install          install the default voice (a one-time ~60 MB download)
#speak hello there     read a line aloud
#tts read on           auto-read whispers, talk, thoughts, and death
```

## Installing voices

Voices are downloaded on demand and stored under your data folder's `Voices`
directory (see [Application Folders](Application-Folders)).

| Command | What it does |
|---|---|
| `#tts install` | Download and select the default voice |
| `#tts install <name>` | Download a specific voice (e.g. `#tts install lessac`) |
| `#tts voices` | List the downloadable voices, which are installed, and the voices already on your computer |
| `#tts use <name>` | Switch the active voice (a downloaded voice or a system voice) |
| `#tts status` | Show the voice folder, installed voices, the active voice, and read-aloud state |

The downloadable voices work identically on Windows, macOS, and Linux.

## Using your computer's own voices

If you already have voices set up for your screen reader or other apps, Genie
can use them. They're listed next to the downloaded voices, with the source in
brackets, for example **Amy (Piper)** and **Microsoft Zira Desktop (system)**.

| Platform | Where system voices come from |
|---|---|
| Windows | SAPI5 voices: Microsoft David and Zira, plus third-party SAPI5 voices such as Ivona 2 |
| macOS | The voices `say` offers (System Settings ▸ Accessibility ▸ Spoken Content) |
| Linux | speech-dispatcher (`spd-say`), or `espeak-ng` when speech-dispatcher isn't installed |

Pick one in the **Text-to-Speech** tab, or by name:

```
#tts voices                 list everything, system voices included
#tts use zira               any unique part of the name works
#tts use Microsoft Zira Desktop
```

The downloaded (Piper) voices stay the default. If the system voice you picked
is later uninstalled, Genie says so once and speaks with a Piper voice instead.

A few limits worth knowing:

- **Windows:** only SAPI5 voices appear. The newer "natural" and OneCore voices
  that Windows 10/11 add for Narrator aren't SAPI5, so they're not listed.
- **Rate** maps onto each engine's own scale: SAPI's −10 to 10, `say` and
  `espeak-ng` words per minute (1 = 175 wpm), and speech-dispatcher's −100 to 100.
  Speeds won't match the Piper voices exactly.
- **Volume** on speech-dispatcher sets its own level from quietest to loudest, so
  it can sound louder than the other engines at the same percentage.
- **Stopping:** `#tts stop` and urgent lines interrupt every engine. On Linux,
  speech-dispatcher can finish the word or sentence it has already started.

## Speaking text

- **`#speak <text>`** (alias **`#say`**) — read a line aloud. Works typed, from
  scripts, and as a trigger action.
- **`#tts stop`** — silence the current line and clear anything queued.

## Rate and volume

| Command | What it does |
|---|---|
| `#tts rate <n>` | Speaking speed — `1` is the voice's natural pace, `0.5`–`3` (e.g. `#tts rate 1.5`) |
| `#tts volume <n>` | Loudness as a percentage, `0`–`100` |

Both apply from the next spoken line — no restart needed — and persist with
your other settings. Run either without a number to see the current value.

## Reading streams aloud

Turn on per-stream read-aloud to have Genie announce game text automatically.

| Command | What it does |
|---|---|
| `#tts read` | Show whether read-aloud is on and which streams are read |
| `#tts read on` / `#tts read off` | Master switch |
| `#tts read <stream>` | Add a stream (e.g. `#tts read combat`) and turn read-aloud on |
| `#tts mute <stream>` | Stop reading a stream |
| `#tts priority <stream> <low\|normal\|high\|default>` | Set a stream's speaking priority — high barges in, low yields; `default` removes the override. Bare `#tts priority` lists them |

Default streams are **whispers, talk, thoughts, death** — the "someone's
talking to me / something important happened" set. (The stream *id* is
`death`, singular, even though the dock window is labeled Deaths.) The chatty
streams (combat, atmospherics) and `main` are left off by default; add them if
you want them.

Urgent lines (whispers, death) are spoken first and can interrupt ongoing
chatter, so you never miss a tell behind a wall of room text.

## Speaking alerts (triggers & highlights)

Any trigger or highlight can speak when it fires — your hand-picked "always
tell me about this" alerts. Add a final *speak* argument: `*` speaks the
matched line, any other text is spoken as-is (triggers expand `$1`-style
capture groups).

```
#trigger add {^(\w+) just arrived} {} {} {} {$1 just arrived}
#highlight add {black-clawed grelkin} {red} {} {string} {} {} {*}
```

The empty `{}` slots are the arguments you're skipping (action/class/sound for
triggers; background, match type, class, and sound for highlights). Spoken
alerts jump the queue and interrupt ordinary read-aloud chatter, and they save
and load with the rest of your triggers and highlights.

## Settings

These persist with your profile (see [Configuration & Rules](Configuration)):

| Setting | Meaning |
|---|---|
| `ttsvoicedir` | Folder holding installed voices (default `Voices`) |
| `ttsvoice` | Selected voice: a downloaded voice's folder name, or `system:<name>` for a system voice (set by `#tts use`) |
| `ttsread` | Master read-aloud on/off |
| `ttsreadstreams` | Comma-separated streams to read aloud |
| `ttsstreampriority` | Per-stream priority overrides, `stream:level` pairs (set by `#tts priority`) |
| `ttsrate` | Speaking speed multiplier, 0.5–3 (default 1) |
| `ttsvolume` | Volume percent, 0–100 (default 100) |

The same settings are also editable in the GUI — the Configuration dialog
(**Edit → Configuration…**) has a **Text-to-Speech** tab.

## Coming next

Content-aware grouping (merging *"X arrived" + "X left"* into one short line)
and a travel mode that announces your journey and stays quiet in between.
