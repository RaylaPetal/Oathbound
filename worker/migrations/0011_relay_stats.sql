-- One row of relay-wide numbers, written only by the cron and read with the pair lookup, so showing them costs
-- no extra request and one row read. active_devices counts distinct device keys that signed a request inside the
-- activity window; it identifies nobody.
CREATE TABLE relay_stats (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  active_devices INTEGER NOT NULL,
  computed_at INTEGER NOT NULL
);
