# Plugins

Plugins extend Genie 5 with compiled .NET code — drop a plugin in your `Plugins/` folder and load it, no rebuild required. The first shipped plugin is an **Experience tracker** (a port of Genie 4's `EXPTracker`), built on the same contract so it doubles as the proving ground for the plugin API.

> 🚧 A one-click **plugin marketplace** with ratings and signed packages is on the roadmap. Today you load plugins from disk and trust them by curation (see [Trust model](#trust-model)).

## Loading a plugin

- Place a plugin DLL in `{AppData}/Genie5/Plugins/` (see [Application Folders](Application-Folders)).
- Manage it from the command bar:

```
#plugin load <name>     # load a plugin
#plugin unload <name>   # unload it
#plugin list            # show loaded plugins
```

Each plugin loads in its own isolated assembly context, so it can be enabled, disabled, and unloaded cleanly without restarting Genie.

## The Experience tracker

The bundled experience plugin watches the live experience stream and your `exp` output, tracks each skill's rank and mindstate, and renders a formatted panel — sortable, grouped by category (Armor / Weapon / Magic / Survival / Lore), with a "Learning: N" counter, session gain, and TDPs. It demonstrates the whole plugin surface: reading the raw stream, reading parsed text, and emitting to its own window.

## How plugins work (for developers)

The plugin contract lives in a small, **UI-free** library (`Genie.Plugins.Abstractions`) so a plugin DLL can reference it without dragging in any Avalonia/UI types. A plugin implements `IGeniePlugin`; the host hands it an `IPluginHost`.

### `IGeniePlugin`

Identity (`Id`, `Name`, `Version`, `Author`, `Description`, `MinHostVersion`), an `Enabled` flag, lifecycle (`Initialize` / `Shutdown`), and hooks:

- **Transform hooks** — `OnGameText(text, stream)`, `OnInput(input)`, and `OnEcho(text, window)` return modified text, or `null` to gag/swallow. Plugins chain in load order, and the transforms are honored end-to-end:
  - `OnGameText` runs **first** in the per-line pipeline (Genie 4's order — plugins before triggers), and what it returns is what scripts, triggers, and every window see; `null` suppresses the line for all of them. `#parse`-injected lines flow through it the same way (a Genie 5 upgrade — Genie 4 fed `#parse` to plugins observe-only). Game state, the mapper, and the built-in trackers read the raw server events, so a plugin controls what's *seen*, not what *happened*; a rewritten line loses its link/bold/preset styling.
  - `OnEcho` is a Genie 5 extension — Genie 4 never ran echoed lines through plugins. It sees `#echo` output, script `echo` lines, and system messages, with the target window name — and has a default pass-through implementation, so plugins built before it exist keep loading unchanged.
- **Observation hooks** — `OnXml(fragment)`, `OnCommandSent(command)`, `OnPrompt()`, `OnVariableChanged(name, value)`.

### `IPluginHost`

What a plugin is allowed to do:

- **Output** — `Echo(text)` to the main window, `EchoToWindow(window, text)` to a **named panel** (the app surfaces unknown window names as dock panels — this is how plugins stay UI-agnostic), and `SendCommand(command)` to the game (policy-gated).
- **Variables** — read/write the same variable store scripts use.
- **State** — `IGameStateView`, a **read-only** projection of game state (vitals, room, hands, skills) so plugins observe without mutating. This preserves Genie's one-way data flow.
- **Diagnostics** — `Log(message)`.

`MinHostVersion` (declared by the plugin) and the host's interface version let the host refuse a plugin built against an incompatible contract.

## Trust model

.NET has no real in-process sandbox — a loaded assembly runs at full trust. Genie is honest about this: trust comes from **curation, signing, and API-surface linting**, not a hard security boundary. The roadmap adds load-time linting (flagging things like raw process/socket access or attempts to reach into host internals), a signing/consent flow for unsigned plugins, and a curated source.

Crucially, the **policy gates are not negotiable** and the host API simply doesn't expose the forbidden paths: a plugin cannot enable headless mode, feed text into the command pipeline to drive the game agentively, or trigger reconnection (reconnect is host behavior, attendance-gated, and outside the plugin API). See [Policy Compliance](Policy-Compliance).

## Porting a Genie 4 plugin

Old Genie 4 plugin DLLs won't load directly — they're WinForms/Windows-only. The interface shape was kept deliberately familiar (transform hooks, an interface version, a host with echo/send/variable access) to ease recompiling against the Genie 5 contract. A `dotnet new genie-plugin` template and the published contract assembly are planned.

## Roadmap

- 🚧 Plugins menu (enable/disable/settings/reload UI), plugin marketplace, signing/trust UX, an SDK + project template, and a Genie 4 → Genie 5 porting guide.

## Related

- [Configuration & Rules](Configuration) — rule engines cover many needs without a plugin.
- [Architecture](Architecture) — where plugins sit in the pipeline and why `Genie.Core` is UI-free.
- [Keeping Up to Date](Updates) — plugin updates.
