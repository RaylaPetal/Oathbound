namespace Oathbound.Plugin.UI;

public sealed record ChangelogSection(string Heading, string[] Items);

public sealed record ChangelogEntry(string Version, string Date, string Title, ChangelogSection[] Sections);

/// Only the current release's notes; replace them with every release. The window opens once per update, and only
/// when Latest is for the version being loaded.
public static class Changelog
{
    public static readonly ChangelogEntry Latest =
        new("0.3.3.1", "2026-11-23", "Know what happened",
        [
            new("New",
            [
                "Owners: every command now tells you how it went - applied, refused with the reason (like \"Outfit is off\" or \"caged\"), or \"sent, not confirmed\" if your Sub's plugin doesn't answer. Banners, Tie off / Untie and your status estimate follow what really happened, not just what you sent.",
                "Rulebooks: when your Sub accepts or declines a version, you see it within seconds - and a decline can say why. Subs get a badge on the Rulebook tile while a new version or oath is waiting.",
                "Catalogs and rulebooks only count as delivered once the other side has actually read them. If something couldn't be read, the sender sees why and sends it again, and the problem stays on screen until it's fixed.",
                "Owners: \"Get latest\" replaces Check now and Request refresh - one button that asks your Sub's plugin for its newest catalog right away.",
                "Chat: an undelivered message now has a Retry.",
            ]),
            new("Easier to find your way",
            [
                "Hints take you where they point, with a button: Go to Sync, or straight to the right Settings tab.",
                "A greyed-out Send always tells you why when you hover it.",
                "Deleting something asks first, and \"Clear all\" is now \"Clear imported\" - it keeps what you wrote yourself.",
                "Colours mean one thing each: red for problems, amber for cautions, blue for information.",
                "One set of words everywhere: points and milestones in rulebooks, \"Unlock outfit\", \"Full Body Cuffed\", and no more leftovers from when commands were tells.",
                "Toy triggers pick their pattern from a list, so a typo can't break them. Restraint editors share one Save / Discard. Every ? starts a tour, and Achievements has one now.",
                "Paired Subs can change their trigger phrase again.",
            ]),
            new("Good to know",
            [
                "The new confirmations need this update on both sides. With a partner who hasn't updated yet, everything works as before - you just won't see the confirmations.",
            ]),
        ]);
}
