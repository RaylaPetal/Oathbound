-- collar/looking-for-pair. Profiles are readable by the relay (others browse them) but never hold a character name or
-- world; device key ids stay here and are never returned to another player. Matches keep both members' match public
-- keys and a profile snapshot so a match outlives either profile.
CREATE TABLE lfp_profiles (
  profile_id TEXT PRIMARY KEY,
  device_key_id TEXT NOT NULL UNIQUE,
  code TEXT NOT NULL UNIQUE,
  visibility TEXT NOT NULL CHECK (visibility IN ('public', 'private')),
  hidden INTEGER NOT NULL DEFAULT 0,
  display_name TEXT NOT NULL,
  pronouns TEXT,
  race TEXT NOT NULL,
  gender TEXT NOT NULL,
  role TEXT NOT NULL,
  seeking TEXT NOT NULL,
  data_center TEXT,
  bio TEXT NOT NULL,
  picture_key TEXT,
  picture_type TEXT,
  match_public_key TEXT NOT NULL,
  adult_attested_at INTEGER NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  active_at INTEGER NOT NULL
);
CREATE INDEX lfp_profiles_browse ON lfp_profiles (visibility, hidden, active_at);
CREATE INDEX lfp_profiles_picture ON lfp_profiles (picture_key);

CREATE TABLE lfp_likes (
  from_profile TEXT NOT NULL,
  to_profile TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  PRIMARY KEY (from_profile, to_profile)
) WITHOUT ROWID;
CREATE INDEX lfp_likes_to ON lfp_likes (to_profile);

CREATE TABLE lfp_matches (
  match_id TEXT PRIMARY KEY,
  device_a TEXT NOT NULL,
  device_b TEXT NOT NULL,
  profile_a TEXT NOT NULL,
  profile_b TEXT NOT NULL,
  key_a TEXT NOT NULL,
  key_b TEXT NOT NULL,
  snapshot_a TEXT NOT NULL,
  snapshot_b TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  ended_at INTEGER
);
-- One live match per pair of devices; two simultaneous mutual likes create a single row.
CREATE UNIQUE INDEX lfp_matches_active ON lfp_matches (device_a, device_b) WHERE ended_at IS NULL;
CREATE INDEX lfp_matches_device_b ON lfp_matches (device_b);
CREATE INDEX lfp_matches_ended ON lfp_matches (ended_at);

CREATE TABLE lfp_blocks (
  block_id TEXT PRIMARY KEY,
  blocker_device TEXT NOT NULL,
  blocked_device TEXT NOT NULL,
  display_name TEXT NOT NULL,
  created_at INTEGER NOT NULL
);
CREATE UNIQUE INDEX lfp_blocks_pair ON lfp_blocks (blocker_device, blocked_device);
CREATE INDEX lfp_blocks_blocked ON lfp_blocks (blocked_device);
