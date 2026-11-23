namespace Oathbound.Plugin.Relay;

/// Mirrors protocol/constants.json's sizeAndExpiryLimits by hand; must stay numerically in sync with it.
public static class RelayProtocolConstants
{
    public const int InvitationExpirySeconds = 900;
    public const int CatalogRequestExpirySeconds = 900;
    public const int CatalogObjectExpirySeconds = 900;
    public const int RevocationRetentionSecondsMax = 604800;
    public const int CatalogPlaintextMaxBytes = 2097152;
    public const int CatalogCiphertextMaxBytes = 786432;
    public const int RevocationPollMinIntervalSeconds = 21600;
    public const int CatalogMailboxExpirySeconds = 604800;
    public const int CatalogMailboxMinUploadIntervalSeconds = 1800;
    public const int CatalogMailboxOwnerPollIntervalSeconds = 3600;
    public const int CodeInvitationExpirySeconds = 604800;
    public const int PairStatusPollIntervalSeconds = 1800;
    public const int BackupCiphertextMaxBytes = 32768;
    public const int CollarCheckinIntervalSeconds = 21600;
    public const int CollarBrokenGraceSeconds = 60;
    public const int CollarStaleSeconds = 172800;
    public const int RulebookMailboxExpirySeconds = 1209600;
    public const int RulebookCiphertextMaxBytes = 65536;
    public const int RulebookReportCiphertextMaxBytes = 32768;
    public const int RulebookMinUploadIntervalSeconds = 60;
    public const int RulebookReportMinUploadIntervalSeconds = 600;
    public const int PictureCiphertextMaxBytes = 65536;
    public const int PicturesPerPairMax = 256;
    public const int PictureFetchBatchMax = 16;
    public const int CatalogMailboxRefreshMinIntervalSeconds = 60;
}

/// Mirrors protocol/constants.json mailboxStatus. Only these codes cross the relay; each side maps them to its own text.
public static class MailboxStatusCodes
{
    public const string Ok = "ok";

    // Recipient (receipt) codes.
    public const string VerifyFailed = "verify_failed";
    public const string DecryptFailed = "decrypt_failed";
    public const string ImportFailed = "import_failed";
    public const string NeedsUpdate = "needs_update";
    public const string Stale = "stale";
    public const string KeyLost = "key_lost";

    // Sender codes.
    public const string NotScanned = "not_scanned";
    public const string KeyUnverified = "key_unverified";
    public const string TooLarge = "too_large";
    public const string ExportFailed = "export_failed";
    public const string UploadFailed = "upload_failed";

    public const int MaxConsecutiveResends = 2;

    /// A fresh copy of the same content can fix these, so the sender sends it again.
    public static bool IsResendable(string code) => code is VerifyFailed or DecryptFailed or KeyLost or Stale;
}
