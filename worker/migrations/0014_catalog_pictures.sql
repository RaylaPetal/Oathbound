-- catalog-pictures: one row per encrypted restraint picture a pair's Sub uploaded. The blob lives in R2 under
-- pictures/<pair_id_hash>/<pair_epoch>/<picture_id>; the relay never sees which restraint it belongs to or its key.
-- WITHOUT ROWID so each insert costs one row written; the cron's per-pair cleanup walks the primary key.
CREATE TABLE catalog_pictures (
  pair_id_hash TEXT NOT NULL,
  pair_epoch INTEGER NOT NULL,
  picture_id TEXT NOT NULL,
  size_bytes INTEGER NOT NULL,
  created_at INTEGER NOT NULL,
  last_referenced_at INTEGER NOT NULL,
  unreferenced_since INTEGER,
  PRIMARY KEY (pair_id_hash, pair_epoch, picture_id)
) WITHOUT ROWID;
