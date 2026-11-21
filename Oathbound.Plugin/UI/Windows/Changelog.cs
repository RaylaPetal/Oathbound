namespace Oathbound.Plugin.UI;

public sealed record ChangelogSection(string Heading, string[] Items);

public sealed record ChangelogEntry(string Version, string Date, string Title, ChangelogSection[] Sections);

/// Only the current release's notes; replace them with every release. The window opens once per update, and only
/// when Latest is for the version being loaded.
public static class Changelog
{
    public static readonly ChangelogEntry Latest =
        new("0.3.1.3", "2026-11-21", "Pairing fix",
        [
            new("Fixed",
            [
                "Paired again after an earlier pairing ended? Oathbound could keep pointing at the old pairing, so Sub Control said \"Not paired with a Sub yet\" and every Send stayed disabled even though you showed as paired. Your current pairing is picked up as soon as you update - nothing to redo.",
            ]),
            new("Also in this update",
            [
                "Catalog sync is always on: the \"Catalog sync\" permission is gone. Your catalog is only the names of what your Owner can pick from - sharing it never puts anything on you.",
                "Right after you pair as a Sub, Oathbound scans and sends your catalog to your Owner on its own.",
                "Owners: \"Request refresh\" is never stuck on \"already waiting\" - press \"Request again\" to replace a request that's still waiting.",
            ]),
        ]);
}
