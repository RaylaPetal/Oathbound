import fixture from "../../protocol/fixtures/valid/lfp-profile.json";
import { SELF } from "cloudflare:test";
import { afterEach, beforeEach } from "vitest";
import { genSigningKeyPair, signedFetch, useFetcher } from "./helpers";
import { newDevice, type Device } from "./socket-helpers";

export type Fields = Omit<typeof fixture.fields, "dataCenter" | "pronouns"> & { dataCenter: string | null; pronouns: string | null };

export async function me(d: Device) {
  return signedFetch("/v1/lfp/me", "POST", { type: "lfp-me", schemaVersion: 1, devicePublicKey: d.key.publicKeyJwk }, d.key.privateKey, d.id);
}

export async function putProfile(d: Device, overrides: Partial<Fields> = {}, extra: Record<string, unknown> = {}) {
  const matchKey = await genSigningKeyPair();
  return signedFetch(
    "/v1/lfp/profile",
    "PUT",
    { ...fixture, fields: { ...fixture.fields, ...overrides }, matchPublicKey: matchKey.publicKeyJwk, ...extra },
    d.key.privateKey,
    d.id,
  );
}

/** A device that introduced itself and published the fixture profile (with overrides). */
export async function withProfile(overrides: Partial<Fields> = {}, extra: Record<string, unknown> = {}) {
  const d = await newDevice();
  await me(d);
  const r = await putProfile(d, overrides, extra);
  if (r.status !== 200) throw new Error(`profile put failed: ${r.status} ${JSON.stringify(r.json)}`);
  return { device: d, profile: r.json.profile as { profileId: string; code: string; fields: Fields; picture: string | null } };
}

export const get = (d: Device, path: string) => signedFetch(path, "GET", undefined, d.key.privateKey, d.id);
export const post = (d: Device, path: string, body: object = {}) => signedFetch(path, "POST", body, d.key.privateKey, d.id);
export const del = (d: Device, path: string) => signedFetch(path, "DELETE", {}, d.key.privateKey, d.id);

export const like = (d: Device, profileId: string) => post(d, `/v1/lfp/likes/${profileId}`);

/** Two profiles that liked each other; returns both and the match id. */
export async function matched() {
  const a = await withProfile({ displayName: "Alpha" });
  const b = await withProfile({ displayName: "Bravo" });
  await like(a.device, b.profile.profileId);
  const r = await like(b.device, a.profile.profileId);
  if (!r.json.matched) throw new Error("no match");
  return { a, b, matchId: r.json.match.matchId as string };
}

export function browseIds(r: { json: { profiles: { profileId: string }[] } }): string[] {
  return r.json.profiles.map((p) => p.profileId);
}

export const PNG = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4]);

export async function newBareDevice(): Promise<Device> {
  const d = await newDevice();
  await me(d);
  return d;
}

/**
 * These suites send far more requests per minute than one real player; a fresh origin per request keeps the
 * per-origin flood limit from failing whichever test happens to run last.
 */
export function spreadOrigins(): void {
  beforeEach(() => useFetcher((request) => {
    const headers = new Headers(request.headers);
    headers.set("cf-connecting-ip", `198.51.100.${Math.floor(Math.random() * 250)}-${crypto.randomUUID()}`);
    return SELF.fetch(new Request(request, { headers }));
  }));
  afterEach(() => useFetcher());
}
