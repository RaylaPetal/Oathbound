namespace Oathbound.Plugin.UI;

public sealed record ChangelogSection(string Heading, string[] Items);

public sealed record ChangelogEntry(string Version, string Date, string Title, ChangelogSection[] Sections);

/// Only the current release's notes; replace them with every release. The window opens once per update, and only
/// when Latest is for the version being loaded.
public static class Changelog
{
    public static readonly ChangelogEntry Latest =
        new("0.3.1.2", "2026-11-18", "Smoother catalog sync",
        [
            new("Changed",
            [
                "Catalog sync is always on: the \"Catalog sync\" permission is gone. Your catalog is only the names of what your Owner can pick from - your designs, emotes, moodles and shared restraints. Sharing it never puts anything on you.",
                "Right after you pair as a Sub, Oathbound scans your designs, emotes, moodles and restraints and sends the catalog to your Owner on its own. No need to scan by hand first.",
                "Owners: \"Request refresh\" is never stuck on \"already waiting\" any more. Press \"Request again\" to replace a request that's still waiting.",
                "Owners: if your Sub's older version turns down a refresh, you're told they need to update.",
            ]),
            new("Fixed",
            [
                "A Sub who hadn't scanned anything yet could send an empty catalog, which cleared their Owner's lists and held back the real one for half an hour.",
                "The Sync tab no longer says \"No updates from your Sub yet\" after their catalog has already arrived.",
            ]),
        ]);
}
