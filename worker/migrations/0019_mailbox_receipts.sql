-- Delivery receipts and sender statuses for every mailbox: the latest signed mailbox-status per (pair epoch,
-- mailbox, role), stored verbatim so the other member verifies the poster's own signature. A status only replaces
-- one with an older or equal `at`, so a late retry can't roll it back. WITHOUT ROWID so each upsert writes one row.
CREATE TABLE mailbox_status (
  pair_id_hash TEXT NOT NULL,
  pair_epoch INTEGER NOT NULL,
  mailbox TEXT NOT NULL CHECK (mailbox IN ('catalog', 'rulebook', 'report')),
  role TEXT NOT NULL CHECK (role IN ('sender', 'recipient')),
  sequence INTEGER NOT NULL,
  code TEXT NOT NULL,
  at INTEGER NOT NULL,
  envelope TEXT NOT NULL,
  PRIMARY KEY (pair_id_hash, pair_epoch, mailbox, role)
) WITHOUT ROWID;

-- The item a consume handed out stays here, with the next key the consume rotated to, until the recipient's
-- receipt, a newer pickup, a key publish, its own expiry or revocation releases it. A consume that retries with the
-- same next key (its response was lost) gets the same item again. No index: the cron's expiry scan is rare and
-- small, and an index would add a write to every consume.
ALTER TABLE catalog_mailboxes ADD COLUMN handed_snapshot_id INTEGER;
ALTER TABLE catalog_mailboxes ADD COLUMN handed_r2_key TEXT;
ALTER TABLE catalog_mailboxes ADD COLUMN handed_envelope TEXT;
ALTER TABLE catalog_mailboxes ADD COLUMN handed_expires_at INTEGER;
ALTER TABLE catalog_mailboxes ADD COLUMN handed_next_key_id TEXT;
-- When the Owner last asked for the latest catalog (a refresh-flagged key publish); limits it to once a minute.
ALTER TABLE catalog_mailboxes ADD COLUMN refresh_requested_at INTEGER;

ALTER TABLE rulebook_mailboxes ADD COLUMN handed_sequence INTEGER;
ALTER TABLE rulebook_mailboxes ADD COLUMN handed_r2_key TEXT;
ALTER TABLE rulebook_mailboxes ADD COLUMN handed_envelope TEXT;
ALTER TABLE rulebook_mailboxes ADD COLUMN handed_expires_at INTEGER;
ALTER TABLE rulebook_mailboxes ADD COLUMN handed_next_key_id TEXT;
