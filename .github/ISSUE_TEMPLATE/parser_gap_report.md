---
name: Parser gap report
about: Wrong game-text output, untyped XML, or a tag Genie 5 doesn't understand
title: "[Parser] "
labels: parser-gap
assignees: ''
---

<!--
Parser-gap reports are some of the most valuable contributions to Genie 5 —
they're how the XML parser learns DragonRealms' full tag vocabulary. The single
most useful thing you can attach is a raw-XML capture of the moment it went
wrong: File → Record Session…, reproduce, then find the relevant section.
-->

## What you saw

<!--
What did Genie 5 display, and what was wrong about it? e.g. "the held-item name
leaked into the room description", "whisper text was split across two lines",
"this verb's output showed raw <tag> markup".
-->

## What it should have shown

<!-- The correct text / behaviour, as best you can describe it. -->

## Raw XML snippet

<!--
Paste the relevant lines from a File → Record Session… capture. Include a little
context before and after the problem. Captures can contain account/character
names and other players' speech — trim anything you don't want public.
-->

```xml
(paste the raw XML here)
```

## Game context

- **Verb / action that produced it:** <!-- e.g. `look`, `inventory`, a whisper received, entering a room -->
- **Connection mode:** <!-- Direct SGE / Lich Proxy / Dev-replay -->
- **Genie 5 version:** <!-- Help → About, or release filename -->

## Anything else

<!-- Is it consistent or intermittent? Tied to a specific room / item / NPC? Did it work in Genie 4? -->
