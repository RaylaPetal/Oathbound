-- The UTC day (unix seconds / 86400) of a device's latest signed request. Updated at most once per device per day,
-- so the relay's active count no longer needs every read to leave a nonce behind.
ALTER TABLE device_keys ADD COLUMN active_day INTEGER;
