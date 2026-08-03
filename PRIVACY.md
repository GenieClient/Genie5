# Genie 5 — Privacy Policy

_Last updated: 2026-08-03_

Genie 5 is an open-source desktop game client for [DragonRealms](https://www.play.net/dr).
This policy describes what data the software handles and where it goes.

## Summary

**This program will not transfer any information to other networked systems unless
specifically requested by the user or the person installing or operating it.**

The Genie 5 maintainers do not operate any servers, do not collect telemetry, and
receive no data from your use of the software.

## What Genie 5 connects to

Genie 5 makes network connections **only** to:

- **Simutronics' official servers** (`eaccess.play.net`, `*.play.net`,
  `*.simutronics.net`) — for account authentication and live gameplay. This is the
  same destination the official client uses.
- **A local [Lich 5](https://github.com/elanthia-online/lich-5) proxy** (`127.0.0.1`)
  — only if you explicitly select Lich Proxy connection mode.
- **GitHub** (`api.github.com`, `raw.githubusercontent.com`,
  `github.com/GenieClient/*` release assets) — for the built-in updater: checking
  for new app releases and downloading map / plugin / script updates, and for
  fetching TTS voice packs you choose to install. These are **retrieval-only**
  requests: nothing about you, your account, or your session is sent beyond the
  standard HTTP request itself. Startup update checks can be turned off in
  **Help ▸ Update Settings**. (The "report a parser gap" helper also opens a
  pre-filled GitHub issue page in your browser — visible to you before anything
  is posted.)
- **An AI vendor API** — only if you explicitly opt in to AI advisor features (a
  roadmap item, off by default). When enabled, the outgoing context is filtered to
  remove other players' speech (whisper / talk / thoughts / familiar / tells) before
  any request is made.

Genie 5 does not "phone home" to maintainer-operated servers (none exist) and does
not send usage analytics anywhere.

## Data stored on your device

- **Account credentials** — your DragonRealms account name and password, if you save a
  connection profile. Passwords are encrypted at rest with **AES-256-GCM** and stored
  locally under `{AppData}/Genie5/`. They are transmitted only to Simutronics' official
  authentication servers, using the game's standard login handshake.
- **Configuration** — your scripts, rules (`.cfg`), maps, and per-character profile
  data, all stored locally on your device.
- **Session recordings** — only if you use the Session Recorder; these raw-XML captures
  are written to local disk and never uploaded.
- **Session logs** — if you enable logging (`#config autolog` / `#log`), plain-text
  logs of your session are written to your local `Logs/` folder and never uploaded.
- **TTS voice packs** — offline neural voices you install for text-to-speech live
  under your data folder's `Voices/`; speech synthesis runs entirely on-device.
- **Skill history (Analytics)** — the Analytics window records your own character's
  skill table over time (ranks, learning rates, session summaries) to local files under
  `{AppData}/Genie5/Analytics/`. It contains no other players' data and is never
  uploaded. A one-time notice explains this at first connect; disable anytime with
  `#config analytics off` or the panel's Record toggle.

All of this data stays on your machine. Uninstalling Genie 5 and deleting its
`{AppData}/Genie5/` folder removes it.

## Third parties

Genie 5 does not share data with any third party. Your interactions with Simutronics'
servers are governed by [Simutronics' own policies](https://www.play.net/) and the
[DragonRealms Scripting Policy](https://elanthipedia.play.net/Policy:Scripting_policy).
If you enable the optional AI advisor, the AI vendor's terms apply to the filtered
context you choose to send.

## Contact

Questions about this policy can be raised via
[GitHub Issues](https://github.com/GenieClient/Genie5/issues) or the process described
in [SECURITY.md](SECURITY.md).
