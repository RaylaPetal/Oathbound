import type { EcPublicKeyJwk } from "./crypto";
import { RelayError } from "./errors";

/**
 * Hand-written validators mirroring protocol/schemas/common.schema.json.
 * Ajv's default compiled validators rely on `new Function`, which the
 * Workers runtime does not permit without an unsafe-eval compatibility flag
 * this project deliberately does not enable, so request bodies are validated
 * by hand instead. protocol/fixtures/ is still the source of truth for the
 * wire shape; a contract test (task 8.1) checks these validators against it.
 */

const HEX64 = /^[0-9a-f]{64}$/;
const PROOF_DIGEST_HEX = /^[0-9a-f]{32}$/;
const BASE64URL_43 = /^[A-Za-z0-9_-]{43}$/;
const BASE64URL_86 = /^[A-Za-z0-9_-]{86}$/;
const CAPABILITY = /^[A-Za-z0-9_-]{32,64}$/;
const NONCE_22 = /^[A-Za-z0-9_-]{22}$/;
const AEAD_NONCE_16 = /^[A-Za-z0-9_-]{16}$/;

export function isHex64(value: unknown): value is string {
  return typeof value === "string" && HEX64.test(value);
}
/** protocol/constants.json `proofDigest` - a 128-bit opaque token, not a SHA-256 digest despite the name. */
export function isProofDigestHex(value: unknown): value is string {
  return typeof value === "string" && PROOF_DIGEST_HEX.test(value);
}
export function isCapabilityId(value: unknown): value is string {
  return typeof value === "string" && CAPABILITY.test(value);
}
export function isSignature(value: unknown): value is string {
  return typeof value === "string" && BASE64URL_86.test(value);
}
export function isNonce(value: unknown): value is string {
  return typeof value === "string" && NONCE_22.test(value);
}
export function isAeadNonce(value: unknown): value is string {
  return typeof value === "string" && AEAD_NONCE_16.test(value);
}
export function isUnixSeconds(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0;
}
export function isNonNegInt(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0;
}
export function isRole(value: unknown): value is "owner" | "sub" {
  return value === "owner" || value === "sub";
}
export function isEcPublicKeyJwk(value: unknown): value is EcPublicKeyJwk {
  if (typeof value !== "object" || value === null) return false;
  const v = value as Record<string, unknown>;
  return v.kty === "EC" && v.crv === "P-256" && typeof v.x === "string" && BASE64URL_43.test(v.x) && typeof v.y === "string" && BASE64URL_43.test(v.y);
}

/**
 * collar/pairing: a code invitation's AES-GCM character blob. The relay can't read it (the key is derived
 * from the pairing code, which it never has) and only bounds its shape: a 12-byte nonce and a small
 * ciphertext (name + world + trigger phrase JSON, plus the 16-byte tag, stays well under 512 bytes).
 */
export interface EncryptedBlob {
  nonce: string;
  ciphertext: string;
}
const BASE64URL_CIPHERTEXT = /^[A-Za-z0-9_-]{24,700}$/;
export function isEncryptedCharacterBlob(value: unknown): value is EncryptedBlob {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const v = value as Record<string, unknown>;
  return Object.keys(v).length === 2 && isAeadNonce(v.nonce) && typeof v.ciphertext === "string" && BASE64URL_CIPHERTEXT.test(v.ciphertext);
}

export function asRecord(bodyJson: unknown): Record<string, unknown> {
  if (typeof bodyJson !== "object" || bodyJson === null || Array.isArray(bodyJson)) {
    throw new RelayError("invalid_request");
  }
  return bodyJson as Record<string, unknown>;
}

export function requireField<T>(record: Record<string, unknown>, field: string, check: (v: unknown) => v is T): T {
  const value = record[field];
  if (!check(value)) throw new RelayError("invalid_request");
  return value;
}
