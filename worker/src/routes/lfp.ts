import type { Env } from "../env";
import { READ_ONLY, resolverFromStoredDeviceKeys, resolverFromStoredOrBodyKey, verifySignedRequest } from "../lib/auth";
import { base64UrlToBytes, bytesToBase64Url } from "../lib/base64";
import {
  LFP_BIO_MAX_CHARS,
  LFP_BROWSE_PAGE_SIZE,
  LFP_DATA_CENTERS,
  LFP_DISPLAY_NAME_MAX_CHARS,
  LFP_DISPLAY_NAME_MIN_CHARS,
  LFP_GENDERS,
  LFP_LISTED_FOR_SECONDS,
  LFP_PICTURE_MAX_BYTES,
  LFP_PRONOUNS_MAX_CHARS,
  LFP_RACES,
  LFP_ROLES,
  LFP_SEEKING,
  LFP_VISIBILITY,
  nowSeconds,
} from "../lib/constants";
import type { EcPublicKeyJwk } from "../lib/crypto";
import { rememberDeviceKey } from "../lib/deviceKeys";
import { RelayError } from "../lib/errors";
import { toCanonicalJson } from "../lib/json";
import {
  isBlockedEitherWay,
  isListed,
  matchById,
  normalizeProfileCode,
  notifyLfp,
  ownProfileView,
  profileByDevice,
  profileById,
  profileView,
  purgeMatchChat,
  randomHex,
  randomProfileCode,
  type MatchRow,
  type ProfileRow,
  type ProfileView,
} from "../lib/lfp";
import { assertCircuitBreakerClosed, enforceQuota } from "../lib/quotas";
import { asRecord, isEcPublicKeyJwk } from "../lib/validate";

/**
 * collar/looking-for-pair. Profiles are plain data the relay serves to other players; it never stores a character
 * name or world, and never returns a device key id to anyone but its owner's own requests.
 */

const PROFILE_ID = /^[0-9a-f]{32}$/;
const MATCH_ID = /^[0-9a-f]{64}$/;
const BLOCK_ID = PROFILE_ID;
const PICTURE_KEY = /^[A-Za-z0-9_-]{43}$/;
const CODE = /^[0-9A-HJKMNP-TV-Z]{10}$/;
// Control characters are refused everywhere; the bio alone keeps line breaks and tabs.
const CONTROL = /[\u0000-\u001f\u007f-\u009f\u2028\u2029]/;
const CONTROL_EXCEPT_NEWLINE = /[\u0000-\u0008\u000b-\u001f\u007f-\u009f\u2028\u2029]/;

function json(body: Record<string, unknown>): Response {
  return Response.json({ schemaVersion: 1, ...body });
}

function chars(value: string): number {
  return [...value].length;
}

interface Fields {
  displayName: string;
  pronouns: string | null;
  race: string;
  gender: string;
  role: string;
  seeking: string;
  dataCenter: string | null;
  bio: string;
}

function readFields(value: unknown): Fields {
  const f = asRecord(value);
  const allowed = new Set(["displayName", "pronouns", "race", "gender", "role", "seeking", "dataCenter", "bio"]);
  if (Object.keys(f).some((k) => !allowed.has(k))) throw new RelayError("invalid_request");

  if (typeof f.displayName !== "string") throw new RelayError("invalid_request");
  const displayName = f.displayName.trim();
  if (chars(displayName) < LFP_DISPLAY_NAME_MIN_CHARS || chars(displayName) > LFP_DISPLAY_NAME_MAX_CHARS || CONTROL.test(displayName)) {
    throw new RelayError("invalid_request");
  }

  let pronouns: string | null = null;
  if (f.pronouns !== undefined && f.pronouns !== null) {
    if (typeof f.pronouns !== "string") throw new RelayError("invalid_request");
    const trimmed = f.pronouns.trim();
    if (chars(trimmed) > LFP_PRONOUNS_MAX_CHARS || CONTROL.test(trimmed)) throw new RelayError("invalid_request");
    pronouns = trimmed.length > 0 ? trimmed : null;
  }

  const pick = (key: string, set: ReadonlySet<string>): string => {
    const v = f[key];
    if (typeof v !== "string" || !set.has(v)) throw new RelayError("invalid_request");
    return v;
  };
  const race = pick("race", LFP_RACES);
  const gender = pick("gender", LFP_GENDERS);
  const role = pick("role", LFP_ROLES);
  const seeking = pick("seeking", LFP_SEEKING);
  const dataCenter = f.dataCenter === undefined || f.dataCenter === null ? null : pick("dataCenter", LFP_DATA_CENTERS);

  if (typeof f.bio !== "string") throw new RelayError("invalid_request");
  const bio = f.bio.trim();
  if (chars(bio) > LFP_BIO_MAX_CHARS || CONTROL_EXCEPT_NEWLINE.test(bio)) throw new RelayError("invalid_request");

  return { displayName, pronouns, race, gender, role, seeking, dataCenter, bio };
}

async function requireProfile(env: Env, deviceKeyId: string): Promise<ProfileRow> {
  const row = await profileByDevice(env, deviceKeyId);
  if (!row) throw new RelayError("not_found");
  return row;
}

/** A profile another player may see: not their own, not hidden, still listed, not blocked either way. */
async function visibleTo(env: Env, row: ProfileRow | null, viewer: string): Promise<ProfileRow | null> {
  if (!row || row.device_key_id === viewer || row.hidden === 1 || !isListed(row)) return null;
  if (await isBlockedEitherWay(env, row.device_key_id, viewer)) return null;
  return row;
}

function sideOf(match: MatchRow, device: string): "a" | "b" | null {
  return match.device_a === device ? "a" : match.device_b === device ? "b" : null;
}

async function matchView(env: Env, match: MatchRow, device: string) {
  const side = sideOf(match, device)!;
  const peerProfileId = side === "a" ? match.profile_b : match.profile_a;
  const peerDevice = side === "a" ? match.device_b : match.device_a;
  const live = await profileById(env, peerProfileId);
  const peer: ProfileView = live && live.device_key_id === peerDevice
    ? profileView(live)
    : (JSON.parse(side === "a" ? match.snapshot_b : match.snapshot_a) as ProfileView);
  return {
    matchId: match.match_id,
    side,
    createdAt: match.created_at,
    endedAt: match.ended_at,
    peer,
    peerMatchPublicKey: JSON.parse(side === "a" ? match.key_b : match.key_a) as EcPublicKeyJwk,
  };
}

async function deletePicture(env: Env, key: string | null): Promise<void> {
  if (key) await env.RELAY_CATALOG_BUCKET.delete(`lfp-pictures/${key}`);
}

// ---- profile ---------------------------------------------------------------------------------------------------

export async function lfpMe(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId, publicKeyJwk, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredOrBodyKey(env), READ_ONLY);
  const body = asRecord(bodyJson);
  if (body.type !== "lfp-me" || body.schemaVersion !== 1) throw new RelayError("invalid_request");
  await enforceQuota(env, "lfpRead", deviceKeyId);
  await rememberDeviceKey(env, publicKeyJwk);
  const row = await profileByDevice(env, deviceKeyId);
  return json({ type: "lfp-me-result", profile: row ? ownProfileView(row) : null });
}

export async function putProfile(request: Request, env: Env): Promise<Response> {
  await assertCircuitBreakerClosed(env);
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  const body = asRecord(bodyJson);
  if (body.type !== "lfp-profile" || body.schemaVersion !== 1 || body.adultAttested !== true) throw new RelayError("invalid_request");
  if (typeof body.visibility !== "string" || !LFP_VISIBILITY.has(body.visibility)) throw new RelayError("invalid_request");
  if (typeof body.hidden !== "boolean" || !isEcPublicKeyJwk(body.matchPublicKey)) throw new RelayError("invalid_request");
  const fields = readFields(body.fields);
  await enforceQuota(env, "lfpProfileWrite", deviceKeyId);

  const now = nowSeconds();
  const matchKey = toCanonicalJson(body.matchPublicKey);
  const updated = await env.RELAY_DB.prepare(
    `UPDATE lfp_profiles SET visibility = ?2, hidden = ?3, display_name = ?4, pronouns = ?5, race = ?6, gender = ?7, role = ?8,
       seeking = ?9, data_center = ?10, bio = ?11, match_public_key = ?12, adult_attested_at = ?13, updated_at = ?13, active_at = ?13
     WHERE device_key_id = ?1`,
  )
    .bind(deviceKeyId, body.visibility, body.hidden ? 1 : 0, fields.displayName, fields.pronouns, fields.race, fields.gender,
      fields.role, fields.seeking, fields.dataCenter, fields.bio, matchKey, now)
    .run();
  if (updated.meta.changes === 0) {
    await env.RELAY_DB.prepare(
      `INSERT INTO lfp_profiles (profile_id, device_key_id, code, visibility, hidden, display_name, pronouns, race, gender, role,
         seeking, data_center, bio, match_public_key, adult_attested_at, created_at, updated_at, active_at)
       VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?15, ?15, ?15)`,
    )
      .bind(randomHex(16), deviceKeyId, randomProfileCode(), body.visibility, body.hidden ? 1 : 0, fields.displayName, fields.pronouns,
        fields.race, fields.gender, fields.role, fields.seeking, fields.dataCenter, fields.bio, matchKey, now)
      .run();
  }
  return json({ type: "lfp-profile-result", profile: ownProfileView(await requireProfile(env, deviceKeyId)) });
}

export async function refreshProfile(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "lfpProfileWrite", deviceKeyId);
  const now = nowSeconds();
  // Within the hour it changes nothing a browser could notice, so it isn't written.
  await env.RELAY_DB.prepare(`UPDATE lfp_profiles SET active_at = ?2 WHERE device_key_id = ?1 AND active_at < ?2 - 3600`)
    .bind(deviceKeyId, now)
    .run();
  return json({ type: "lfp-profile-result", profile: ownProfileView(await requireProfile(env, deviceKeyId)) });
}

export async function newProfileCode(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "lfpProfileWrite", deviceKeyId);
  const result = await env.RELAY_DB.prepare(`UPDATE lfp_profiles SET code = ?2, updated_at = ?3 WHERE device_key_id = ?1`)
    .bind(deviceKeyId, randomProfileCode(), nowSeconds())
    .run();
  if (result.meta.changes === 0) throw new RelayError("not_found");
  return json({ type: "lfp-profile-result", profile: ownProfileView(await requireProfile(env, deviceKeyId)) });
}

export async function deleteProfile(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "lfpProfileWrite", deviceKeyId);
  const row = await requireProfile(env, deviceKeyId);
  await env.RELAY_DB.batch([
    env.RELAY_DB.prepare(`DELETE FROM lfp_likes WHERE from_profile = ?1 OR to_profile = ?1`).bind(row.profile_id),
    env.RELAY_DB.prepare(`DELETE FROM lfp_profiles WHERE profile_id = ?1`).bind(row.profile_id),
  ]);
  await deletePicture(env, row.picture_key);
  return json({ type: "lfp-deleted" });
}

function sniffPicture(bytes: Uint8Array, contentType: string): boolean {
  if (contentType === "image/png") return [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a].every((b, i) => bytes[i] === b);
  if (contentType === "image/jpeg") return bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff;
  return false;
}

export async function putPicture(request: Request, env: Env): Promise<Response> {
  await assertCircuitBreakerClosed(env);
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  const body = asRecord(bodyJson);
  if (body.type !== "lfp-picture" || body.schemaVersion !== 1) throw new RelayError("invalid_request");
  if ((body.contentType !== "image/png" && body.contentType !== "image/jpeg") || typeof body.dataBase64Url !== "string") {
    throw new RelayError("invalid_request");
  }
  if (!/^[A-Za-z0-9_-]+$/.test(body.dataBase64Url)) throw new RelayError("invalid_request");
  const bytes = base64UrlToBytes(body.dataBase64Url);
  if (bytes.byteLength > LFP_PICTURE_MAX_BYTES) throw new RelayError("payload_too_large");
  if (!sniffPicture(bytes, body.contentType)) throw new RelayError("invalid_request");
  const row = await requireProfile(env, deviceKeyId);
  await enforceQuota(env, "lfpPictureUpload", deviceKeyId);

  const key = bytesToBase64Url(crypto.getRandomValues(new Uint8Array(32)));
  await env.RELAY_CATALOG_BUCKET.put(`lfp-pictures/${key}`, bytes, { httpMetadata: { contentType: body.contentType } });
  await env.RELAY_DB.prepare(`UPDATE lfp_profiles SET picture_key = ?2, picture_type = ?3, updated_at = ?4 WHERE profile_id = ?1`)
    .bind(row.profile_id, key, body.contentType, nowSeconds())
    .run();
  await deletePicture(env, row.picture_key);
  return json({ type: "lfp-profile-result", profile: ownProfileView(await requireProfile(env, deviceKeyId)) });
}

export async function removePicture(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "lfpProfileWrite", deviceKeyId);
  const row = await requireProfile(env, deviceKeyId);
  await env.RELAY_DB.prepare(`UPDATE lfp_profiles SET picture_key = NULL, picture_type = NULL, updated_at = ?2 WHERE profile_id = ?1`)
    .bind(row.profile_id, nowSeconds())
    .run();
  await deletePicture(env, row.picture_key);
  return json({ type: "lfp-profile-result", profile: ownProfileView(await requireProfile(env, deviceKeyId)) });
}

/** Unsigned: the random key is the capability, and anyone who sees a profile may see its picture. */
export async function fetchPicture(env: Env, key: string): Promise<Response> {
  if (!PICTURE_KEY.test(key)) throw new RelayError("not_found");
  const object = await env.RELAY_CATALOG_BUCKET.get(`lfp-pictures/${key}`);
  if (!object) throw new RelayError("not_found");
  return new Response(object.body, {
    headers: {
      "content-type": object.httpMetadata?.contentType === "image/jpeg" ? "image/jpeg" : "image/png",
      "cache-control": "public, max-age=3600",
      "x-content-type-options": "nosniff",
      "content-security-policy": "default-src 'none'",
    },
  });
}

// ---- browse and lookup -----------------------------------------------------------------------------------------

export async function browse(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env), READ_ONLY);
  await enforceQuota(env, "lfpBrowse", deviceKeyId);
  const params = new URL(request.url).searchParams;
  const mine = await profileByDevice(env, deviceKeyId);
  const now = nowSeconds();

  const where = [
    `p.visibility = 'public'`, `p.hidden = 0`, `p.active_at > ?`, `p.device_key_id != ?`,
    `NOT EXISTS (SELECT 1 FROM lfp_blocks b WHERE (b.blocker_device = ? AND b.blocked_device = p.device_key_id)
       OR (b.blocker_device = p.device_key_id AND b.blocked_device = ?))`,
    `NOT EXISTS (SELECT 1 FROM lfp_matches m WHERE m.ended_at IS NULL AND ((m.device_a = ? AND m.device_b = p.device_key_id)
       OR (m.device_b = ? AND m.device_a = p.device_key_id)))`,
  ];
  const binds: unknown[] = [now - LFP_LISTED_FOR_SECONDS, deviceKeyId, deviceKeyId, deviceKeyId, deviceKeyId, deviceKeyId];
  if (mine) {
    where.push(`NOT EXISTS (SELECT 1 FROM lfp_likes l WHERE l.from_profile = ? AND l.to_profile = p.profile_id)`);
    binds.push(mine.profile_id);
  }

  const filter = (name: string, set: ReadonlySet<string>): string | null => {
    const v = params.get(name);
    if (v === null || v === "") return null;
    if (!set.has(v)) throw new RelayError("invalid_request");
    return v;
  };
  const role = filter("role", LFP_ROLES);
  // A Switch fits a search for either role, and someone seeking either fits any search.
  if (role) { where.push(role === "switch" ? `p.role = ?` : `(p.role = ? OR p.role = 'switch')`); binds.push(role); }
  const seeking = filter("seeking", LFP_SEEKING);
  if (seeking) { where.push(seeking === "either" ? `p.seeking = ?` : `(p.seeking = ? OR p.seeking = 'either')`); binds.push(seeking); }
  const dataCenter = filter("dataCenter", LFP_DATA_CENTERS);
  if (dataCenter) { where.push(`(p.data_center = ? OR p.data_center IS NULL)`); binds.push(dataCenter); }
  const race = filter("race", LFP_RACES);
  if (race) { where.push(`p.race = ?`); binds.push(race); }
  const gender = filter("gender", LFP_GENDERS);
  if (gender) { where.push(`p.gender = ?`); binds.push(gender); }

  const cursor = params.get("cursor");
  if (cursor) {
    const m = /^(\d{1,12})\.([0-9a-f]{32})$/.exec(cursor);
    if (!m) throw new RelayError("invalid_request");
    where.push(`(p.active_at < ? OR (p.active_at = ? AND p.profile_id < ?))`);
    binds.push(Number(m[1]), Number(m[1]), m[2]);
  }

  const { results } = await env.RELAY_DB.prepare(
    `SELECT p.* FROM lfp_profiles p WHERE ${where.join(" AND ")} ORDER BY p.active_at DESC, p.profile_id DESC LIMIT ${LFP_BROWSE_PAGE_SIZE + 1}`,
  )
    .bind(...binds)
    .all<ProfileRow>();
  const page = results.slice(0, LFP_BROWSE_PAGE_SIZE);
  const last = page[page.length - 1];
  const nextCursor = results.length > LFP_BROWSE_PAGE_SIZE && last ? `${last.active_at}.${last.profile_id}` : null;
  return json({ type: "lfp-browse-result", profiles: page.map(profileView), nextCursor });
}

export async function lookupCode(request: Request, env: Env, rawCode: string): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env), READ_ONLY);
  await enforceQuota(env, "lfpCodeLookup", deviceKeyId);
  const code = normalizeProfileCode(decodeURIComponent(rawCode));
  if (!CODE.test(code)) throw new RelayError("not_found");
  const row = await env.RELAY_DB.prepare(`SELECT * FROM lfp_profiles WHERE code = ?1`).bind(code).first<ProfileRow>();
  const visible = await visibleTo(env, row, deviceKeyId);
  if (!visible) throw new RelayError("not_found");
  return json({ type: "lfp-profile-result", profile: profileView(visible) });
}

// ---- likes and matches -----------------------------------------------------------------------------------------

export async function like(request: Request, env: Env, profileId: string): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  if (!PROFILE_ID.test(profileId)) throw new RelayError("not_found");
  const mine = await profileByDevice(env, deviceKeyId);
  if (!mine) throw new RelayError("invalid_request");
  const target = await visibleTo(env, await profileById(env, profileId), deviceKeyId);
  if (!target) throw new RelayError("not_found");
  await enforceQuota(env, "lfpLike", deviceKeyId);

  const now = nowSeconds();
  await env.RELAY_DB.prepare(`INSERT INTO lfp_likes (from_profile, to_profile, created_at) VALUES (?1, ?2, ?3) ON CONFLICT DO NOTHING`)
    .bind(mine.profile_id, target.profile_id, now)
    .run();
  const reverse = await env.RELAY_DB.prepare(`SELECT 1 AS hit FROM lfp_likes WHERE from_profile = ?1 AND to_profile = ?2`)
    .bind(target.profile_id, mine.profile_id)
    .first<{ hit: number }>();
  if (!reverse) return json({ type: "lfp-like-result", matched: false });

  const [a, b] = mine.device_key_id < target.device_key_id ? [mine, target] : [target, mine];
  const inserted = await env.RELAY_DB.prepare(
    `INSERT INTO lfp_matches (match_id, device_a, device_b, profile_a, profile_b, key_a, key_b, snapshot_a, snapshot_b, created_at)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10) ON CONFLICT DO NOTHING`,
  )
    .bind(randomHex(32), a.device_key_id, b.device_key_id, a.profile_id, b.profile_id, a.match_public_key, b.match_public_key,
      JSON.stringify(profileView(a)), JSON.stringify(profileView(b)), now)
    .run();
  await env.RELAY_DB.prepare(`DELETE FROM lfp_likes WHERE (from_profile = ?1 AND to_profile = ?2) OR (from_profile = ?2 AND to_profile = ?1)`)
    .bind(mine.profile_id, target.profile_id)
    .run();
  const match = await env.RELAY_DB.prepare(`SELECT * FROM lfp_matches WHERE device_a = ?1 AND device_b = ?2 AND ended_at IS NULL`)
    .bind(a.device_key_id, b.device_key_id)
    .first<MatchRow>();
  if (!match) throw new RelayError("service_unavailable", 5);
  if (inserted.meta.changes > 0) await notifyLfp(env, [a.device_key_id, b.device_key_id]);
  return json({ type: "lfp-like-result", matched: true, match: await matchView(env, match, deviceKeyId) });
}

export async function unlike(request: Request, env: Env, profileId: string): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  if (!PROFILE_ID.test(profileId)) throw new RelayError("not_found");
  await enforceQuota(env, "lfpOps", deviceKeyId);
  const mine = await requireProfile(env, deviceKeyId);
  await env.RELAY_DB.prepare(`DELETE FROM lfp_likes WHERE from_profile = ?1 AND to_profile = ?2`).bind(mine.profile_id, profileId).run();
  return json({ type: "lfp-deleted" });
}

export async function listLikes(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env), READ_ONLY);
  await enforceQuota(env, "lfpRead", deviceKeyId);
  const mine = await profileByDevice(env, deviceKeyId);
  if (!mine) return json({ type: "lfp-likes-result", profiles: [] });
  const { results } = await env.RELAY_DB.prepare(
    `SELECT p.* FROM lfp_likes l JOIN lfp_profiles p ON p.profile_id = l.to_profile WHERE l.from_profile = ?1 ORDER BY l.created_at DESC`,
  )
    .bind(mine.profile_id)
    .all<ProfileRow>();
  return json({ type: "lfp-likes-result", profiles: results.map(profileView) });
}

export async function listMatches(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env), READ_ONLY);
  await enforceQuota(env, "lfpRead", deviceKeyId);
  const { results } = await env.RELAY_DB.prepare(
    `SELECT * FROM lfp_matches WHERE (device_a = ?1 OR device_b = ?1) ORDER BY created_at DESC`,
  )
    .bind(deviceKeyId)
    .all<MatchRow>();
  const matches = await Promise.all(results.map((m) => matchView(env, m, deviceKeyId)));
  return json({ type: "lfp-matches-result", matches });
}

async function endMatches(env: Env, matches: readonly MatchRow[]): Promise<void> {
  if (matches.length === 0) return;
  const now = nowSeconds();
  await env.RELAY_DB.batch(matches.map((m) =>
    env.RELAY_DB.prepare(`UPDATE lfp_matches SET ended_at = ?2 WHERE match_id = ?1 AND ended_at IS NULL`).bind(m.match_id, now)));
  await Promise.all(matches.map((m) => purgeMatchChat(env, m)));
  await notifyLfp(env, [...new Set(matches.flatMap((m) => [m.device_a, m.device_b]))]);
}

export async function unmatch(request: Request, env: Env, matchId: string): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  if (!MATCH_ID.test(matchId)) throw new RelayError("not_found");
  await enforceQuota(env, "lfpOps", deviceKeyId);
  const match = await matchById(env, matchId);
  if (!match || !sideOf(match, deviceKeyId)) throw new RelayError("not_found");
  if (match.ended_at === null) await endMatches(env, [match]);
  return json({ type: "lfp-deleted" });
}

// ---- blocks ----------------------------------------------------------------------------------------------------

export async function block(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  const body = asRecord(bodyJson);
  if (body.type !== "lfp-block" || body.schemaVersion !== 1) throw new RelayError("invalid_request");
  const byProfile = typeof body.profileId === "string" && PROFILE_ID.test(body.profileId) ? body.profileId : null;
  const byMatch = typeof body.matchId === "string" && MATCH_ID.test(body.matchId) ? body.matchId : null;
  if ((byProfile === null) === (byMatch === null)) throw new RelayError("invalid_request");
  await enforceQuota(env, "lfpBlock", deviceKeyId);

  let target: { device: string; displayName: string } | null = null;
  if (byProfile) {
    const row = await profileById(env, byProfile);
    if (row) target = { device: row.device_key_id, displayName: row.display_name };
  } else {
    const match = await matchById(env, byMatch!);
    const side = match ? sideOf(match, deviceKeyId) : null;
    if (match && side) {
      const snapshot = JSON.parse(side === "a" ? match.snapshot_b : match.snapshot_a) as ProfileView;
      target = { device: side === "a" ? match.device_b : match.device_a, displayName: snapshot.fields.displayName };
    }
  }
  if (!target || target.device === deviceKeyId) throw new RelayError("not_found");

  const now = nowSeconds();
  await env.RELAY_DB.prepare(
    `INSERT INTO lfp_blocks (block_id, blocker_device, blocked_device, display_name, created_at) VALUES (?1, ?2, ?3, ?4, ?5)
     ON CONFLICT DO NOTHING`,
  )
    .bind(randomHex(16), deviceKeyId, target.device, target.displayName, now)
    .run();
  const [mine, theirs] = await Promise.all([profileByDevice(env, deviceKeyId), profileByDevice(env, target.device)]);
  if (mine && theirs) {
    await env.RELAY_DB.prepare(`DELETE FROM lfp_likes WHERE (from_profile = ?1 AND to_profile = ?2) OR (from_profile = ?2 AND to_profile = ?1)`)
      .bind(mine.profile_id, theirs.profile_id)
      .run();
  }
  const [a, b] = deviceKeyId < target.device ? [deviceKeyId, target.device] : [target.device, deviceKeyId];
  const { results } = await env.RELAY_DB.prepare(`SELECT * FROM lfp_matches WHERE device_a = ?1 AND device_b = ?2 AND ended_at IS NULL`)
    .bind(a, b)
    .all<MatchRow>();
  await endMatches(env, results);
  return json({ type: "lfp-deleted" });
}

export async function unblock(request: Request, env: Env, blockId: string): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  if (!BLOCK_ID.test(blockId)) throw new RelayError("not_found");
  await enforceQuota(env, "lfpOps", deviceKeyId);
  await env.RELAY_DB.prepare(`DELETE FROM lfp_blocks WHERE block_id = ?1 AND blocker_device = ?2`).bind(blockId, deviceKeyId).run();
  return json({ type: "lfp-deleted" });
}

export async function listBlocks(request: Request, env: Env): Promise<Response> {
  const { deviceKeyId } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env), READ_ONLY);
  await enforceQuota(env, "lfpRead", deviceKeyId);
  const { results } = await env.RELAY_DB.prepare(
    `SELECT block_id, display_name, created_at FROM lfp_blocks WHERE blocker_device = ?1 ORDER BY created_at DESC`,
  )
    .bind(deviceKeyId)
    .all<{ block_id: string; display_name: string; created_at: number }>();
  return json({
    type: "lfp-blocks-result",
    blocks: results.map((r) => ({ blockId: r.block_id, displayName: r.display_name, createdAt: r.created_at })),
  });
}
