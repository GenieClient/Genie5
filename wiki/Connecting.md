# Connecting & Profiles

Genie 5 reaches DragonRealms three ways, all chosen from **File → Connect…**. Most players use the first one (a normal account login); the other two exist for Lich users and for development.

| Mode | Use it when |
| --- | --- |
| **Simutronics SGE login** | You want Genie to log you in with your DragonRealms account — the normal case. |
| **Lich proxy** | You run [Lich 5](https://github.com/elanthia-online/lich-5) and want Genie connected behind it. See [Lich 5 Integration](Lich-5-Integration). |
| **Dev replay** | You're replaying a recorded session through the engine (development/testing). |

## The Connect dialog

1. **File → Connect…**
2. Enter your **account name** and **password**.
3. Choose the **game** (DragonRealms — Prime, Platinum, Fallen, or the Test instance, depending on your account).
4. Click **Fetch** to pull your **character list** from Simutronics' login service.
5. Select a **character** and click **Connect**.

Genie authenticates, receives the game host/port for your character, connects, and starts streaming the game. When the server signals it's ready, Genie sends an initial `look` so you land with your room shown.

### How the login works (and what it never does)

Genie performs the Simutronics **SGE** handshake itself: it exchanges an encryption key, sends your encrypted password, retrieves your characters, and selects the game server. Your password is sent **only** to Simutronics' official authentication servers (`play.net`), encrypted in transit by the SGE scheme.

- **No auto-reconnect.** If the connection drops, Genie surfaces it and stops — you reconnect by hand. This is deliberate (see [Policy Compliance](Policy-Compliance)).
- **No headless login.** Connecting always happens in the visible app.

The wire-level details are documented for developers in [SGE_PROTOCOL.md](https://github.com/GenieClient/Genie5/blob/main/docs/SGE_PROTOCOL.md).

### Secure (TLS) login — the padlock

Genie logs in over an **encrypted TLS connection** by default (the same secure
login Lich 5 uses). You can tell which transport a session used at a glance:

![Title bar showing the 🔒 padlock next to the connected Character-Account name](images/connect-padlock.png)

| Indicator | Meaning |
| --- | --- |
| 🔒 in the title bar + *"Connected over TLS (encrypted)"* | Your login was encrypted end-to-end — the normal, preferred case. |
| 🔓 + *"login was obfuscated, not encrypted"* | Genie couldn't reach the secure endpoint and fell back to the legacy plaintext login. It still works, but the password is only lightly obfuscated in transit. |

The fallback is automatic — if the secure endpoint is blocked (by a firewall or
network filter) Genie won't fail the login, it just drops to the older path and
shows the 🔓 so you know. Seeing 🔓 every time usually means something on your
network is blocking the secure port; the connection trace below can confirm it.

## Profiles

A **profile** is a saved connection so you don't retype next time. Save one from the Connect dialog; it stores the account, game, character, and (optionally) the password.

- **Passwords are encrypted.** If you save a password, it's stored with **AES-256-GCM** using a machine-bound key — never plain text, never the weak obfuscation older clients used. If you'd rather not store it, save the profile without a password and type it at connect time.
- **Per-character settings.** Each character gets its own profile directory (`Profiles/<Character>-<Account>/`) holding that character's aliases, triggers, highlights, and so on. Your combat triggers on one character don't follow you onto a shopping alt. See [Application Folders](Application-Folders) for the layout.
- **Character-Account naming.** Characters are identified as `Character-Account` (for example `Renucci-MONIL`) so two accounts with a same-named character never collide.

## Lich proxy mode

If you run Lich 5, start Lich first so it authenticates and listens locally, then in Genie's Connect dialog choose **Lich Proxy** and point it at `127.0.0.1:8000` (Lich's default). Genie sees a clean game stream and your Ruby scripts keep running underneath. Full details: [Lich 5 Integration](Lich-5-Integration).

## Dev replay mode

Replay mode feeds a previously **recorded** raw-XML session back through the engine — handy for trying things without a live login, and the backbone of parser testing. Recording and replay terminology (App vs. Console, what "replay" means) is covered in [Building from Source](Building-from-Source). Replay **only reads** a recording; it never sends anything to a live server.

## Recording a session

**File → Record Session** captures the current live session's raw stream to your `Logs/` folder; the title bar shows a 🔴 REC indicator while it's on. Recording is **off by default** and writes only to your machine — it never leaves your computer. It's useful for filing precise bug reports (attach a snippet to an issue).

## Connection troubleshooting

| Message / symptom | Meaning |
| --- | --- |
| "already logged in" | That character is still in game elsewhere — disconnect the other session first. |
| "billing problem" | The account's subscription/billing needs attention on play.net. |
| "in game" / "unavailable" | The character or game instance is temporarily not selectable; try again shortly. |
| Fetch returns no characters | Wrong account name or password, or the account has no DragonRealms characters. |
| Connect hangs then fails | Network/firewall blocking `play.net`, or the game is down for maintenance. Genie retries the **initial** connect a few times, then stops. |

Genie translates the server's raw `PROBLEM` codes into the friendly reasons above. More in [Troubleshooting & FAQ](Troubleshooting).

**Capturing a connection trace.** If a login hangs or you're stuck on the 🔓
plaintext fallback and want to know why, turn on the step-by-step connect trace:

```
#config conndebug true
```

The next connect prints each protocol step (TLS handshake, key exchange, auth,
character list, game select) with timings straight into the game window, so a
stall can be pinned to an exact step — ideal to paste into a bug report. It's
**off by default**; turn it back off with `#config conndebug false`.

When Genie auto-launches Lich (owns the process), you can also mirror that
session's Lich `temp/debug-*.log` into the game window as `[lich-debug]` lines:

```
#config lichdebug true
```

`conndebug` and `lichdebug` are independent — enable both for a full Lich-proxy
connection diagnosis. See [Lich 5 Integration](Lich-5-Integration).

## Related

- [Quick Start](Quick-Start) — the condensed first-login walkthrough.
- [The Interface](The-Interface) — what you see once you're connected.
- [Application Folders](Application-Folders) — where profiles and encrypted passwords live.
