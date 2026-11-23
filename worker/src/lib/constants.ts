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
    pictureCiphertextMaxBytes: number;
    picturesPerPairMax: number;
    picturesPerPairMaxBytes: number;
    pictureUnreferencedGraceSeconds: number;
    pictureIdleExpirySeconds: number;
    pictureFetchBatchMax: number;
    pairCharactersCiphertextMaxBytes: number;
  };
  socketAuth: { authTimeoutSeconds: number };
  mailboxStatus: {
    recipientCodes: string[];
    senderCodes: string[];
    resendableCodes: string[];
    catalogMailboxRefreshMinIntervalSeconds: number;
  };
  peerMessage: {
    maxFrameBytes: number;
    maxAgeSeconds: number;
    heldFramesMax: number;
    deviceBurstFrames: number;
    deviceBurstWindowSeconds: number;
    deviceHourlyFrames: number;
  };
  peerChat: {
    maxTextChars: number;
    storedTtlSeconds: number;
    storedPerSenderPerPairMax: number;
    deviceBurstMessages: number;
    deviceBurstWindowSeconds: number;
    deviceHourlyMessages: number;
    image: {
      maxCiphertextBytes: number;
      storedPerPairMax: number;
      storedPerPairMaxBytes: number;
      storedTtlSeconds: number;
    };
  };
}

export const PROTOCOL = raw as unknown as ProtocolConstants;

export const TIMESTAMP_TOLERANCE_SECONDS = PROTOCOL.requestSigning.timestampToleranceSeconds;
export const NONCE_REPLAY_WINDOW_SECONDS = PROTOCOL.requestSigning.nonce.replayWindowSeconds;
export const NONCE_RETENTION_SECONDS = NONCE_REPLAY_WINDOW_SECONDS;
/** The active count covers devices seen today or yesterday (UTC), recomputed at most this often. */
export const ACTIVE_DEVICE_RECOMPUTE_SECONDS = 3600;
/** Pairings one pair status batch may ask about; far above any real device's pairing count. */
export const PAIR_STATUS_BATCH_MAX = 32;
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

export const MAILBOX_STATUS_RECIPIENT_CODES: ReadonlySet<string> = new Set(PROTOCOL.mailboxStatus.recipientCodes);
export const MAILBOX_STATUS_SENDER_CODES: ReadonlySet<string> = new Set(PROTOCOL.mailboxStatus.senderCodes);
export const CATALOG_MAILBOX_REFRESH_MIN_INTERVAL_SECONDS = PROTOCOL.mailboxStatus.catalogMailboxRefreshMinIntervalSeconds;

export function nowSeconds(): number {
  return Math.floor(Date.now() / 1000);
}
export const PICTURE_CIPHERTEXT_MAX_BYTES = PROTOCOL.sizeAndExpiryLimits.pictureCiphertextMaxBytes;
export const PICTURES_PER_PAIR_MAX = PROTOCOL.sizeAndExpiryLimits.picturesPerPairMax;
export const PICTURES_PER_PAIR_MAX_BYTES = PROTOCOL.sizeAndExpiryLimits.picturesPerPairMaxBytes;
export const PICTURE_UNREFERENCED_GRACE_SECONDS = PROTOCOL.sizeAndExpiryLimits.pictureUnreferencedGraceSeconds;
export const PICTURE_IDLE_EXPIRY_SECONDS = PROTOCOL.sizeAndExpiryLimits.pictureIdleExpirySeconds;
export const PICTURE_FETCH_BATCH_MAX = PROTOCOL.sizeAndExpiryLimits.pictureFetchBatchMax;
export const PAIR_CHARACTERS_CIPHERTEXT_MAX_BYTES = PROTOCOL.sizeAndExpiryLimits.pairCharactersCiphertextMaxBytes;

export const SOCKET_AUTH_TIMEOUT_SECONDS = PROTOCOL.socketAuth.authTimeoutSeconds;
export const PEER_FRAME_MAX_BYTES = PROTOCOL.peerMessage.maxFrameBytes;
export const PEER_FRAME_MAX_AGE_SECONDS = PROTOCOL.peerMessage.maxAgeSeconds;
export const HELD_FRAMES_MAX = PROTOCOL.peerMessage.heldFramesMax;
export const DEVICE_BURST_FRAMES = PROTOCOL.peerMessage.deviceBurstFrames;
export const DEVICE_BURST_WINDOW_SECONDS = PROTOCOL.peerMessage.deviceBurstWindowSeconds;
export const DEVICE_HOURLY_FRAMES = PROTOCOL.peerMessage.deviceHourlyFrames;
export const CHAT_STORED_TTL_SECONDS = PROTOCOL.peerChat.storedTtlSeconds;
export const CHAT_STORED_PER_SENDER_PER_PAIR_MAX = PROTOCOL.peerChat.storedPerSenderPerPairMax;
export const CHAT_DEVICE_BURST_MESSAGES = PROTOCOL.peerChat.deviceBurstMessages;
export const CHAT_DEVICE_BURST_WINDOW_SECONDS = PROTOCOL.peerChat.deviceBurstWindowSeconds;
export const CHAT_DEVICE_HOURLY_MESSAGES = PROTOCOL.peerChat.deviceHourlyMessages;
export const CHAT_IMAGE_MAX_CIPHERTEXT_BYTES = PROTOCOL.peerChat.image.maxCiphertextBytes;
export const CHAT_IMAGES_PER_PAIR_MAX = PROTOCOL.peerChat.image.storedPerPairMax;
export const CHAT_IMAGES_PER_PAIR_MAX_BYTES = PROTOCOL.peerChat.image.storedPerPairMaxBytes;
export const CHAT_IMAGE_TTL_SECONDS = PROTOCOL.peerChat.image.storedTtlSeconds;
