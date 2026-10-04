import raw from "../../../protocol/constants.json";

interface ProtocolConstants {
  protocolVersion: number;
  requestSigning: {
    timestampToleranceSeconds: number;
    nonce: { replayWindowSeconds: number };
  };
  sizeAndExpiryLimits: {
    invitationExpirySeconds: number;
    catalogRequestExpirySeconds: number;
    catalogObjectExpirySeconds: number;
    revocationRetentionSecondsMax: number;
    catalogPlaintextMaxBytes: number;
    catalogCiphertextMaxBytes: number;
    envelopeMaxBytes: number;
    revocationPollMinIntervalSeconds: number;
    catalogMailboxExpirySeconds: number;
    catalogMailboxMinUploadIntervalSeconds: number;
    catalogMailboxOwnerPollIntervalSeconds: number;
    codeInvitationExpirySeconds: number;
    pairStatusPollIntervalSeconds: number;
    backupCiphertextMaxBytes: number;
    collarCheckinIntervalSeconds: number;
    collarBrokenGraceSeconds: number;
    collarStaleSeconds: number;
    rulebookMailboxExpirySeconds: number;
    rulebookCiphertextMaxBytes: number;
    rulebookReportCiphertextMaxBytes: number;
    rulebookMinUploadIntervalSeconds: number;
    rulebookReportMinUploadIntervalSeconds: number;
  };
}

export const PROTOCOL = raw as unknown as ProtocolConstants;

export const TIMESTAMP_TOLERANCE_SECONDS = PROTOCOL.requestSigning.timestampToleranceSeconds;
export const NONCE_REPLAY_WINDOW_SECONDS = PROTOCOL.requestSigning.nonce.replayWindowSeconds;
// Nonces are kept past the replay window so they also show which devices were recently active. Clients check in
// about every pairStatusPollIntervalSeconds, so the activity window has to span one full interval plus slack.
export const ACTIVE_DEVICE_WINDOW_SECONDS = PROTOCOL.sizeAndExpiryLimits.pairStatusPollIntervalSeconds + 300;
export const NONCE_RETENTION_SECONDS = Math.max(NONCE_REPLAY_WINDOW_SECONDS, ACTIVE_DEVICE_WINDOW_SECONDS);
export const INVITATION_EXPIRY_SECONDS = PROTOCOL.sizeAndExpiryLimits.invitationExpirySeconds;
export const CATALOG_REQUEST_EXPIRY_SECONDS = PROTOCOL.sizeAndExpiryLimits.catalogRequestExpirySeconds;
export const CATALOG_OBJECT_EXPIRY_SECONDS = PROTOCOL.sizeAndExpiryLimits.catalogObjectExpirySeconds;
export const REVOCATION_RETENTION_SECONDS_MAX = PROTOCOL.sizeAndExpiryLimits.revocationRetentionSecondsMax;
export const CATALOG_CIPHERTEXT_MAX_BYTES = PROTOCOL.sizeAndExpiryLimits.catalogCiphertextMaxBytes;
export const ENVELOPE_MAX_BYTES = PROTOCOL.sizeAndExpiryLimits.envelopeMaxBytes;
// Base64url expands ciphertext by 4/3; leave one envelope allowance for signed metadata/JSON syntax.
export const SIGNED_REQUEST_MAX_BYTES = Math.ceil(CATALOG_CIPHERTEXT_MAX_BYTES / 3) * 4 + ENVELOPE_MAX_BYTES;
export const REVOCATION_POLL_MIN_INTERVAL_SECONDS = PROTOCOL.sizeAndExpiryLimits.revocationPollMinIntervalSeconds;
export const CATALOG_MAILBOX_EXPIRY_SECONDS = PROTOCOL.sizeAndExpiryLimits.catalogMailboxExpirySeconds;
export const CATALOG_MAILBOX_MIN_UPLOAD_INTERVAL_SECONDS = PROTOCOL.sizeAndExpiryLimits.catalogMailboxMinUploadIntervalSeconds;

export const CODE_INVITATION_EXPIRY_SECONDS = PROTOCOL.sizeAndExpiryLimits.codeInvitationExpirySeconds;
export const BACKUP_CIPHERTEXT_MAX_BYTES = PROTOCOL.sizeAndExpiryLimits.backupCiphertextMaxBytes;

export const RULEBOOK_MAILBOX_EXPIRY_SECONDS = PROTOCOL.sizeAndExpiryLimits.rulebookMailboxExpirySeconds;
export const RULEBOOK_CIPHERTEXT_MAX_BYTES = PROTOCOL.sizeAndExpiryLimits.rulebookCiphertextMaxBytes;
export const RULEBOOK_REPORT_CIPHERTEXT_MAX_BYTES = PROTOCOL.sizeAndExpiryLimits.rulebookReportCiphertextMaxBytes;
export const RULEBOOK_MIN_UPLOAD_INTERVAL_SECONDS = PROTOCOL.sizeAndExpiryLimits.rulebookMinUploadIntervalSeconds;
export const RULEBOOK_REPORT_MIN_UPLOAD_INTERVAL_SECONDS = PROTOCOL.sizeAndExpiryLimits.rulebookReportMinUploadIntervalSeconds;

export function nowSeconds(): number {
  return Math.floor(Date.now() / 1000);
}
