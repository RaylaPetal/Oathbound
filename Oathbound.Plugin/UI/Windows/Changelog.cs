namespace Oathbound.Plugin.UI;

public sealed record ChangelogSection(string Heading, string[] Items);

public sealed record ChangelogEntry(string Version, string Date, string Title, ChangelogSection[] Sections);

/// Only the current release's notes; replace them with every release. The window opens once per update, and only
/// when Latest is for the version being loaded.
public static class Changelog
{
    public static readonly ChangelogEntry Latest =
        new("0.3.2.1", "2026-11-21", "Pictures in chat, and a new look",
        [
            new("New",
            [
                "Send pictures in chat: press the paperclip to pick a PNG or JPEG, or take a snapshot of the game screen. Whatever you've typed goes along as its caption. Click a picture to see it full size; right-click to save it.",
                "Pictures are encrypted with their own key before they leave your computer. The relay keeps the encrypted picture for up to 7 days so your partner can pick it up.",
                "A sound when a chat message arrives. Pick it (or turn it off) under Settings > Chat.",
                "The chat window has a fresh look: bubbles with the time and a delivered tick in the corner, day labels, and a rounded message box.",
                "If you're both Owner and Sub of the same person, your two chats are merged into one.",
            ]),
            new("Testing build",
            [
                "This testing build uses Oathbound's testing relay. Your partner needs this testing build too for commands, chat and pictures to reach each other.",
            ]),
        ]);
}
