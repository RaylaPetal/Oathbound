# Relay Threat Model and Data Retention

## Scope

This document covers the Cloudflare relay only: invitation/acceptance/pair
exchange, revocation publication, and encrypted catalog snapshot transfer. It
does not cover FFXIV chat, Dalamud, or the local machine, except where local
compromise affects the relay's guarantees.

## Assets and adversaries

| Asset | Adversary considered | Out of scope |
|---|---|---|
| Device signing/ECDH private keys | Network attacker, relay operator, another relay client | Local-machine attacker with code execution on the user's PC |
| Pairing state (who is paired with whom) | Passive relay operator, network attacker, other relay clients | N/A |
| Catalog plaintext | Relay operator, network attacker, R2/D1 access by anyone but the paired Owner | N/A |
| Relay service availability/cost | Anonymous internet abuse, a paired peer misusing its own capabilities | Targeted DDoS beyond Cloudflare's platform-level protection |
| Character identity (name/world) | Anyone observing relay traffic or storage | FFXIV server-side compromise |

A compromised local machine (stolen private key, modified plugin binary) is
explicitly **out of the relay's threat boundary**: the mitigation is device
identity reset and re-pairing, not a relay-side defense, because the relay
cannot distinguish a legitimate signature from one produced by malware
holding the same key.

## Trust boundaries

1. **Relay operator is honest-but-curious, not malicious-but-not-Byzantine.**
   It must not be able to read catalog plaintext or private keys (enforced by
   end-to-end encryption before upload), but it is trusted to run the code as
   deployed, apply quotas, and delete expired data. A fully Byzantine operator
   could still deny service or drop messages; that failure mode is mitigated
   by keeping manual pairing/export as an offline fallback, never by relay-side
   cryptography.
2. **Character identity can never be proven to the relay.** Only FFXIV's
   server-verified tell sender proves a character. The relay only ever
   authenticates a *device key*, and pairing activation requires both a
   relay-observed device proof and a matching verified tell (see
   `../../openspec/changes/add-cloudflare-pairing-catalog-relay/design.md`,
   "Bind device keys through a two-channel handshake").
   **Code invitations** (`kind: "code"`, collar/pairing) replace that tell with
   an explicit confirmation by both people: the invitee accepts after seeing the
   inviter's character, and the inviter confirms after seeing the accepter's.
   Both characters travel only inside AES-GCM blobs keyed from the pairing code,
   which the relay never sees. This binding is weaker than a verified tell, and
   that is acceptable because **gameplay commands never depend on it**: every
   command is still accepted only from an FFXIV tell whose verified sender
   matches the stored peer, so a false character claim yields a pairing no
   command can ever match. The residual risk (a third party using an
   intercepted code) is covered by the inviter's Confirm/Reject step.
3. **A leaked capability id alone is insufficient.** Every mutating request
   is additionally signed (`constants.json` → `requestSigning`); a leaked URL
   without the corresponding private key cannot mutate or, for read paths,
   disclose data.

## Abuse assumptions

- Anyone on the internet can call every endpoint anonymously; there is no
  account system and no IP allowlist. Layered quotas (per-device, per-pair,
  per-origin, per-endpoint, payload bytes, global) are the only gate, per
  `specs/collar/relay-service/spec.md` → "Relay enforces layered abuse and
  cost controls".
- A malicious *paired* peer (one that legitimately holds a device key bound
  to an active pair) can still attempt to exceed the catalog cooldown, replay
  its own old signed requests, or hold the connection open; server-side
  cooldown/replay/timeout enforcement must not trust client-declared retry
  behavior.
- Decompression bombs are assumed to be attempted against the Owner client on
  import (bounded allocation during decompress, per
  `specs/collar/catalog-sync/spec.md` and task 6.4) and are not a relay-side
  concern beyond enforcing `catalogCiphertextMaxBytes`.

## Retained metadata and deletion windows

Every field defined in `../schemas/*.schema.json` is classified below. "D1
metadata" rows are stored as declared; "Never stored" rows exist only in
client-to-client envelopes and must not appear in relay logs or database rows
by construction (enforced by the D1 migrations in task 2.2 and the log-capture
test in task 2.9).

| Field (schema) | Purpose | Retention |
|---|---|---|
| `invitationId`, `requestId` (capability ids) | Unguessable handle for one-use fetch/consume | Deleted at consumption or 15 min expiry. A code invitation (`kind: "code"`, where `invitationId` is the lookup id derived from the pairing code) is kept, with its end state, until its own expiry of at most 7 days so the accepter can read the outcome |
| `encryptedCharacter` (code invitation/acceptance) | Each side's character name, world and trigger phrase, readable only with the pairing code | Opaque AES-GCM ciphertext; deleted with its invitation (at most 7 days) |
| Backup `ciphertext`, `nonce`, bound `deviceKeyId` (collar/pairing-recovery) | Lets a player restore identity and pairings with their recovery code | Opaque AES-GCM ciphertext keyed from the recovery code, stored under SHA-256 of the backupId; replaced on each refresh, deleted when the code is regenerated or the identity reset |
| `inviterDeviceKeyId`, `accepterDeviceKeyId`, `requesterDeviceKeyId`, `senderDeviceKeyId`, `recipientDeviceKeyId`, `issuedByDeviceKeyId`, `ownerDeviceKeyId`, `subDeviceKeyId` | Bind an envelope to a specific device's signing key | Kept only while the owning pair/invitation record exists; deleted with it |
| `inviterPublicKey`, `accepterPublicKey`, `ownerEphemeralPublicKey`, `senderEphemeralPublicKey` (JWKs) | Signature verification / ECDH key agreement | Ephemeral ECDH keys deleted with their one-use request/response; signing public keys retained only for the life of the pair record |
| `role` | Distinguishes Owner/Sub for the pending invitation | Deleted with the invitation |
| `pairIdHash` | Server-side row key without revealing character identity | Retained indefinitely, including after revocation: a revoked pair row (hash, epoch, the two device key ids, `created_at`, `revoked_at`) is the permanent tombstone that lets a peer who was offline for any length of time learn the pairing ended (collar/pairing, `GET /v1/pairs/{hash}?epoch=N`). It carries no character identity |
| `pairEpoch`, `sequence` (monotonic), `snapshotId` (monotonic) | Ordering and stale/replay rejection | Retained only as long as needed to reject an older duplicate; not historical audit data |
| `createdAt`, `expiresAt` | Server-enforced lifecycle | Drives deletion; not retained past expiry |
| `signature` | Authenticates the envelope | Never stored separately from the record it authenticates; deleted with it |
| `proofDigest` | Binds the acknowledgement tell to a specific acceptance | Deleted with the invitation/pair record |
| `reason` (`unpair`/`panic`) | Lets a peer distinguish safety-relevant revocation from routine unpair (both processed identically as "end pairing now") | Deleted at revocation expiry (max 7 days) |
| `algorithm`, `ciphertextDigest`, `ciphertextSizeBytes`, `nonce` | Integrity/size bookkeeping for the R2 ciphertext object, never key material | Deleted at consumption or 15 min expiry |
| R2 object bytes (ciphertext) | The only place catalog content exists on the relay, and only in encrypted form | One-use retrieval, eager delete on consumption, scheduled orphan cleanup, hard 15 min expiry |
| `code`, `retryAfterSeconds` (error) | Client-facing outcome, deliberately coarse (see `schemas/error.schema.json`) | Not persisted; response-only |
| `revocation`, `attempt`, `nextAttemptAt` (retry) | **Client-local** outbox state for best-effort re-publication | Never sent to or stored by the relay; lives only in the plugin's own configuration store, deleted on successful publish or outbox expiry |

**Never stored anywhere in the relay, by construction:** character name,
character world, FFXIV account identity, gameplay command content (no schema
branch exists for it — see `fixtures/invalid/gameplay-command.json`),
plaintext catalog content, private keys, or capability secrets in
non-hashed form.

## Local key storage under Wine

Dalamud plugins commonly run under Wine/Proton rather than native Windows.
Windows DPAPI (`ProtectedData`/`CryptProtectData`), which the plugin uses as
its best-available local key protection, has **no equivalent secret-sealing
guarantee under Wine**: Wine's DPAPI implementation exists for compatibility
but does not provide OS-enforced, user-session-bound encryption backed by a
real Windows credential store. Concretely:

- Under native Windows, a stolen configuration file alone is insufficient to
  recover the private key without the user's Windows login session.
  Under Wine, this guarantee **does not hold** — a stolen configuration
  directory should be treated as equivalent to a stolen plaintext key.
- The plugin MUST still call the platform DPAPI API where available (it costs
  nothing and helps on native Windows installs), but MUST NOT claim or imply
  in its UI that the key is "protected" when running under Wine.
- Settings UI (task 7.2) must visibly disclose this limitation near the
  device identity/reset control, not bury it in documentation only.
- The mitigation is operational, not cryptographic: keep the private
  configuration directory's OS file permissions as restrictive as the
  platform allows, and make device-identity reset (which invalidates all
  relay pairings) fast and low-friction so a suspected compromise is cheap to
  recover from.

## Explicit command-transport exclusion

The relay's schemas (`schemas/envelope.schema.json`) have no branch for
restraint, gesture, outfit, title, Moodle, follow, collar, custom-trigger,
arbitrary chat, or any future gameplay command. This is a protocol-level
guarantee, not a policy one: an envelope of unknown `type`, or one that adds
command-shaped fields to a known type, fails `additionalProperties`/`oneOf`
validation and is rejected before any handler runs (verified by
`fixtures/invalid/gameplay-command.json` and `fixtures/invalid/unknown-type.json`
in the schema conformance check). Gameplay commands remain authenticated
exclusively through FFXIV tells and `ChatSender`; no future relay endpoint may
be added without updating this document and the schema fixtures to justify why
it is not gameplay-command transport.
