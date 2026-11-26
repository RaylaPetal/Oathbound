import type { Env } from "../env";
import { LFP_LISTED_FOR_SECONDS, nowSeconds } from "./constants";
import { toHex } from "./json";
import { chargeLive } from "./quotas";
import { logEvent } from "./log";

export interface ProfileRow {
  profile_id: string;
  device_key_id: string;
  code: string;
  visibility: "public" | "private";
  hidden: number;
  display_name: string;
  pronouns: string | null;
  race: string;
  gender: string;
  role: string;
  seeking: string;
  data_center: string | null;
  bio: string;
  picture_key: string | null;
  picture_type: string | null;
  match_public_key: string;
  adult_attested_at: number;
  created_at: number;
  updated_at: number;
  active_at: number;
}

export interface MatchRow {
  match_id: string;
  device_a: string;
  device_b: string;
  profile_a: string;
  profile_b: string;
  key_a: string;
  key_b: string;
  snapshot_a: string;
  snapshot_b: string;
  created_at: number;
  ended_at: number | null;
}

export interface ProfileView {
  profileId: string;
  fields: {
    displayName: string;
    pronouns: string | null;
    race: string;
    gender: string;
    role: string;
    seeking: string;
    dataCenter: string | null;
    bio: string;
  };
  picture: string | null;
  activeAt: number;
}

export function pictureUrl(key: string | null): string | null {
  return key ? `/v1/lfp/pictures/${key}` : null;
}

export function profileView(row: ProfileRow): ProfileView {
  return {
    profileId: row.profile_id,
    fields: {
      displayName: row.display_name,
      pronouns: row.pronouns,
      race: row.race,
      gender: row.gender,
      role: row.role,
      seeking: row.seeking,
      dataCenter: row.data_center,
      bio: row.bio,
    },
    picture: pictureUrl(row.picture_key),
    activeAt: row.active_at,
  };
}

export function isListed(row: ProfileRow, now = nowSeconds()): boolean {
  return row.active_at > now - LFP_LISTED_FOR_SECONDS;
}

export function ownProfileView(row: ProfileRow) {
  return { ...profileView(row), code: row.code, visibility: row.visibility, hidden: row.hidden === 1, listed: isListed(row) };
}

export async function profileByDevice(env: Env, deviceKeyId: string): Promise<ProfileRow | null> {
  return env.RELAY_DB.prepare(`SELECT * FROM lfp_profiles WHERE device_key_id = ?1`).bind(deviceKeyId).first<ProfileRow>();
}

export async function profileById(env: Env, profileId: string): Promise<ProfileRow | null> {
  return env.RELAY_DB.prepare(`SELECT * FROM lfp_profiles WHERE profile_id = ?1`).bind(profileId).first<ProfileRow>();
}

export async function matchById(env: Env, matchId: string): Promise<MatchRow | null> {
  return env.RELAY_DB.prepare(`SELECT * FROM lfp_matches WHERE match_id = ?1`).bind(matchId).first<MatchRow>();
}

/** Either device blocked the other. */
export async function isBlockedEitherWay(env: Env, deviceA: string, deviceB: string): Promise<boolean> {
  const row = await env.RELAY_DB.prepare(
    `SELECT 1 AS hit FROM lfp_blocks WHERE (blocker_device = ?1 AND blocked_device = ?2) OR (blocker_device = ?2 AND blocked_device = ?1) LIMIT 1`,
  )
    .bind(deviceA, deviceB)
    .first<{ hit: number }>();
  return row !== null;
}

/** Lowercase hex of `bytes` random bytes. */
export function randomHex(bytes: number): string {
  return toHex(crypto.getRandomValues(new Uint8Array(bytes)).buffer);
}

const CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/** 10 Crockford base32 characters (50 random bits). */
export function randomProfileCode(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(10));
  let code = "";
  for (const b of bytes) code += CROCKFORD[b & 31];
  return code;
}

/** Uppercase, separators dropped, I/L -> 1 and O -> 0, like a pairing code. */
export function normalizeProfileCode(raw: string): string {
  return raw.toUpperCase().replace(/[\s-]/g, "").replace(/[IL]/g, "1").replace(/O/g, "0");
}

/** collar/looking-for-pair: tells each device's live connection to re-read its matches. Best effort. */
export async function notifyLfp(env: Env, devices: readonly string[]): Promise<void> {
  await Promise.all(devices.map(async (device) => {
    try {
      if (!(await chargeLive(env))) return;
      await env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(device)).lfpHint();
    } catch {
      logEvent("hint_failed");
    }
  }));
}

/** Drops anything still waiting on either member's hub for an ended match. */
export async function purgeMatchChat(env: Env, match: Pick<MatchRow, "match_id" | "device_a" | "device_b">): Promise<void> {
  await Promise.all([match.device_a, match.device_b].map((device) =>
    env.DEVICE_HUB.get(env.DEVICE_HUB.idFromName(device)).purgeChat(match.match_id).catch(() => logEvent("purge_failed"))));
}
