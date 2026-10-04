# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

Oathbound (internal name `Oathbound.Plugin`) is a consent-based Owner/Sub control plugin for FINAL FANTASY
XIV, built on Dalamud. Ongoing commands (title/outfit/gesture/follow/moodles/restraints) travel as ordinary
`/tell` messages between the Owner's and Sub's own clients - no server involved, nothing crosses the wire but
short alias words, and everything is applied locally by the Sub's own Glamourer/Penumbra/Honorific/Moodles
IPC calls. See `README.md` for the full feature/consent-model writeup and `ffxiv-collar-system-design.md` for
the original feasibility research.

**Pairing, recovery backups, encrypted catalog sync and encrypted rulebooks are the one exception** and go through a Cloudflare
Worker relay (`worker/`) - see Architecture below. The default pairing flow is pairing by code
(`Oathbound.Plugin/Relay/CodePairingService.cs`, no tells, asynchronous, both people confirm each other's
character); the older tell-referenced handshake in `Relay/PairingService.cs` stays as the fallback for peers on
an older version. README is user-facing and light on pairing internals - read the code (and the local openspec
specs `collar/pairing`, `collar/pairing-recovery`, if present) for the current behavior.

This is two independently built and independently deployed things sharing one repo:
- `Oathbound.Plugin/` - the Dalamud plugin (C#/.NET), shipped as a GitHub Release + `repo.json` third-party
  Dalamud feed.
- `worker/` - the Cloudflare Worker relay (TypeScript), deployed directly via `wrangler deploy`, not tied to
  plugin releases at all.
- `protocol/` - the wire-format source of truth both sides must agree with byte-for-byte (see below).
- `openspec/` - OpenSpec (`schema: spec-driven`) planning, **gitignored and kept local only** (never
  committed). Capability specs live under `openspec/specs/collar/*`, in-flight work under `openspec/changes/`.
  Use the `opsx:*` skills/commands for that workflow rather than hand-editing those directories.

## Commands

### Plugin (Oathbound.Plugin)

```
dotnet build Oathbound.slnx                                          # build everything
dotnet build Oathbound.Plugin/Oathbound.Plugin.csproj                # build just the plugin
```

Requires a .NET 10 SDK and Dalamud installed at least once (`DALAMUD_HOME` env var if it's not at the
default `~/.xlcore/dalamud/Hooks/dev` / XIVLauncher path). The csproj forces `Platform=x64` unconditionally,
so build output always lands in `bin/x64/Debug/` (or `Release/`) regardless of how you invoke the build.

There is no automated test project for the plugin - correctness on the C# side is verified by building and
by manual in-game testing (see `README.md`'s "Activating in-game" section), not by an automated suite.
`protocol/vectors/crypto-vectors.json` is exercised only from `worker/test/` now.

### Worker (worker/)

```
cd worker
npm run dev                 # wrangler dev --env local
npm run lint                # eslint . --max-warnings 0
npm run typecheck           # tsc --noEmit
npm test                    # vitest run (Workers pool + miniflare, env.local, D1 migrations auto-applied)
npm run test:watch
npx vitest run path/to/file.spec.ts        # single test file
npx vitest run -t "test name"              # single test by name
npm run db:migrate:local                   # wrangler d1 migrations apply RELAY_DB --local --env local
npm run db:migrate:staging                 # --remote --env staging
npm run deploy:staging                     # wrangler deploy --env staging
npm run deploy:production                  # wrangler deploy --env production
```

Tests run against `wrangler.toml`'s `env.local` (in-memory D1/R2 via miniflare), not staging/production.
`worker/wrangler.toml`'s base block has no bindings and is never deployed directly - always target an
explicit `--env`. `staging` is the environment the shipped plugin actually talks to
(`RelayClient.RelayOrigin` in the plugin is hardcoded to `oathbound-relay-staging...workers.dev`);
`production`'s D1 database ID is still the literal placeholder `REPLACE_WITH_PRODUCTION_D1_ID` and isn't
deployable as-is.

Live-monitoring the deployed worker: `npx wrangler tail --env staging` (or the Cloudflare dashboard's Workers
& Pages -> `oathbound-relay-staging` -> Logs tab). Observability (`[env.X.observability]` in `wrangler.toml`)
is enabled with full log sampling on all three environments.

### Releasing the plugin

A version bump commit is not a release by itself - nothing ships until the matching git tag is pushed:

1. Bump `<Version>` in `Oathbound.Plugin/Oathbound.Plugin.csproj`, commit as `release: X.Y.Z <description>`.
2. `git push origin master`
3. `git tag vX.Y.Z && git push origin vX.Y.Z`

Pull first: the release bot's `chore: point repo.json at ...` commit usually lands on `master` after every
release, so local `master` is typically one commit behind. Never push or tag unless asked.

The tag push triggers `.github/workflows/release.yml`, which builds the plugin, publishes a GitHub Release
with `latest.zip`, then checks out `master` again and auto-commits `chore: point repo.json at vX.Y.Z`
(`stefanzweifel/git-auto-commit-action` - its commits show the triggering user as `Author` but
`github-actions[bot]` as `Committer`, which is normal, not a manual edit). `repo.json` is what Dalamud's
Plugin Installer actually polls (via `raw.githubusercontent.com`, ~5 min CDN cache) - a correctly-updated
`repo.json` does not mean testers see the update immediately, since Dalamud only refetches third-party repos
on launch or manual refresh (`/xlplugins` -> reopen/refresh, or restart the game).

`.github/workflows/pr-build.yml` only builds `Oathbound.Plugin/Oathbound.Plugin.csproj` on PRs to `master` -
it does not run the worker's tests/lint.

The worker (`worker/`) is never touched by either workflow and never needs a plugin version bump or tag -
deploy it directly with `npm run deploy:staging` / `deploy:production`.

## Architecture

### Plugin layout

```
Oathbound.Plugin/
  Ipc/          thin wrappers around Glamourer.Api, Penumbra.Api, Honorific's IPC, and Moodles' IPC
  Commands/     one file per command category, the chat listener, and the trigger composer/sender
  Config/       persisted plugin configuration, including the Sub's alias definitions and device identity
  UI/           CollarWindow (Title/Wardrobe/Gesture/Moodles/Restraints/Custom Triggers/Collar/Owner/
                Permissions tabs), SettingsWindow
  Safety/       panic handler and in-memory "what's currently applied" state
  Relay/        everything that talks to the Cloudflare relay (see below)
```

Owner and Sub share one codebase, one window, and one build - there is no separate Owner/Sub project, and
Role only changes what a client does with incoming tells and what it declares during pairing.

### The relay (pairing + catalog sync only)

`Oathbound.Plugin/Relay/` <-> `worker/` is the one part of this system with real client/server infrastructure.
Everything else in the plugin is peer-to-peer over FFXIV tells.

- **`CodePairingService.cs`** is the default pairing flow: a pairing code (`PairingCodes.cs`, 80-bit Crockford
  base32) derives both the invitation's lookup id and the AES key for each side's character name/world, so the
  relay never sees who pairs. The inviter confirms the accepting character before consume; commands stay
  tell-verified regardless. **`BackupService.cs`** keeps an encrypted backup of the device key + pairings on the
  relay under a recovery code, and **`RevocationService.CheckPairStatusAsync`** ends any pairing whose relay pair
  row (a permanent tombstone) is revoked - that's how unpair reaches an offline peer.
- **`PairingService.cs`** owns the original tell-referenced relay-assisted pairing state machine for both roles. The inviter
  creates a signed `InvitationEnvelope` via the relay, sends only its id in a `collarinvite <id>` tell; the
  receiver fetches and independently verifies that envelope's signature before ever showing a Pending
  request, and Accept publishes a signed `AcceptanceEnvelope` plus a `collarpairack <id> <proofDigest>` tell
  back. The inviter only activates the pairing after re-verifying that proof digest against what the relay
  actually recorded and consuming the invitation - relay state alone is never trusted as sufficient, the
  tell's FFXIV-verified sender is what binds a relay claim to a real character (see the comment on
  `HandleAcknowledgementTellAsync`).
- **`RelayClient.cs`** is the one HTTP boundary to the Worker (`RelayOrigin` is release-pinned to
  `oathbound-relay-staging...workers.dev`, not user-configurable). Every mutating call is signed with the
  device's own ECDSA key (`x-relay-device-key-id`/`-timestamp`/`-nonce`/`-signature` headers); reads are
  capability-only (the id in the URL path is itself the proof of possession).
- **`DeviceIdentityService.cs`** generates/persists each install's ECDSA P-256 device identity (DPAPI-
  protected on Windows, best-effort plaintext fallback otherwise - explicitly documented as *not* a real
  confidentiality guarantee under Wine).
- **`RelayCrypto.cs`** deliberately uses BouncyCastle, not `System.Security.Cryptography`, for EC key
  generation/signing: Wine's CNG shim doesn't implement EC key generation, which would crash the plugin on
  load under Wine if it used the BCL's CNG-backed `ECDsa`. AES-GCM/SHA-256 stay on the BCL since those aren't
  CNG-backed and aren't affected.
- **`CanonicalJson.cs` / `EnvelopeCanonical.cs`** implement the RFC 8785 (JCS) subset the protocol needs.
  This must byte-for-byte match the Worker's `canonicalize` npm package output for every envelope shape, or
  every signature verification on one side or the other silently fails - `protocol/vectors/crypto-vectors.json`
  is the cross-runtime source of truth this is tested against (see `worker/test/vectors.spec.ts`); there is
  no automated check on the plugin side, so a change here needs manual in-game verification against a
  running relay.

### protocol/

Shared, language-agnostic definition of the relay wire format: `constants.json` (timestamp tolerance,
expiry/size limits, request-signing shape), `schemas/*.schema.json`, and `vectors/*.json` (cross-runtime
crypto/canonicalization test vectors). Both `Oathbound.Plugin/Relay/` and `worker/src/` are hand-written
implementations of this contract in two different languages - when changing anything about envelope shape,
signing, or canonicalization, `protocol/` is the thing to update first; `worker/test/` enforces agreement
on the Worker side, but the plugin side has no automated check and needs manual verification instead.

### worker/ (the Cloudflare Worker relay)

```
worker/src/
  index.ts        routing
  routes/         invitations.ts (create/fetch/accept/consume), pairs.ts, revocations.ts, catalog.ts,
                  mailbox.ts (automatic catalog push/pickup), backups.ts (recovery backups), health.ts
  lib/auth.ts     verifySignedRequest - the one place every signed request's headers+signature are checked
  lib/quotas.ts   per-device/per-pair/global rate limits and the free-tier circuit breaker (assertCircuitBreakerClosed)
  lib/pairs.ts, crypto.ts, capability.ts, deviceKeys.ts, log.ts, ...
```

Storage is Cloudflare D1 (`RELAY_DB`, migrations in `worker/migrations/`) for invitations/pairs/revocations/
nonces/quota counters, and R2 (`RELAY_CATALOG_BUCKET`) for encrypted catalog-sync ciphertext. The relay never
sees plaintext catalog contents or character identity - see `lib/log.ts`'s `FORBIDDEN_FIELD_NAME_PATTERN`,
which throws if a log call site ever tries to emit a field name that looks like it could carry secrets,
signatures, ciphertext, or character/world identity.

`verifySignedRequest` (`lib/auth.ts`) is the single chokepoint every mutating endpoint runs through: header
shape, a `TIMESTAMP_TOLERANCE_SECONDS`-bounded clock check (300s, from `protocol/constants.json`), a resolved
public key (either from the request body itself for first-contact operations like invitation creation, or
from a previously-remembered device key for operations that require prior proof), a device-key-id/public-key
match, ECDSA signature verification, and nonce-replay rejection - every failure path throws the same generic
`"unauthorized"` `RelayError` so a caller can never distinguish *which* check failed.

### Consent/safety model

Everything gameplay-facing is designed so nothing can ever apply to a Sub's character without that Sub's own
plugin, pairing, and per-category permission all being true at the moment a tell arrives - see README.md's
"Consent model" and "Automation risk / ToS disclosure" sections for the full model (panic-as-safeword, scoped
revocable permissions, Gagged/Chat-action being called out as materially riskier automation surfaces). That
model is a constraint on any new feature here, not just documentation.

Everything the plugin sends on its own must be listed in README's automation section. Today that is: the
Owner's `leash travel` on area change (`LeashTravelWatcher`), the Sub's `collarleash off <reason>` notice when
a leash ends on its side (`LeashOffNotifier`), a user-configured Reactions chat reply, and lifecycle tells tied
to a user action (pairing ack, unpair, catalog request/denied, the optional rulebook publish nudge). Accepted
rulebooks (`Rulebook/`) run consequences on their own, but only through `ChatCommandListener.RunRulebookCommand`
and `ConsequenceQueue`, limited to `ConsequenceValidator`'s categories (never chat, teleport, leash or collar). Only `ChatSender` transmits chat. Peer-notice
tells are trigger-less keywords (`collarinvite`, `collarpairack`, `collarunpair`, `collarcatalogreq`,
`collarcatalogdenied`, `collarleash`, `collarrulebook`) composed in `ChatComposer` and matched in `ChatCommandListener`, always
verified against the tell's sender and a paired `PairingState` of the right direction - fail closed otherwise.

The Owner never sees the Sub's real state: `OwnerStatusEstimateTracker` builds an estimate from what the Owner
sent. Two pieces of real state flow back: the leash-off notice, and the collar status the Sub's
`CollarStatusReporter` posts to the relay (read by the Owner into `OwnerCollarStatusStore` during the pair status
check). Panic (`PanicHandler`) reverts everything
local except a locked collar - the collar only comes off via the owning Owner's `collar unlock` or unpairing.

Guided tours live in `UI/TutorialCatalog.cs` and point at sections by their `Section.Begin` id. Renaming,
removing or adding a module section needs the matching tour step updated there.

## Code comments

Keep comments minimal: only the non-obvious *why* (workarounds, threading, wire-format/security invariants).
No spec/design references (`collar/...`, `design D4`), no change history, nothing that restates the code, and
no mentions of non-dependency plugins (GagSpeak, Lightless, PoseKit, ReactToMe, ...).
