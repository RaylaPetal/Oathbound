namespace Oathbound.Plugin.UI;

public sealed record ChangelogSection(string Heading, string[] Items);

public sealed record ChangelogEntry(string Version, string Date, string Title, ChangelogSection[] Sections);

/// Only the current release's notes; replace them with every release. The window opens once per update, and only
/// when Latest is for the version being loaded.
public static class Changelog
{
    public static readonly ChangelogEntry Latest =
        new("0.3.2.0", "2026-11-21", "Chat with your partner",
        [
            new("New",
            [
                "Chat: talk with your Owner or Sub right inside Oathbound - across worlds and data centers, where /tell can't reach. Open it with the speech-bubble icon in the main window's header; a red badge shows unread messages.",
                "If your partner is offline, your message waits for them on the relay for up to 7 days, and you see when it's delivered. Messages are end-to-end encrypted, and your chat history stays on your own computer.",
                "If you're both Owner and Sub of the same person, it's one conversation.",
                "A gagged Sub's messages come out gagged, just like their game chat.",
            ]),
            new("Testing build",
            [
                "This testing build uses Oathbound's testing relay, which has a copy of your pairings. Your partner needs this testing build too for commands and chat to reach each other.",
            ]),
        ]);
}
