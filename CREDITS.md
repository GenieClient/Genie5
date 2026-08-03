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

## Game

[DragonRealms](https://www.play.net/dr) is a Simutronics text MMO.
Genie 5 is an independent client — not affiliated with or endorsed
by Simutronics Corp.
