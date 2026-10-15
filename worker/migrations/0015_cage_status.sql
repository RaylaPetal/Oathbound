-- The Sub's latest cage report for this pair epoch, read by the Owner through the pair lookup.
-- No index: rows are only ever reached by their primary key.
ALTER TABLE pairs ADD COLUMN cage_state TEXT CHECK (cage_state IN ('none', 'armed', 'caged'));
ALTER TABLE pairs ADD COLUMN cage_state_at INTEGER;
ALTER TABLE pairs ADD COLUMN cage_ends_at INTEGER;
