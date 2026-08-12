# Playing DragonRealms on iPhone with Genie 5 — options

*Exploration doc. Status: research / no code committed. Written 2026-07.*

This note maps out the ways a player could use an iPhone to play DragonRealms
through Genie 5, from "works today, zero code" to "first-party native app."
It is grounded in the current architecture, not a wish-list — each option is
scored against how much of the existing codebase it reuses, what has to be
built or replaced, and whether it fits the project's
[DR policy stance](POLICY.md).

## What makes this a non-trivial question

Two facts about the codebase shape every option below:

1. **`Genie.Core` is already portable.** It's a pure .NET 10 class library
   with **zero UI dependencies** (see [README architecture](../README.md) and
   `src/Genie.Core/Genie.Core.csproj`). Its package deps — `Jint`,
   `System.Reactive`, `Anthropic.SDK`, `Microsoft.Extensions.Logging` — are
   all managed and cross-platform. Connection, the DR XML parser, game state,
   the `.cmd`/`.js` script engines, and every rules engine live here and would
   move to iOS essentially unchanged. This clean Core/App split is the single
   biggest asset for any mobile effort.

2. **`Genie.App` is desktop-bound in several concrete ways.** It targets
   `net10.0` / `WinExe` and leans on pieces that don't exist or don't fit on
   iPhone:
   - **Dock.Avalonia** — the dockable multi-panel MDI layout. A dense,
     drag-to-dock desktop paradigm; wrong model for a 6-inch touch screen.
   - **Velopack** — the in-app updater. Irrelevant on iOS (the App Store or a
     sideload channel handles updates).
   - **`org.k2fsa.sherpa.onnx` + `PortAudioSharp2`** — native TTS and PCM
     audio output. PortAudio in particular is a desktop native lib; iOS audio
     goes through AVAudioEngine/CoreAudio.
   - **SkiaSharp** rendering (via Avalonia) — this one *does* work on iOS.

Also: DR is reached over a **raw TCP connection** to `play.net` (SGE auth +
game stream) or via a **local Lich proxy on `127.0.0.1:8000`**. iOS apps can
open TCP sockets fine; **browsers cannot**, which rules out a pure-web client
without a server-side bridge (see Option 5).

And one policy constraint that pre-empts a whole class of designs:
[POLICY.md §4](POLICY.md) commits the project to **no headless / daemon mode** —
"Genie requires an interactive Avalonia window." Any "run Core as a background
server, drive it from the phone" design (Options 4 and 5) collides with that
commitment and would need a maintainer policy decision *before* code, not after.

---

## Option 1 — Remote-desktop into a running Genie (available today, zero code)

Run Genie 5 on a Mac / PC / Linux box and reach it from the iPhone with a
remote-desktop app (Jump Desktop, Screens, Microsoft Remote Desktop, VNC,
Steam Link, Moonlight, etc.).

- **Effort:** none. Works with today's release.
- **Reuse:** 100% — it *is* Genie, every feature intact (mapper, panels,
  scripts, TTS on the host).
- **Cons:** needs an always-on host machine; the dense docking UI is rough
  under touch and at phone resolution; input latency; you're really operating
  a desktop, not using a phone app.
- **Policy:** neutral — it's just screen-sharing an attended session.
- **Verdict:** the honest "how do I do this *right now*" answer. Worth a short
  wiki page (recommended host settings, on-screen-keyboard tips, a Bluetooth
  keyboard note). It is not a product, but it unblocks users immediately.

## Option 2 — Native iOS port via Avalonia.iOS (the first-party path)

Avalonia has first-class iOS support: the `Avalonia.iOS` package targets
.NET 8+, and Avalonia 12 added **touch-first navigation primitives** — drawer
navigation, tab bars, bottom sheets, native gesture handling — that map
directly onto the reflow this UI needs. So the framework is not the blocker;
the **UI paradigm and the desktop-only dependencies** are.

What a port actually involves:

- **Reuse `Genie.Core` wholesale.** This is the win — the connection stack,
  parser, game state, and script engines come along for free.
- **Rebuild the shell for touch.** Replace the Dock.Avalonia MDI layout with
  a mobile navigation model: the game stream as the primary view, panels
  (Room, Vitals, Inventory, Mapper, Experience) as a tab bar / bottom-sheet /
  swipe-drawer set rather than draggable docks. This is the bulk of the work
  and effectively a second front-end, though it can share view-models.
- **Swap desktop-only deps:** drop Velopack; replace `PortAudioSharp2` +
  `sherpa-onnx` with an iOS audio path (or ship without offline TTS at v1 and
  use `AVSpeechSynthesizer`). Keep SkiaSharp — it renders on iOS.
- **Input:** on-screen command bar + iOS keyboard; the tap-a-`<d>`-link model
  the desktop already uses translates naturally to touch and is arguably
  *better* on a phone than a mouse.
- **Distribution is the real friction, not the code.** Genie 5 is
  **GPL-3.0**, and the GPL's free-redistribution terms are in long-standing
  conflict with the App Store's usage rules — the precedent is VLC being
  pulled from the App Store over exactly this. Realistic channels:
  - **TestFlight** for alpha testers (fits the project's current alpha stage);
  - **AltStore / sideload** for a GPL-clean distribution;
  - a **relicensing / additional-permission grant** if App Store distribution
    is ever wanted (a deliberate maintainer decision, not a code task).
- **Effort:** large. New front-end project, dependency surgery, Apple
  developer account + signing, distribution decision.
- **Verdict:** the only path that yields an actual "Genie on iPhone" app, and
  the clean Core makes it *tractable* rather than a rewrite. Best sequenced as
  **iPad-first** (see note below) with the phone reflow as a follow-on.

## Option 3 — iPad-first as the pragmatic middle step

Not strictly "iPhone," but worth stating because it de-risks Option 2. The
docking, multi-panel UI is far closer to usable on an iPad's screen than a
phone's, and iPad shares the exact same `Avalonia.iOS` target and the same
`Genie.Core`. An iPad build could keep something much nearer the desktop
layout, ship sooner, and prove out the Core-on-iOS + audio + distribution
questions before investing in a full phone-scale reflow.

- **Effort:** medium (a subset of Option 2 — less UI reinvention).
- **Verdict:** if a native Apple target is on the table at all, this is the
  lowest-risk first increment. Recommended staging for Option 2.

## Option 4 — Companion thin-client over LAN (Core-as-server + phone front-end)

Because `Genie.Core` is UI-free (and already builds as an `Exe`), one could
run it as a local service that exposes the game stream + accepts input over a
WebSocket, with a lightweight iPhone front-end (native SwiftUI, or a small
Avalonia.iOS view) rendering the stream and sending commands.

- **Reuse:** high on the Core side; the phone app is a thin renderer.
- **Cons / blockers:**
  - **Policy collision.** This is a headless Core running without the
    interactive Avalonia window that [POLICY.md §4](POLICY.md) explicitly
    rules out. It also edges toward the "operate a session you're not sitting
    at" pattern the policy is wary of. **This needs a maintainer policy
    decision first** — it is not just an engineering choice.
  - Still needs an always-on host on the LAN (same as Option 1).
- **Verdict:** technically elegant given the Core split, but currently
  **off-limits by the project's own stated policy**. Park it behind a
  `policy-question` issue rather than prototyping.

## Option 5 — Web / PWA front-end (home-screen app, no App Store)

A browser-based UI added to the iPhone home screen as a PWA sidesteps both the
App Store and the GPL friction. Avalonia can target WebAssembly, or a small
hand-built web UI could render the stream.

- **Hard blocker:** browsers **cannot open raw TCP** to `play.net`. This
  option *requires* a server-side bridge that terminates the SGE/TCP
  connection and relays over WebSocket — i.e. it inherits **all of Option 4's
  headless-Core policy problem**, plus a network hop.
- **Verdict:** the PWA packaging is attractive, but it doesn't remove the
  server-bridge requirement, so it lands under the same policy gate as
  Option 4. Not viable until that gate is resolved.

## Option 6 — Generic iOS MUD client + Lich (bare-bones fallback)

Run [Lich 5](https://github.com/elanthia-online/lich-5) as a proxy on a host
and point an existing iOS MUD/telnet client at it.

- **Cons:** DR speaks the StormFront/Wizard **XML** protocol, not clean
  telnet. A generic client renders the markup as garbage unless Lich's
  plain-text front-end emulation strips it down — and even then you lose
  *everything* that makes Genie Genie: the mapper, the panels, the scripting
  UI, highlights, presets.
- **Verdict:** a degraded "just get text on the screen" fallback, unrelated to
  Genie the product. Mention for completeness only.

---

## Comparison

| Option | Effort | Core reuse | New UI | Native app? | Policy | Notes |
|---|---|---|---|---|---|---|
| 1. Remote desktop | none | full (it's Genie) | none | no | ok | Works today |
| 2. Avalonia.iOS port | large | `Genie.Core` as-is | full touch reflow | **yes** | ok | Distribution (GPL/App Store) is the friction |
| 3. iPad-first | medium | `Genie.Core` as-is | light reflow | yes | ok | De-risks Option 2 |
| 4. Core-as-server + phone client | medium | high | thin renderer | yes | **blocked** | Headless Core vs. POLICY §4 |
| 5. Web / PWA | medium | via server bridge | web UI | PWA | **blocked** | Browser can't do raw TCP → needs the same bridge |
| 6. Generic MUD client + Lich | low | none | none | no | ok | Loses all Genie features |

## Recommendation

1. **Now:** document **Option 1 (remote desktop)** in the wiki as the
   supported "play from your phone today" answer. Zero engineering, unblocks
   users immediately.
2. **If a first-party Apple target is wanted:** pursue **Option 2**, staged
   **iPad-first (Option 3)**. The project already paid the hard architectural
   cost — a UI-free `Genie.Core` — so the port is a *new front-end*, not a
   rewrite. Start with a spike that boots `Genie.Core` on `Avalonia.iOS`,
   connects to DR, and renders the raw stream in a single scrolling view; that
   proves the Core, the socket, and the audio/distribution questions before
   any panel-reflow investment.
3. **Settle two decisions before writing port code** — they gate everything
   and aren't engineering problems:
   - **Distribution:** TestFlight vs. AltStore/sideload vs. a GPL
     additional-permission grant for the App Store.
   - **Policy:** whether any server-mediated design (Options 4/5) is ever
     acceptable under [POLICY.md §4](POLICY.md). Until that's answered, treat
     4 and 5 as off the table and don't prototype them.

## Distribution decision (Option 2/3 prerequisite)

If a native Apple target is pursued, **how it reaches users** must be settled
before port code is written — it's a licensing/values call, not engineering.
The three candidates are not competing choices; they sit at different points
on a *beta → permanent → App-Store-legitimate* spectrum, and Genie 5's
**GPL-3.0** license bites each one differently.

| Path | What it actually is | GPL-3 fit | Reach | Cost / friction | The catch |
|---|---|---|---|---|---|
| **TestFlight** | Apple's *beta* channel | ⚠️ gray — still App-Store-Connect terms | Worldwide, ≤10k external testers | $99/yr dev account; first build per version hits Apple beta review (~24h) | **Builds expire every 90 days** — a testing channel, not a permanent home |
| **AltStore / sideload** | Self-hosted distribution outside Apple | ✅ cleanest — no App Store DRM/usage terms | **Geo-split (see below)** | Notarization (malware scan only, no content review) | AltStore **PAL** is EU/Japan/Brazil-only; the worldwide variant re-signs every **7 days** and needs a desktop running AltServer |
| **GPL exception / relicense** | Legally unblock the real App Store | ✅ makes App Store legitimate | Worldwide, everyone | Must obtain **every copyright holder's** consent (GPLv3 §7) | Highest effort; conflicts with the deliberate "same license as Lich 5" choice in the README |

**Project-specific nuances:**

- **TestFlight — the 90-day expiry is the story.** Perfect for an *alpha*
  (which Genie 5 is), useless as a permanent channel: every build dies at 90
  days, so you re-upload forever and testers re-download. Nobody gets pulled
  from *TestFlight* over GPL the way VLC was pulled from the App Store, so for
  a small tester pool the license tension is a tolerable gray area.
- **AltStore — geography is the catch, and it matters here.** The DR
  playerbase is largely **US-based**, where the DMA doesn't apply — so the
  clean, malware-scan-only **AltStore PAL** marketplace (EU/Japan/Brazil, with
  Australia/UK following) is *not available*. For the actual audience,
  "AltStore" means the **worldwide classic-sideload path**: 7-day re-sign,
  3-app-per-device limit, a desktop AltServer to refresh. A heavy ask for
  casual users — but this community already runs Lich and writes `.cmd`
  scripts, so it's within reach. GPL-wise it's the cleanest fit.
- **GPL exception — tractable *now*, harder every merge.** GPLv3 §7 lets you
  add an "App Store distribution" permission, but only copyright holders can
  grant it — meaning **every contributor** must agree (or sign a CLA). Genie 5
  is effectively solo-maintained today (@monil2233 holds the signing roles),
  so there are few/no external copyright holders to chase — **the easiest this
  will ever be.** VLC solved the equivalent problem by relicensing its engine
  to LGPL, but that works because VLC is library-shaped; for a whole GPL app
  the normal move is the added exception. Note the README chose GPL-3
  deliberately to align with Lich 5, so this is a values decision too.

**Recommended phasing** (you likely never need all three):

1. **Alpha:** **TestFlight** — matches the project's stage, worldwide reach,
   lowest friction; accept the 90-day churn as the cost of a beta.
2. **Permanent GPL-clean release:** **sideload / AltStore**, documented for the
   technical DR crowd who can tolerate the 7-day resign. The honest "GPL app,
   no App Store" answer.
3. **Real App Store presence (the GPL exception):** only if frictionless,
   one-tap install for *non-technical* users ever becomes a goal.

**The question that decides it:** *is a frictionless App Store install ever a
goal, or is "technical DR players who already run Lich" the whole audience?*
If the latter, TestFlight-for-alpha + sideload-for-release covers everything
and the GPL headache is avoidable entirely. If the former, begin collecting
contributor consent / adopt a **CLA now** — while the contributor list is tiny
— because every merged external-contributor PR raises the cost of ever
relicensing.

## Sources

- [Avalonia — Supported Platforms](https://docs.avaloniaui.net/docs/overview/supported-platforms)
- [Avalonia UI for Mobile (iOS & Android)](https://avaloniaui.net/avalonia/mobile)
- [Avalonia.iOS on NuGet](https://www.nuget.org/packages/Avalonia.iOS)
- [FSF — VLC and App Store GPL enforcement](https://www.fsf.org/blogs/licensing/vlc-enforcement)
- [Apple — TestFlight overview](https://developer.apple.com/help/app-store-connect/test-a-beta-version/testflight-overview/)
- [AltStore PAL — FAQ](https://faq.altstore.io/altstore-pal/what-is-altstore-pal)
- [TechCrunch — alternative EU app stores](https://techcrunch.com/2026/02/22/move-over-apple-meet-the-alternative-app-stores-available-in-the-eu-and-elsewhere/)
- [App Fair — The GPL and Commercial App Stores](https://appfair.org/blog/gpl-and-the-app-stores/)
- Genie 5 internals: [README architecture](../README.md), [POLICY.md](POLICY.md),
  `src/Genie.Core/Genie.Core.csproj`, `src/Genie.App/Genie.App.csproj`
