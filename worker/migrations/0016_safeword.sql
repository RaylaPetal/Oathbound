-- When the pair's Sub last used their safeword, read by the Owner through the pair lookup.
-- No index: rows are only ever reached by their primary key.
ALTER TABLE pairs ADD COLUMN safeword_at INTEGER;
ALTER TABLE pairs ADD COLUMN safeword_mood TEXT CHECK (safeword_mood IN ('stop', 'checkin'));
