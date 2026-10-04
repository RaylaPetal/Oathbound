-- collar/rulebook: one mailbox per (pair_id_hash, pair_epoch, channel), scoped by epoch for the same mutual-pair
-- reason as 0005. channel 'rulebook' carries Owner-authored rulebooks to the Sub, channel 'report' carries the
-- Sub's activity reports to the Owner. Each row works like a catalog_mailboxes row in that direction: the
-- recipient publishes a signed receive key (stored verbatim so the sender can verify the recipient's own
-- signature), the sender leaves at most one encrypted item, and the recipient consumes it while atomically
-- rotating to a new key.
--
-- last_sequence is the highest item sequence ever accepted on the row and survives consumes and key
-- replacements, so an older upload can never be replayed over a newer one. last_upload_at drives the
-- per-channel minimum upload interval. last_consumed_sequence is the delivery receipt the sender checks.
CREATE TABLE rulebook_mailboxes (
  pair_id_hash TEXT NOT NULL,
  pair_epoch INTEGER NOT NULL,
  channel TEXT NOT NULL CHECK (channel IN ('rulebook', 'report')),
  receive_key_id TEXT NOT NULL,
  receive_key_envelope TEXT NOT NULL,
  key_published_at INTEGER NOT NULL,
  last_upload_at INTEGER,
  last_sequence INTEGER NOT NULL DEFAULT 0,
  item_sequence INTEGER,
  item_r2_key TEXT,
  item_envelope TEXT,
  item_created_at INTEGER,
  item_expires_at INTEGER,
  last_consumed_sequence INTEGER,
  PRIMARY KEY (pair_id_hash, pair_epoch, channel)
);
CREATE INDEX idx_rulebook_mailboxes_item_expires_at ON rulebook_mailboxes (item_expires_at);
