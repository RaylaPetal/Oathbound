-- collar/pairing (cloud-pairing-lifecycle): code-based invitations share the invitations table with the
-- original tell-referenced ones, told apart by `kind`. A code invitation carries each side's character
-- fields only as AES-GCM ciphertext under a key derived from the pairing code (which the relay never has),
-- and adds two end states: `cancelled` (inviter withdrew an unused code) and `rejected` (inviter declined
-- the character that accepted). SQLite can't widen a CHECK constraint in place, so the table is rebuilt;
-- every existing row carries over as kind 'tell' with its status unchanged.
CREATE TABLE invitations_new (
  invitation_id_hash TEXT PRIMARY KEY,
  kind TEXT NOT NULL DEFAULT 'tell' CHECK (kind IN ('tell', 'code')),
  inviter_device_key_id TEXT NOT NULL,
  inviter_public_key_jwk TEXT NOT NULL,
  role TEXT NOT NULL CHECK (role IN ('owner', 'sub')),
  trigger_phrase TEXT,
  encrypted_inviter_nonce TEXT,
  encrypted_inviter_ciphertext TEXT,
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  signature TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'accepted', 'consumed', 'expired', 'cancelled', 'rejected')),
  accepter_device_key_id TEXT,
  accepter_public_key_jwk TEXT,
  proof_digest TEXT,
  accepter_created_at INTEGER,
  accepter_expires_at INTEGER,
  accepter_signature TEXT,
  accepter_role TEXT CHECK (accepter_role IN ('owner', 'sub')),
  accepter_trigger_phrase TEXT,
  encrypted_accepter_nonce TEXT,
  encrypted_accepter_ciphertext TEXT,
  accepted_at INTEGER,
  consumed_at INTEGER
);
INSERT INTO invitations_new (
  invitation_id_hash, kind, inviter_device_key_id, inviter_public_key_jwk, role, trigger_phrase, created_at, expires_at,
  signature, status, accepter_device_key_id, accepter_public_key_jwk, proof_digest, accepter_created_at,
  accepter_expires_at, accepter_signature, accepter_role, accepter_trigger_phrase, accepted_at, consumed_at)
SELECT
  invitation_id_hash, 'tell', inviter_device_key_id, inviter_public_key_jwk, role, trigger_phrase, created_at, expires_at,
  signature, status, accepter_device_key_id, accepter_public_key_jwk, proof_digest, accepter_created_at,
  accepter_expires_at, accepter_signature, accepter_role, accepter_trigger_phrase, accepted_at, consumed_at
FROM invitations;
DROP TABLE invitations;
ALTER TABLE invitations_new RENAME TO invitations;
CREATE INDEX idx_invitations_expires_at ON invitations (expires_at);

-- collar/pairing-recovery: one encrypted backup per recovery code. Keyed by SHA-256 of the backupId the
-- client derives from its recovery code (the relay never sees the code or the backupId itself), bound to
-- the device key that first wrote it so only that identity can replace or delete it. The ciphertext is
-- opaque to the relay: identity, pairings and character names are all inside it.
CREATE TABLE backups (
  backup_id_hash TEXT PRIMARY KEY,
  device_key_id TEXT NOT NULL,
  nonce TEXT NOT NULL,
  ciphertext TEXT NOT NULL,
  updated_at INTEGER NOT NULL
);
