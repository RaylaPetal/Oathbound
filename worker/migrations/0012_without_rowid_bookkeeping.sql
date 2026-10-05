-- D1 bills a composite primary key's autoindex as a second row on every insert. As WITHOUT ROWID tables the primary
-- key is the table itself, so each nonce and each new quota counter costs one row written instead of two. Existing
-- rows are copied so the replay window and the in-window counters survive the deploy.
CREATE TABLE nonces_new (
  device_key_id TEXT NOT NULL,
  nonce TEXT NOT NULL,
  seen_at INTEGER NOT NULL,
  PRIMARY KEY (device_key_id, nonce)
) WITHOUT ROWID;
INSERT INTO nonces_new (device_key_id, nonce, seen_at) SELECT device_key_id, nonce, seen_at FROM nonces;
DROP TABLE nonces;
ALTER TABLE nonces_new RENAME TO nonces;

CREATE TABLE quota_counters_new (
  scope TEXT NOT NULL,
  window_start INTEGER NOT NULL,
  count INTEGER NOT NULL DEFAULT 0,
  bytes INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (scope, window_start)
) WITHOUT ROWID;
INSERT INTO quota_counters_new (scope, window_start, count, bytes) SELECT scope, window_start, count, bytes FROM quota_counters;
DROP TABLE quota_counters;
ALTER TABLE quota_counters_new RENAME TO quota_counters;
