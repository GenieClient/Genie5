# Security Policy

## Reporting a vulnerability

**Please do not file public GitHub issues for security bugs.**

If you've found a security issue in Genie 5, email the maintainers privately at:

> **genie5@theshadowrealms.com**

We aim to:

- Acknowledge receipt within **3-5 business days**
- Provide an initial assessment within **7 business days**
- Issue a fix or detailed timeline within **30 days** for confirmed high-severity issues

If you don't hear back within those windows, feel free to follow up via DM on [Discord](https://discord.gg/MtmzE2w) (shared community server with Genie 4).

**Non-sensitive bug reports** (crashes, missing features, weird parser output) don't need to come through this private channel — file them as [GitHub issues](https://github.com/GenieClient/Genie5/issues) or drop a note in Discord. This policy is specifically for vulnerabilities that could let someone harm another user — credential leaks, code execution, privilege escalation, that kind of thing.

## What counts as a security issue

The following classes of bug are *especially* important to report privately rather than as public issues:

### Credential / authentication

- Anything that leaks DR account credentials beyond the local machine
- Anything that weakens the AES-GCM password encryption in `ProfileCrypto.cs`
- Anything that exposes the SGE handshake (custom password-XOR-byte encoding) in a way that could be replayed
- Anything that lets one user's `profiles.json` be decrypted on another machine without explicit sync

### Local file safety

- Path traversal in script loading (`.foo` resolving to a path outside `Scripts/`)
- Arbitrary file write from a `.cmd` script (the `.cmd` interpreter shouldn't have file I/O primitives — if you find one, it's a bug)
- Plugin-loader bugs that would let a plugin DLL load or execute without the user having placed it in the Plugins folder

### Network / protocol

- Anything that lets a malicious server inject commands that get sent back to DR as the user
- Anything in the XML parser that crashes on hostile input, allows uncontrolled allocation, or otherwise turns into a denial-of-service
- Anything in `LichProxy` mode that lets a man-in-the-middle on `127.0.0.1` alter game traffic

### AI pipeline

- Anything that bypasses the "advisor-only" wall and gets AI-generated text into `Commands.ProcessInput` (this would turn Genie into an agentive bot, which is forbidden by DR policy)
- Anything that ships other players' speech (whispers / talk / thoughts / familiar / tells) to an external AI service when the user hasn't opted in

## What's NOT a security issue (file as a normal bug)

- App crashes from your own malformed `.cmd` script
- Display glitches, UI flicker, layout bugs
- Wrong parser output for game text (file a parser-gap report)
- Anything that requires physical access to the user's machine — that's outside our threat model for a local client

## Threat model

Genie 5 is a **desktop game client**, not a server-side service. Our threat model assumes:

- ✅ The user is the only person on the local machine (or the only person we're protecting)
- ✅ The DR game server is mostly trusted (it's Simutronics' production server) but we don't trust it to send safe XML — the parser must remain robust to hostile input
- ✅ The AI vendor API endpoint is trusted for the AI pipeline (TLS, signed cert)
- ❌ We do **not** protect against an attacker with disk access (they can decrypt `profiles.json` if they know the machine name + read the source)
- ❌ We do **not** protect against a malicious user-installed plugin — the plugin host is shipped and a loaded plugin DLL runs at full trust (.NET has no real in-process sandbox). Only install plugins you trust; load-time linting and a signing/consent flow are roadmap (v1.0)

## Existing security posture

- **Passwords on disk**: AES-256-GCM, authenticated encryption, key derived from `Environment.MachineName` + fixed salt. Sufficient for local-only storage; **not portable** (this is by design — same plaintext encrypts differently on different machines, which protects against disk-image attacks but breaks naive cloud sync).
- **SGE auth password**: encrypted with the canonical `(byte - 32) XOR keybyte) + 32` formula at the wire level, as required by the Simutronics protocol. The plaintext password is never written to disk; it lives in memory for the duration of the handshake then is overwritten.
- **AI pipeline filtering**: `AiContextBuffer` strips other-players' speech streams before any external API call. See [docs/POLICY.md](docs/POLICY.md) for the full filter list.

## Coordinated disclosure

If your finding affects multiple Genie ecosystem projects (Genie 4, Lich, etc.) we're happy to coordinate disclosure timing so all affected clients ship fixes together. Mention this in your initial email.

Thank you for keeping Genie 5 users safe.
