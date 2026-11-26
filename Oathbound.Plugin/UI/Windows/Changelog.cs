namespace Oathbound.Plugin.UI;

public sealed record ChangelogSection(string Heading, string[] Items);

public sealed record ChangelogEntry(string Version, string Date, string Title, ChangelogSection[] Sections);

/// Only the current release's notes; replace them with every release. The window opens once per update, and only
/// when Latest is for the version being loaded.
public static class Changelog
{
    public static readonly ChangelogEntry Latest =
        new("0.3.4.1", "2026-11-26", "Looking for pair",
        [
            new("New",
            [
                "Looking for pair: not paired yet? Make a profile - display name, pronouns, race, gender, whether you're an Owner, Sub or Switch and what you're looking for, an optional data center, a bio and a picture - and browse everyone else's. For adults (18+) only.",
                "Profiles are public (listed in Browse) or private (seen only with your profile code). Your character's name and world are never on it.",
                "Like the profiles you're interested in. Nobody is told who liked them - but when two people like each other it's a match, and you both get a notice.",
                "A match gets its own end-to-end encrypted chat. Share your character when you're ready, and send a pairing invite right from the chat with the + button.",
                "View profile opens anyone's full profile with a bigger picture and their whole bio; cards show just the start.",
                "Unmatch or block anyone at any time. A block hides you from each other for good, even if they make a new profile code.",
                "The header shows how many Oathbound players are online right now, refreshed every couple of minutes while the main window is open.",
                "Chat: close a conversation with the X to tidy the list. It comes back when a new message arrives, or reopen it from the folder button.",
            ]),
            new("Changed",
            [
                "Favorites moved from the module grid to the star in the main window's title bar.",
                "Dialogs and windows share the same card look.",
            ]),
            new("Good to know",
            [
                "To be counted as online and use Looking for pair, Oathbound now keeps its relay connection while you're logged in, even with no pairing. It only checks in every 30 minutes and never says who you are.",
                "There's no in-plugin reporting yet - if someone makes you uncomfortable, block them.",
            ]),
        ]);
}
