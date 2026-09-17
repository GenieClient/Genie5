# Credits

Genie 5 is built by the Genie 5 Team and contributors. This file tracks
non-code contributions (artwork, sounds, original designs) that ship as
part of the project's redistributable assets.

For source-code contributions, see the project's git history
(`git log` / GitHub's contributor view).

---

## Support partner

Genie 5 is maintained by the Genie community and open-source contributors.
**Shadow Realms LLC** is the project's support partner: it sponsors the
project's code-signing infrastructure, and Windows release binaries are EV
code-signed under its name (GlobalSign Extended Validation certificate via
[SignPath.io](https://signpath.io/)) — certificates can only be issued to a
registered legal entity, which the community project itself is not.

---

## Art & icon set

**App icon, status indicators, compass-direction glyphs** — [@dylb0t](https://github.com/dylb0t).

Set covers:

- `src/Genie.App/Assets/app.ico` — Windows EXE icon (taskbar + installer)
- `src/Genie.App/Assets/app.icns` — macOS `.app` bundle icon
- `src/Genie.App/Assets/app.png` — Linux + Avalonia `Window.Icon` source
- `src/Genie.App/Assets/Genie5_iconset/` — source iconset (Apple
  `iconutil` layout, all sizes including @2x retina variants) — kept
  for future regeneration
- `src/Genie.App/Assets/Icons/*.png` — 11 status-indicator glyphs
  (bleeding / dead / hidden / invisible / joined / kneeling / prone /
  sitting / standing / stunned / webbed) + 12 compass icons (8 cardinal
  + up / down / out + center rose)

Donated by the author for inclusion in the GenieClient/Genie5 public
distribution under the same **GPL-3.0** license as the rest of the
project. Attribution requested.

If you redistribute Genie 5 (or a derivative work) and want to swap or
extend the icon set, please retain credit to @dylb0t for any of the
original glyphs you keep.

---

## Genie 4 lineage

Genie 5 is the cross-platform successor to
[Genie 4](https://github.com/GenieClient/Genie4), the long-running
Windows client for DragonRealms. Architectural decisions, the `.cmd`
script dialect we stay backwards-compatible with, the rules-engine
vocabulary (`#alias` / `#trigger` / `#highlight` / `#substitute` /
`#gag` / `#macro` / `#var` / `#class`), and the `.xml` zone-map format
all trace back to that codebase. Without the Genie 4 community's
decade-plus of work there would be no Genie 5 to write.

---

## Ported Genie 4 features

Several Genie 5 built-ins reimplement the behaviour of Genie 4 plugins. Where
the original source exists it was ported; where only a DLL survives, the
behaviour was recovered by decompiling it and then **reimplemented** — no
decompiled code ships in Genie 5. Script-variable names are preserved exactly,
so community `.cmd` scripts that read them keep working.

The original authors, with thanks:

- **Time Tracker** — *Barnacus* (Genie 4 Time Tracker v1.9.0). Source no longer
  exists; behaviour recovered from the shipped DLL. Genie 5's built-in keeps the
  calculator model, the rise/set calibration, and the `Time.*` script variables.
- **Circle Calculator** — *VTCifer*
  ([Plugin_CircleCalculator](https://github.com/GenieClient/Plugin_CircleCalculator)
  v4.0.6b). Ported from the author's published source, including the circle
  requirement tables.
- **Alteration Buddy** — *Djordje*
  ([mj-colonel-panic/AlterationBuddy](https://github.com/mj-colonel-panic/AlterationBuddy),
  GPL-3.0), source provided by the author for this purpose. Ported as the
  Alterations menu.
- **SimuCoins** — *Thires* ([Thires/SimuCoins](https://github.com/Thires/SimuCoins)
  v2.1.2, GPL-3.0), rebuilt with the author's blessing. Genie 5's built-in keeps
  the store conversation and the `/sc` / `/sct` / `/sca` command spellings; the
  plugin's own account file is not carried over, because the credentials are
  already in your profile.

Plugins that remain plugins are credited in their own `Plugin_*V5` repositories.

If your Genie 4 plugin's behaviour is reflected here and you are not credited —
or you would rather not be — open an issue and it will be corrected.

---

## Game

[DragonRealms](https://www.play.net/dr) is a Simutronics text MMO.
Genie 5 is an independent client — not affiliated with or endorsed
by Simutronics Corp.
