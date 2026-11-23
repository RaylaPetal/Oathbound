using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.UI;

/// Local wording for the mailbox status codes; only the codes ever cross the relay.
public static class MailboxStatusText
{
    /// Why the receiving plugin couldn't take an item, after "{who}'s plugin couldn't read it: ".
    public static string Reject(string? code) => code switch
    {
        MailboxStatusCodes.VerifyFailed => "it didn't pass its checks",
        MailboxStatusCodes.DecryptFailed => "it couldn't be decrypted",
        MailboxStatusCodes.ImportFailed => "what was in it couldn't be used",
        MailboxStatusCodes.NeedsUpdate => "their plugin needs updating to read it",
        MailboxStatusCodes.Stale => "it already had a newer one",
        MailboxStatusCodes.KeyLost => "it was lost while being picked up",
        _ => "an unknown problem",
    };

    /// Why this plugin couldn't take an item, after "couldn't be read: ".
    public static string OwnFailure(string? code) => code switch
    {
        MailboxStatusCodes.VerifyFailed => "it didn't pass the checks",
        MailboxStatusCodes.DecryptFailed => "it couldn't be decrypted",
        MailboxStatusCodes.ImportFailed => "what was in it couldn't be used",
        MailboxStatusCodes.NeedsUpdate => "your plugin needs updating to read it",
        MailboxStatusCodes.Stale => "a newer one had already arrived",
        MailboxStatusCodes.KeyLost => "it was lost while being picked up",
        _ => "an unknown problem",
    };

    /// A sender's can't-send status, as "{who}'s plugin ...".
    public static string SenderProblem(string? code, string what) => code switch
    {
        MailboxStatusCodes.NotScanned => $"hasn't scanned anything yet, so there's no {what} to send",
        MailboxStatusCodes.KeyUnverified => "couldn't verify your plugin's key for it",
        MailboxStatusCodes.TooLarge => $"can't send the {what}: it's too large",
        MailboxStatusCodes.ExportFailed => $"couldn't put the {what} together",
        MailboxStatusCodes.UploadFailed => $"couldn't upload the {what} and keeps trying",
        _ => $"can't send the {what} right now",
    };

    public static bool IsProblem(string? code) => code is not null && code != MailboxStatusCodes.Ok;
}
