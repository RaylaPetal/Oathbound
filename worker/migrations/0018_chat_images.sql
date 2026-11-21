-- Encrypted chat pictures. The relay holds only the ciphertext (in R2) and this bookkeeping; the key travels inside the
-- sealed chat message. Rows expire after peerChat.image.storedTtlSeconds or with their pair epoch's revocation.
CREATE TABLE chat_images (
  pair_id_hash TEXT NOT NULL,
  pair_epoch INTEGER NOT NULL,
  image_id TEXT NOT NULL,
  uploader_device_key_id TEXT NOT NULL,
  size_bytes INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  PRIMARY KEY (pair_id_hash, image_id)
) WITHOUT ROWID;

CREATE INDEX chat_images_expiry ON chat_images (expires_at);
