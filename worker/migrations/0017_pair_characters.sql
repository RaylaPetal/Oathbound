-- collar/alt-characters: each member's latest encrypted characters record (linked characters and the one in use),
-- written by that member and read by the other through the pair lookups. The relay can't read it. *_seq only grows,
-- so a delayed or replayed upload can't roll a record back. No index: rows are only ever reached by their primary key.
ALTER TABLE pairs ADD COLUMN owner_characters TEXT;
ALTER TABLE pairs ADD COLUMN owner_characters_seq INTEGER;
ALTER TABLE pairs ADD COLUMN sub_characters TEXT;
ALTER TABLE pairs ADD COLUMN sub_characters_seq INTEGER;
