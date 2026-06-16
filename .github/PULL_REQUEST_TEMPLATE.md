<!--
Thanks for contributing to Genie 5! Keep PRs focused — one feature or one fix.
For anything beyond a trivial change, please open an issue first (see CONTRIBUTING.md).
-->

## Summary

<!-- What does this PR do, and why? Link the issue it closes: "Closes #123". -->

## Test plan

<!--
What did you do to verify this works, and what could it have broken? Be specific.
For parser / scripting / mapper changes, a REPLAY smoke-test against a real
recording is the easiest path:
  dotnet run --project src/Genie.Core -- REPLAY <file>
-->

## Checklist

- [ ] `dotnet build -c Release` succeeds cleanly (warnings OK, errors not)
- [ ] Tests / relevant test-harness modes pass for the subsystems I touched
- [ ] **No machine-specific paths** committed (no `C:\Users\…`, no hard-coded home dirs, no local absolute paths — use `AppPaths` / config)
- [ ] **No AI brand references** in tracked files (the public repo stays vendor-neutral)
- [ ] Docs updated if user-visible behaviour changed (README / CONTRIBUTING / `docs/`)
- [ ] This is a focused PR — one feature or one fix

## Compatibility & policy

<!-- Tick the lines that apply; delete the ones that don't. -->

- [ ] **Script parity** — if I touched the `.cmd` engine, existing Genie 4 scripts still work (tested against DR-Genie-Scripts where relevant)
- [ ] **Map format** — if I touched map I/O, Genie 4 `.xml` zone files still round-trip without loss
- [ ] **SGE protocol** — if I touched the handshake, I verified against the Genie 4 source / `docs/SGE_PROTOCOL.md`
- [ ] **DR policy acknowledged** — this change introduces no auto-reconnect, no agentive AI driving commands, no headless mode, and ships no other-player speech off-machine without consent. Anything that constrains how a player runs the client is opt-in and off by default. (See `docs/POLICY.md`.)
