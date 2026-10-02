-- The Sub's latest collar report for this pair epoch, read by the Owner through the pair lookup.
-- No index: rows are only ever reached by their primary key.
ALTER TABLE pairs ADD COLUMN collar_state TEXT CHECK (collar_state IN ('locked', 'unlocked', 'broken'));
ALTER TABLE pairs ADD COLUMN collar_state_at INTEGER;
ALTER TABLE pairs ADD COLUMN collar_checkin_at INTEGER;
