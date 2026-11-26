-- Who's online now: an install is online until online_until, which each presence ping from a logged-in client pushes
-- forward and a logout ping clears. The count is cached in one row so a ping reads it instead of counting.
ALTER TABLE device_keys ADD COLUMN online_until INTEGER;
CREATE INDEX device_keys_online ON device_keys (online_until) WHERE online_until IS NOT NULL;

CREATE TABLE relay_presence (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  online_devices INTEGER NOT NULL,
  computed_at INTEGER NOT NULL
);
