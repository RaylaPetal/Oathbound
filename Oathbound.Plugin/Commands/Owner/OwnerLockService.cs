using System;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Ends the Owner's title, outfit and moodle locks when their timers run out or the pairing that set them ends, and
/// keeps locked moodles on. Waits, like restraint timers, until the character can be changed.
public sealed class OwnerLockService
{
    private readonly PluginConfig config;
    private readonly TitleCommand title;
    private readonly OutfitCommand outfit;
    private readonly MoodlesCommand moodles;
    private long nextSlowCheckTicks;

    /// Moodles has no change event, so a removed locked moodle is noticed by looking.
    private const long SlowCheckIntervalMs = 3_000;

    public OwnerLockService(PluginConfig config, TitleCommand title, OutfitCommand outfit, MoodlesCommand moodles)
    {
        this.config = config;
        this.title = title;
        this.outfit = outfit;
        this.moodles = moodles;
    }

    public void OnFrameworkUpdate()
    {
        if (!RestraintCommand.CanChangeCharacter())
            return;

        var locks = config.OwnerLocks;
        var now = DateTime.UtcNow;
        if (locks.Title is { } titleLock && (Expired(titleLock.ExpiresAtUtc, now) || PairingGone(titleLock.ByPairingId)))
        {
            Plugin.Log.Information($"Owner title lock on \"{titleLock.Text}\" ended (timer or pairing).");
            title.ForceClear();
        }
        if (locks.Outfit is { } outfitLock)
        {
            if (Expired(outfitLock.ExpiresAtUtc, now))
                outfit.ExpireLock();
            else if (PairingGone(outfitLock.ByPairingId))
                outfit.ForceUnlock();
        }
        foreach (var moodleLock in locks.Moodles.Where(l => Expired(l.ExpiresAtUtc, now) || PairingGone(l.ByPairingId)).ToList())
        {
            Plugin.Log.Information($"Owner moodle lock on \"{moodleLock.Name}\" ended (timer or pairing).");
            moodles.EndLock(moodleLock);
        }

        var ticks = Environment.TickCount64;
        if (ticks < nextSlowCheckTicks)
            return;
        nextSlowCheckTicks = ticks + SlowCheckIntervalMs;
        moodles.ReassertLocks();
    }

    private static bool Expired(DateTime? end, DateTime now) => end is { } e && now >= e;

    /// A lock from no pairing (an older save, or a local test) stays until cleared.
    private bool PairingGone(Guid? pairingId) => pairingId is { } id && config.FindPairingById(id) is not { IsPaired: true };
}
