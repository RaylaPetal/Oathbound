namespace Oathbound.Plugin.UI;

public sealed record ChangelogSection(string Heading, string[] Items);

public sealed record ChangelogEntry(string Version, string Date, string Title, ChangelogSection[] Sections);

/// Only the current release's notes; replace them with every release. The window opens once per update, and only
/// when Latest is for the version being loaded.
public static class Changelog
{
    public static readonly ChangelogEntry Latest =
        new("0.3.3.0", "2026-11-23", "Know what happened",
        [
            new("New",
            [
                "Owners: every command now tells you how it went - applied, refused (with the reason, like \"Outfit is off\" or \"caged\"), or \"sent, not confirmed\" if your Sub's plugin doesn't answer. Banners, Tie off/Untie and the status estimate follow what really happened.",
                "Rulebooks: when your Sub accepts or declines a version (with an optional reason), you see it within seconds. Subs get a badge on the Rulebook tile until they open it.",
                "Catalog and rulebook delivery only counts as delivered once the other side has read it. If it couldn't be read, the sender sees why and resends; errors stay visible until things work again.",
                "Owners: \"Get latest\" replaces Check now and Request refresh - it asks your Sub's plugin for its newest catalog right away.",
                "Chat: an undelivered message now has a Retry.",
            ]),
            new("Clearer everywhere",
            [
                "Hints point to the right place, with a button: Go to Sync, or the right Settings tab.",
                "A greyed-out Send always says why on hover.",
                "Deleting something asks first; \"Clear all\" is now \"Clear imported\" and keeps what you wrote yourself.",
                "Colours mean one thing each: red for errors, amber for cautions, blue for information.",
                "One set of words: points and milestones, \"Unlock outfit\", \"Full Body Cuffed\", and no more leftovers from the tell days.",
                "Toy triggers pick their pattern from a list. Restraint editors share one Save / Discard. Every ? starts a tour - Achievements has one now.",
            ]),
            new("Testing build",
            [
                "This testing build uses Oathbound's testing relay. Your partner needs it too; with an older partner, things work as before without the new confirmations.",
            ]),
        ]);
}
