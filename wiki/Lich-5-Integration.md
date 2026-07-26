# Lich 5 Integration

[Lich 5](https://github.com/elanthia-online/lich-5) is a Ruby proxy engine for Simutronics games. It runs *on top of* a front-end client like Genie — it is not a competing client. Genie 5 is designed to work cleanly behind Lich, so your Lich scripts and Genie's own features coexist.

> Genie 5 and Lich 5 are both **GPL-3.0**, deliberately aligning Genie with the broader DragonRealms tooling ecosystem.

## Two ways to combine them

### 1. Lich proxy mode

Lich authenticates and connects to DragonRealms, then exposes a local game stream that Genie connects to.

1. **Start Lich 5** the way you normally do, so it logs in and listens locally (its default is `127.0.0.1:8000`).
2. In Genie, **File → Connect…** and choose **Lich Proxy**.
3. Point it at Lich's host/port (`127.0.0.1:8000` by default) and connect.

Genie receives a clean DragonRealms stream and renders it normally. Your **Lich Ruby scripts keep running** underneath — Genie simply sees their output as ordinary game text. You get Lich's automation plus Genie's UI, mapper, highlights, and `.cmd` scripts at the same time.

### 2. Direct login with Lich alongside

Genie can handle authentication itself (no Lich required) while you still run Lich-managed automation in parallel through Lich's own command channel. Use this when you want Genie to own the connection but still lean on specific Lich scripts.

## Three script ecosystems, side by side

Genie 5 is built so all three of DragonRealms' scripting worlds coexist:

| Ecosystem | Language | How Genie sees it |
| --- | --- | --- |
| **Native scripts** | Genie `.cmd` (Wizard dialect) | Run directly by Genie's [script engine](Scripting). |
| **Lich scripts** | Ruby (`.rb`) | Run by Lich behind the proxy; transparent to Genie. |
| **Plugins** 🚧 | .NET DLLs | Loaded by Genie's [plugin host](Plugins). |

You can mix them: a Lich script can be doing one thing while a Genie `.cmd` script and a highlight rule do others.

## Notes & current limits

- **Lich launch: manual by default, auto-launch opt-in.** Out of the box, start Lich first, then connect Genie to it. If you'd rather Genie start Lich for you, turn on auto-launch: set `#config lichpath {path-to-lich.rbw}` and `#config lichautolaunch on`, and Genie will launch Lich before a Lich-proxy connect (it's idempotent — if Lich is already up, Genie just connects). Genie 4's `#lc` / `#lconnect` shortcuts work too, and `#ls` dumps the current Lich settings.
- **Dynamic `lichargs`.** Auto-launch expands `{character}` and `{port}` in `#config lichargs` from the Lich-proxy profile's Character field and proxy port at connect time. Nested braces are fine (Genie’s `{…}` config grouping allows them):

  ```text
  #config lichpath {/path/to/lich.rbw}
  #config lichargs {--login {character} --dragonrealms --genie --headless {port}}
  #config lichautolaunch on
  ```

  Switch characters by changing the profile Character — Genie stops the Lich it auto-launched before starting the new one. If `{character}` is present but Character is empty, Genie aborts the connect with a clear error.

  Arguments split on whitespace, so **quote any value containing a space** — single or double quotes both work, and the quotes are stripped before Lich sees the argument:

  ```text
  #config lichargs {--temp="/Users/me/Library/Application Support/lich/temp" --genie}
  ```

  Backslashes are literal (so Windows paths like `--temp=C:\lich\temp` work as-is); there is no escape character. A quote you never close aborts the connect with `[lich] unterminated " quote in the Lich arguments…` rather than starting Lich with a mangled argument list.

  **Lich's path flags require the `=` form.** `lich.rbw` matches `--temp=PATH`, `--home=PATH`, `--scripts=PATH`, `--data=PATH` and friends; it does *not* read a space-separated value, and it silently ignores anything it doesn't match. Note that `lich --help` currently advertises `--temp-dir=`, `--script-dir=` and `--data-dir=`, none of which are implemented — use `--temp=`, `--scripts=` and `--data=` instead. Rather than start Lich with an argument it would throw away, Genie aborts the connect and says so:

  ```text
  [lich] Lich ignores '--temp-dir=/tmp/lich' — use --temp=PATH instead (the '=' is
  required, and Lich's --help lists some path flags it doesn't implement).
  ```
- **Auto-launched Lich lifecycle.** When Genie starts Lich (outcome `Launched`), it owns that process and stops it on manual disconnect, final session end (no auto-reconnect), or character/port change. If Lich was already running and Genie only attached, Genie never kills it. Transient drops that arm auto-reconnect leave the owned Lich up so Genie can reattach.
- **`#config lichdebug` + owned Lich.** With auto-launch ownership and `lichdebug` on, Genie tails that session's Lich `temp/debug-*.log` into the game window as `[lich-debug]` lines (ignores older leftover debug files from prior runs). Genie resolves the directory the same way Lich does — `--temp=PATH`, else `{--home=PATH}/temp`, else the `temp` folder beside `lich.rbw` — and reports `[lich-debug] no debug-*.log in …` if nothing appears there within 15s, rather than watching an empty path in silence. Independent of `#config conndebug` (Genie-side connection trace) — enable both for a full Lich-proxy connection diagnosis.
- **Policy still applies.** Running behind Lich doesn't change DragonRealms' [Scripting Policy](https://elanthipedia.play.net/Policy:Scripting_policy). The responsiveness expectation in [Policy Compliance](Policy-Compliance) applies to whatever automation you run, in either tool.

## Related

- [Connecting & Profiles](Connecting) — choosing Lich Proxy in the Connect dialog.
- [Scripting](Scripting) — Genie's native `.cmd` scripts.
- [Plugins](Plugins) — the .NET plugin ecosystem.
