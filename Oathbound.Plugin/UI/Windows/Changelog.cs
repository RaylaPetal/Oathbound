namespace Oathbound.Plugin.UI;

public sealed record ChangelogSection(string Heading, string[] Items);

public sealed record ChangelogEntry(string Version, string Date, string Title, ChangelogSection[] Sections);

/// Only the current release's notes; replace them with every release. The window opens once per update, and only
/// when Latest is for the version being loaded.
public static class Changelog
{
    public static readonly ChangelogEntry Latest =
        new("0.3.4.2", "2026-11-27", "Restraint packs",
        [
            new("New",
            [
                "Restraint packs: an Owner can make one restraint for a whole mod pack, like a gag pack, and pick the piece each time they send it. Tick \"Chosen when sending\" in the restraint's Name & item section.",
                "The piece picker is next to the send button, in Favorites, and a submenu in Quick Access. Rulebook consequences, ladder stages and game cards list each piece on its own.",
                "Restraint mod items are named after the mod itself: each gag in a gag pack by its option name, and single-piece mods like cuffs by the mod's name, instead of the base game item's name.",
                "Sending another piece of a pack while one is on swaps it: the old piece comes off and the new one goes on with the new lock. If the new one can't go on, the old one stays as it was.",
            ]),
            new("Changed",
            [
                "A Sub's client only accepts restraint items the shared mod offers. A piece you switch off in Penumbra stops being offered after the next scan.",
            ]),
            new("Good to know",
            [
                "Subs: nothing to set up per piece. Keep the pack in your restraint folder; its pieces and their names reach your Owner with the catalog.",
                "Pieces get names once the Sub is on this version and the catalog has synced again.",
            ]),
        ]);
}
