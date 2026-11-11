---
version: 0.1.2
level: copilot
processes:
  design: pair
  implementation: copilot
  testing: copilot
  documentation: copilot
  review: assist
  deployment: assist
---

This format is based on
[AI-DECLARATION.md](https://ai-declaration.md/en/0.1.2). Please read it to
understand the levels above.

## Notes

I am not a vibe coder. I have been working professionally as a software
developer for 6 years, and I know how every part of this plugin works. Oathbound
was built with AI assistance (Claude), and this document is here to be
transparent about exactly where and how.

AI is a tool here, not the author. I decide what gets built and how it should
behave, I review what is produced, and nothing ships without me understanding
it and testing it in game. Changes go through a spec-driven workflow: a
proposal, a design and a list of requirements are written and reviewed
before any code is written, so the AI works against decisions I made rather
than improvising.

That matters more for Oathbound than for most plugins. It acts on another
player's character, so its consent model (per-category permissions, panic as a
safeword that always works, nothing applied without the Sub's own plugin
agreeing) is set by me and treated as a hard constraint on every change, not
something the AI gets to reinterpret.

## Design

Pair. Features, behavior and UX are my decisions, based on how I and the
people I play with actually use it, and on what testers tell me. Claude helped
think through edge cases and write the design documents for each change, which
I reviewed and corrected before implementation.

## Implementation

Copilot. Most of the code was written by Claude from the reviewed specs, in
small, verifiable steps that I approved and reviewed. The tricky parts were
worked through together:
- the relay's signed requests and end-to-end encryption, and keeping the
  plugin and the worker agreeing byte for byte;
- the game hooks (chat, movement, actions);
- leash and cage movement.

I checked the results in game and against a running relay rather than trusting
them.

## Testing

Copilot. The relay (`worker/`) has an automated test suite, mostly written by
Claude and reviewed by me, that runs every endpoint against a local database,
including the authentication and rate-limit failure paths. The plugin itself
has no automated tests: every feature is tested by hand, in game, by me and
the people I pair with.

## Documentation

Copilot. The README, the in-game changelog, guided tours and code comments were
drafted by Claude and edited by me.

## Review

Assist. Claude is used to review changes for bugs before release. Every
finding is checked and either fixed or dismissed by me.

## Deployment

Assist. The GitHub release workflows were written with Claude's help, and
Claude can prepare a release when I ask for one. I decide when to release, and I
deploy and run the relay myself.
