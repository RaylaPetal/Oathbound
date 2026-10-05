import { SELF } from "cloudflare:test";
import canonicalize from "canonicalize";

export const BASE = "https://relay.test";

type Fetcher = (request: Request) => Promise<Response>;
const defaultFetcher: Fetcher = (request) => SELF.fetch(request);
let fetcher: Fetcher = defaultFetcher;

/** Routes every helper request through `f` instead of SELF; pass nothing to restore the default. */
export function useFetcher(f: Fetcher = defaultFetcher): void {
  fetcher = f;
}

export function relayFetch(path: string, init?: RequestInit): Promise<Response> {
  return fetcher(new Request(BASE + path, init));
}

export function hex(buf: ArrayBuffer): string {
  return Array.from(new Uint8Array(buf)).map((b) => b.toString(16).padStart(2, "0")).join("");
}

export function b64url(buf: ArrayBuffer | Uint8Array): string {
  const bytes = buf instanceof Uint8Array ? buf : new Uint8Array(buf);
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function b64urlToBytes(value: string): Uint8Array {
  const padded = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

export interface Jwk {
  kty: "EC";
  crv: "P-256";
  x: string;
  y: string;
}

export interface KeyPair {
  privateKey: CryptoKey;
  publicKeyJwk: Jwk;
}

export async function genSigningKeyPair(): Promise<KeyPair> {
  const kp = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
  const jwk = (await crypto.subtle.exportKey("jwk", kp.publicKey)) as JsonWebKey;
  return { privateKey: kp.privateKey, publicKeyJwk: { kty: "EC", crv: "P-256", x: jwk.x!, y: jwk.y! } };
}

export async function deviceKeyId(publicKeyJwk: Jwk): Promise<string> {
  return hex(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(canonicalize(publicKeyJwk))));
}

export async function signEnvelope(privateKey: CryptoKey, unsigned: object): Promise<string> {
  const canonical = canonicalize(unsigned)!;
  const sig = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, privateKey, new TextEncoder().encode(canonical));
  return b64url(sig);
}

export function randomCapabilityId(): string {
  return b64url(crypto.getRandomValues(new Uint8Array(32)));
}

export interface SignedFetchResult {
  status: number;
  json: any;
  headers: Headers;
}

export async function signedFetch(
  path: string,
  method: string,
  bodyObj: object | undefined,
  privateKey: CryptoKey,
  deviceKeyIdValue: string,
): Promise<SignedFetchResult> {
  const bodyJson = bodyObj ?? {};
  const canonicalBody = canonicalize(bodyJson)!;
  const bodyDigest = hex(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(canonicalBody)));
  const timestamp = Math.floor(Date.now() / 1000);
  const nonce = b64url(crypto.getRandomValues(new Uint8Array(16)));
  const pathnameOnly = path.split("?")[0];
  const baseString = [method, pathnameOnly, bodyDigest, String(timestamp), nonce].join("\n");
  const sig = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, privateKey, new TextEncoder().encode(baseString));

  const hasBody = method !== "GET" && method !== "HEAD";
  const response = await relayFetch(path, {
    method,
    headers: {
      "content-type": "application/json",
      "x-relay-device-key-id": deviceKeyIdValue,
      "x-relay-timestamp": String(timestamp),
      "x-relay-nonce": nonce,
      "x-relay-signature": b64url(sig),
    },
    ...(hasBody ? { body: canonicalBody } : {}),
  });
  const text = await response.text();
  let json: any;
  try {
    json = JSON.parse(text);
  } catch {
    json = text;
  }
  return { status: response.status, json, headers: response.headers };
}

/** Builds and sends a fully-signed invitation-creation request; returns the parsed invitation envelope on success. */
export async function createInvitation(inviter: KeyPair, inviterId: string, role: "owner" | "sub" = "owner") {
  const now = Math.floor(Date.now() / 1000);
  const unsigned = {
    type: "invitation",
    schemaVersion: 1,
    invitationId: randomCapabilityId(),
    inviterDeviceKeyId: inviterId,
    inviterPublicKey: inviter.publicKeyJwk,
    role,
    createdAt: now,
    expiresAt: now + 900,
  };
  const signature = await signEnvelope(inviter.privateKey, unsigned);
  return signedFetch("/v1/invitations", "POST", { ...unsigned, signature }, inviter.privateKey, inviterId);
}

/** Builds and sends a fully-signed Accept request for a given invitation. */
export async function acceptInvitation(invitationId: string, receiver: KeyPair, receiverId: string, proofDigest?: string) {
  const now = Math.floor(Date.now() / 1000);
  const unsigned = {
    type: "acceptance",
    schemaVersion: 1,
    invitationId,
    accepterDeviceKeyId: receiverId,
    accepterPublicKey: receiver.publicKeyJwk,
    proofDigest: proofDigest ?? hex(crypto.getRandomValues(new Uint8Array(16)).buffer),
    createdAt: now,
    expiresAt: now + 900,
  };
  const signature = await signEnvelope(receiver.privateKey, unsigned);
  return signedFetch(`/v1/invitations/${invitationId}/accept`, "POST", { ...unsigned, signature }, receiver.privateKey, receiverId);
}

/** Full happy-path pairing: creates an invitation, accepts it, and consumes it. Returns the resulting pair envelope. */
export async function pairDevices(inviter: KeyPair, inviterId: string, receiver: KeyPair, receiverId: string, role: "owner" | "sub" = "owner") {
  const invite = await createInvitation(inviter, inviterId, role);
  await acceptInvitation(invite.json.invitationId, receiver, receiverId);
  const consumeResult = await signedFetch(`/v1/invitations/${invite.json.invitationId}/consume`, "POST", {}, inviter.privateKey, inviterId);
  return { invitationId: invite.json.invitationId as string, pair: consumeResult.json };
}
