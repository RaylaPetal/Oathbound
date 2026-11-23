import type { Env } from "../env";
import { notifyPeer } from "../lib/hints";
import { resolverFromStoredDeviceKeys, verifySignedRequest } from "../lib/auth";
import {
  MAILBOX_STATUS_RECIPIENT_CODES,
  MAILBOX_STATUS_SENDER_CODES,
  TIMESTAMP_TOLERANCE_SECONDS,
  nowSeconds,
} from "../lib/constants";
import { verifyEcdsaSignature } from "../lib/crypto";
import { lookupDeviceKey } from "../lib/deviceKeys";
import { RelayError } from "../lib/errors";
import { toCanonicalJson } from "../lib/json";
import { logEvent } from "../lib/log";
import { pairAtEpoch, type PairRow } from "../lib/pairs";
import { assertCircuitBreakerClosed, enforceQuota } from "../lib/quotas";
import { asRecord, isHex64, isNonNegInt, isSignature, isUnixSeconds, requireField } from "../lib/validate";
import { highestReceiptableSnapshot, releaseHandedSnapshot } from "./mailbox";
import { highestReceiptableItem, releaseHandedRulebookItem } from "./rulebook";

export type MailboxName = "catalog" | "rulebook" | "report";
export type MailboxRole = "sender" | "recipient";

const SIDES: Record<MailboxName, Record<MailboxRole, "owner" | "sub">> = {
  catalog: { sender: "sub", recipient: "owner" },
  rulebook: { sender: "owner", recipient: "sub" },
  report: { sender: "sub", recipient: "owner" },
};

const FIELDS = ["type", "schemaVersion", "pairIdHash", "pairEpoch", "mailbox", "role", "deviceKeyId", "sequence", "code", "at", "signature"];

export interface MailboxStatusEnvelope {
  type: "mailbox-status";
  schemaVersion: 1;
  pairIdHash: string;
  pairEpoch: number;
  mailbox: MailboxName;
  role: MailboxRole;
  deviceKeyId: string;
  sequence: number;
  code: string;
  at: number;
  signature: string;
}

function isMailbox(value: unknown): value is MailboxName {
  return value === "catalog" || value === "rulebook" || value === "report";
}

function isRole(value: unknown): value is MailboxRole {
  return value === "sender" || value === "recipient";
}

export type MailboxStatuses = Partial<Record<`${MailboxName}:${MailboxRole}`, MailboxStatusEnvelope>>;

/** Every stored status of one pair epoch, verbatim, for the pair status response. */
export async function loadMailboxStatuses(env: Env, pairIdHash: string, pairEpoch: number): Promise<MailboxStatuses> {
  const rows = await env.RELAY_DB.prepare(`SELECT mailbox, role, envelope FROM mailbox_status WHERE pair_id_hash = ?1 AND pair_epoch = ?2`)
    .bind(pairIdHash, pairEpoch)
    .all<{ mailbox: MailboxName; role: MailboxRole; envelope: string }>();
  const statuses: MailboxStatuses = {};
  for (const row of rows.results) statuses[`${row.mailbox}:${row.role}`] = JSON.parse(row.envelope) as MailboxStatusEnvelope;
  return statuses;
}

async function highestReceiptable(env: Env, pair: PairRow, mailbox: MailboxName): Promise<number> {
  return mailbox === "catalog"
    ? highestReceiptableSnapshot(env, pair.pair_id_hash, pair.pair_epoch)
    : highestReceiptableItem(env, pair.pair_id_hash, pair.pair_epoch, mailbox);
}

/**
 * A member posts a signed receipt (role recipient) or sender status for one mailbox. The relay stores the latest per
 * (pair epoch, mailbox, role) for the other member to read and verify, and a receipt releases the item its consume
 * kept for retries. Codes come from a fixed list, so nothing free-form ever crosses the relay.
 */
export async function postMailboxStatus(request: Request, env: Env): Promise<Response> {
  await assertCircuitBreakerClosed(env);
  const { deviceKeyId, bodyJson } = await verifySignedRequest(request, env, resolverFromStoredDeviceKeys(env));
  await enforceQuota(env, "deviceMailboxStatus", deviceKeyId);

  const body = asRecord(bodyJson);
  if (Object.keys(body).some((key) => !FIELDS.includes(key))) throw new RelayError("invalid_request");
  const envelope: MailboxStatusEnvelope = {
    type: requireField(body, "type", (v): v is "mailbox-status" => v === "mailbox-status"),
    schemaVersion: requireField(body, "schemaVersion", (v): v is 1 => v === 1),
    pairIdHash: requireField(body, "pairIdHash", isHex64),
    pairEpoch: requireField(body, "pairEpoch", isNonNegInt),
    mailbox: requireField(body, "mailbox", isMailbox),
    role: requireField(body, "role", isRole),
    deviceKeyId: requireField(body, "deviceKeyId", isHex64),
    sequence: requireField(body, "sequence", isNonNegInt),
    code: requireField(body, "code", (v): v is string => typeof v === "string"),
    at: requireField(body, "at", isUnixSeconds),
    signature: requireField(body, "signature", isSignature),
  };
  const codes = envelope.role === "recipient" ? MAILBOX_STATUS_RECIPIENT_CODES : MAILBOX_STATUS_SENDER_CODES;
  if (!codes.has(envelope.code)) throw new RelayError("invalid_request");
  if (envelope.at > nowSeconds() + TIMESTAMP_TOLERANCE_SECONDS) throw new RelayError("invalid_request");

  const pair = await pairAtEpoch(env, envelope.pairIdHash, envelope.pairEpoch);
  // One generic answer for wrong pair, wrong device, wrong side or a revoked epoch, so a caller can't tell which.
  if (!pair || pair.revoked_at !== null || envelope.deviceKeyId !== deviceKeyId) throw new RelayError("unauthorized");
  const side = SIDES[envelope.mailbox][envelope.role];
  if ((side === "owner" ? pair.owner_device_key_id : pair.sub_device_key_id) !== deviceKeyId) throw new RelayError("unauthorized");

  // A receipt can only settle an item that was handed out, so it can't mark a later upload delivered in advance.
  if (envelope.role === "recipient" && envelope.sequence > (await highestReceiptable(env, pair, envelope.mailbox))) {
    throw new RelayError("invalid_request");
  }

  const signer = await lookupDeviceKey(env, deviceKeyId);
  const { signature, ...unsigned } = envelope;
  if (!signer || !(await verifyEcdsaSignature(signer, signature, toCanonicalJson(unsigned)))) throw new RelayError("unauthorized");

  const stored = await env.RELAY_DB.prepare(
    `INSERT INTO mailbox_status (pair_id_hash, pair_epoch, mailbox, role, sequence, code, at, envelope)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)
     ON CONFLICT (pair_id_hash, pair_epoch, mailbox, role) DO UPDATE SET
       sequence = excluded.sequence, code = excluded.code, at = excluded.at, envelope = excluded.envelope
     WHERE excluded.at > mailbox_status.at OR (excluded.at = mailbox_status.at AND excluded.sequence >= mailbox_status.sequence)`,
  )
    .bind(envelope.pairIdHash, envelope.pairEpoch, envelope.mailbox, envelope.role, envelope.sequence, envelope.code, envelope.at, toCanonicalJson(envelope))
    .run();
  const changed = (stored.meta.changes ?? 0) > 0;

  if (envelope.role === "recipient") {
    if (envelope.mailbox === "catalog") await releaseHandedSnapshot(env, envelope.pairIdHash, envelope.pairEpoch, envelope.sequence);
    else await releaseHandedRulebookItem(env, envelope.pairIdHash, envelope.pairEpoch, envelope.mailbox, envelope.sequence);
  }

  logEvent("mailbox_status", { mailbox: envelope.mailbox, role: envelope.role, code: envelope.code });
  if (changed) await notifyPeer(env, pair, deviceKeyId);
  return Response.json({ type: "mailbox-status-ack", schemaVersion: 1, stored: changed });
}
