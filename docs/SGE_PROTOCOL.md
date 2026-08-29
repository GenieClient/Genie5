# SGE protocol notes

Wire-level documentation for the Simutronics Game Entry (SGE) login flow at
`eaccess.play.net` (TLS on `:7910`, plaintext on `:7900`). Verified against the
[Genie 4 source][genie4] and [Lich 5][lich] and confirmed via live testing on
DragonRealms.

[lich]: https://github.com/elanthia-online/lich-5

[genie4]: https://github.com/GenieClient

If you touch `SgeAuthClient.cs`, read this file first. Small mistakes here
silently break auth in ways that are hard to debug — a missing tab field,
an off-by-one in the password byte transformation, or assuming a field
index that the server occasionally leaves empty, and you get back a
response that *looks* fine until login mysteriously fails.

## Transport

The **same SGE handshake** is served on two ports with different transports.
Genie tries TLS first and falls back to plaintext.

| Port | Transport | Notes |
|---|---|---|
| `eaccess.play.net:7910` | **TLS** (preferred) | The port Lich 5 uses. Wrapped in `SslStream`, peer cert validated against a pinned self-signed certificate. The password still travels the SGE scheme, but inside an encrypted tunnel. |
| `eaccess.play.net:7900` | **plain TCP** (fallback) | No TLS — port 7900 does not respond to a TLS ClientHello, so never wrap *this* port in `SslStream`. The password is only XOR-obfuscated with a key the server sends in the clear. |

- Server sends **nothing** on connect on either port. The client must initiate.
- `ConnectionConfig.UseTls` (default **true**) selects 7910. On any TLS-side
  failure that isn't a credential rejection, `SgeAuthClient` logs the reason
  and retries the whole login on 7900. A bad password does **not** trigger
  fallback (it would just fail again).

### ⚠️ The server accepts exactly ONE cipher suite — Linux/macOS need an explicit offer

Probed 2026-08-29 (`openssl s_client`, suite by suite): 7910 negotiates **only**
`TLS_RSA_WITH_AES_128_GCM_SHA256` — TLS 1.2, static-RSA key exchange, no
forward secrecy. Everything else — every ECDHE/DHE suite, every CBC suite,
AES-256-GCM, all of TLS 1.3 — is answered by **closing the connection with a
bare EOF, no handshake alert**. The client-visible symptom is
`IOException: Received an unexpected EOF or 0 bytes from the transport stream`
roughly one RTT after the ClientHello.

That interacts badly with .NET's platform defaults: **Windows Schannel still
offers static-RSA suites, but .NET on Linux/macOS uses a restricted default
cipher list with only forward-secrecy suites** — so the same build that shows
🔒 on Windows always EOF'd on Linux and silently fell back to plaintext 7900.
Fix: on non-Windows platforms `SgeAuthClient` sets an explicit
`CipherSuitesPolicy` — the modern TLS 1.3/ECDHE suites (so a future server
upgrade negotiates something better with no client change) plus
`TLS_RSA_WITH_AES_128_GCM_SHA256` (what today's server actually takes). The
API throws `PlatformNotSupportedException` on Windows, hence the OS gate.
Losing PFS is acceptable here: the alternative transport is XOR-obfuscated
plaintext, and MITM is defended by the certificate pin, not the key exchange.

### ⚠️ Framing differs between the two ports — this is not cosmetic

This bit silently broke TLS login and is the single most important thing to
know here:

- **Plaintext (7900)** terminates each response with a trailing `\n`.
- **TLS (7910)** is **record-framed and sends responses *without* a trailing
  `\n`.** Each logical response arrives as one (or more) TLS records; there is
  no newline to wait for.

A naïve `StreamReader.ReadLineAsync()` works fine on 7900 but **blocks forever
on 7910** — it waits for an end-of-line byte that never comes, so login appears
to hang and only "recovers" when the server eventually closes the socket
(EOF). The fix is to read raw bytes and stop on **either** a `\n` (plaintext)
**or** a brief read-idle after a record arrives (TLS). See
`SgeAuthClient.ReadLineAsync(stream, useTls, ct)`. Likewise, the 32-byte key in
step 1 is followed by a trailing `\n` **only on plaintext** — the key-read must
not block waiting for that newline over TLS.

> Earlier notes called the two transports "byte-for-byte identical." They are
> not: the *handshake bytes* match, but the *framing* (trailing newlines) does
> not. Treat 7910 as record-framed.

## Handshake sequence

| Step | Client sends | Server responds |
|---|---|---|
| 1 | `K\n` | 32 raw key bytes (+ trailing `\n` over **plaintext only** → 33 bytes; over **TLS** just the 32 bytes, no newline — see Framing above) |
| 2 | `A\t{ACCOUNT_UPPER}\t` + encrypted-password-bytes + `\n` | `A\tMONIL\tKEY\t{hash}\t{full name}` on success; `A\t\tPASSWORD` or `A\t\tUNKNOWN…` on failure |
| 3 | `M\n` | game list |
| 4 | `G\tDR\n` | game selected (read and discard the `PREMIUM`/`FREE_TO_PLAY` + URL fields) |
| 5 | `C\n` | `C\t{count}\t{?}\t{?}\t{?}\t{CODE}\t{Name}\t{CODE}\t{Name}\t…` |
| 6 | `L\t{code}\tSTORM\n` | `KEY=…`, `GAMEHOST=…`, `GAMEPORT=…` (game server: `dr.simutronics.net:11024`) |

## Password encoding (from `Genie4/Utility/Utility.cs EncryptText`)

For each byte `i` of the password:

```
encryptedByte[i] = ((password[i] - 32) XOR keyByte[i]) + 32
```

- `keyByte[i]` is used **raw** — do NOT subtract 32 from it.
- Result is sent as **raw bytes** directly in the stream (not hex-encoded).
- The auth message is built as a byte array, not a string:
  `"A\t{ACCOUNT_UPPER}\t"` + raw encrypted bytes + `\n`.

## Response-parsing gotchas

### Character list field layout

```
C\t{count}\t{?}\t{?}\t{?}\t{CODE}\t{Name}\t{CODE}\t{Name}\t…
```

Character data starts at tab-field index 5, alternating code/name pairs.
The format is **not** `CODE=Name` despite some older docs suggesting so.

### PROBLEM responses have empty middle fields

```
L\t\tPROBLEM 2
```

Note the empty middle tab field. **Scan ALL fields for "PROBLEM"** — never
assume the message is at `parts[1]`. Known codes:

| Code | Meaning |
|---|---|
| `PROBLEM 1` | Billing |
| `PROBLEM 2` | Already logged in **or** mode not supported |
| `PROBLEM 3` | Already in game |
| `PROBLEM 4` | Unavailable |

### Wizard (plain-text) mode

DR's SGE has deprecated `L\t{code}\tWIZ` — it returns `L\t\tPROBLEM 2`.
Plain text is achieved by:

1. Always authenticating with `L\t{code}\tSTORM` (which DR supports).
2. NOT sending the `FE:GENIE /VERSION:… /XML\n` announcement after
   connecting to the game server.

Without the FE announcement the server sends plain text. With it, the
server switches to StormFront XML mode.

### Character list without login

Steps 1–5 only. Stop after `C\n`; do not send `L\t…`.
`SgeAuthClient.ListCharactersAsync()` does this — useful for the
account-management UI before the user picks a character.

## Successful auth response shape

```
A\tMONIL\tKEY\t{hash}\t{full name}
```

The account name appears in field 1 on success. If field 1 is empty (e.g.
`A\t\tPASSWORD`), it's a failure — the message tells you why.

## After auth

`<settingsInfo/>` is the authoritative "server ready for input" signal in
StormFront mode. `GenieCore` subscribes to `SettingsInfoEvent` once
(`.Take(1)`) and sends `look` when it fires. Do **not** use a timer or
hardcoded delay — `<settingsInfo/>` arrives in all three connection modes
(DirectSGE, LichProxy, DevReplay).

For Wizard (plain-text) mode, no `<settingsInfo/>` ever arrives. `GenieCore`
fires `look` on the `Connected` event instead.
