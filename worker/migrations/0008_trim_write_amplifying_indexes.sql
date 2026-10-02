-- D1 bills each index entry as an extra row written. These two only served the cron's cleanup, which can
-- scan these small tables instead: reads are far cheaper than writes on the free plan.
DROP INDEX IF EXISTS idx_nonces_seen_at;
DROP INDEX IF EXISTS idx_quota_counters_window_start;

-- Already covered by the pairs primary key (pair_id_hash, pair_epoch).
DROP INDEX IF EXISTS idx_pairs_pair_id_hash;
