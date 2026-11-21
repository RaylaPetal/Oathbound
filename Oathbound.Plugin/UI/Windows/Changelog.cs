namespace Oathbound.Plugin.UI;

public sealed record ChangelogSection(string Heading, string[] Items);

public sealed record ChangelogEntry(string Version, string Date, string Title, ChangelogSection[] Sections);

/// Only the current release's notes; replace them with every release. The window opens once per update, and only
/// when Latest is for the version being loaded.
public static class Changelog
{
    public static readonly ChangelogEntry Latest =
        new("0.3.2.2", "2026-11-21", "Chat with your partner",
        [
            new("New: Chat",
            [
                "Talk with your Owner or Sub right inside Oathbound - across worlds and data centers, where /tell can't reach. Open it with the speech-bubble icon in the main window's header (Owners: next to Revert all). A red badge counts unread messages.",
                "If your partner is offline, your message waits for them for up to 7 days. The ticks in each message's corner show it: a clock while sending, one tick while it waits, two ticks once it's delivered.",
                "Send pictures: the paperclip picks a PNG or JPEG from your computer, or takes a snapshot of the game screen. Whatever you've typed goes along as its caption. Click a picture to see it full size; right-click to save it.",
                "Everything is end-to-end encrypted, pictures included, and your chat history stays on your own computer.",
                "If you're both Owner and Sub of the same person, it's one conversation.",
                "A sound plays when a message arrives - pick it or turn it off under Settings > Chat.",
                "A gagged Sub's messages come out gagged, just like their game chat.",
            ]),
            new("Good to know",
            [
                "You both need this update to chat. Until your partner updates, the chat tells you they need to.",
                "The guided tour now covers chat, and the chat window has its own tour behind the ? in its title bar.",
            ]),
        ]);
}
