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
    public const int CatalogMailboxMinUploadIntervalSeconds = 60;
    public const int CatalogMailboxOwnerPollIntervalSeconds = 3600;
    public const int CodeInvitationExpirySeconds = 604800;
    public const int PairStatusPollIntervalSeconds = 1800;
    public const int BackupCiphertextMaxBytes = 32768;
}
