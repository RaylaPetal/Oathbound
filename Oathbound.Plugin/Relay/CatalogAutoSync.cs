using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// When automatic sync runs, driven from the framework tick (only relay work goes to the thread pool).
/// Sub: rescans at login and ~hourly (one category per tick); publishes after saves go quiet; hourly checks that
/// the last push arrived. Owner: checks each mailbox after login, ~hourly and on demand. Every schedule has 0-5 min jitter.
public sealed class CatalogAutoSync
{
    private static readonly TimeSpan ChangeQuietPeriod = TimeSpan.FromSeconds(60);
    /// A pending change still goes out this long after it appeared, even if saves keep landing.
    private static readonly TimeSpan ChangeMaxWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Hourly = TimeSpan.FromSeconds(RelayProtocolConstants.CatalogMailboxOwnerPollIntervalSeconds);
    private static readonly TimeSpan OnDemandMinimumGap = TimeSpan.FromSeconds(60);

    private readonly PluginConfig config;
    private readonly CatalogMailboxService mailbox;
    private readonly CatalogSyncService catalogSync;
    private readonly Action[] rescanSteps;
    private readonly Func<CancellationToken> backgroundToken;

    private DateTime nextRescanUtc;
    private int rescanStage = -1;

    // Written from the thread pool, read on the framework thread.
    private int catalogDirty;
    private long lastChangeTicks;
    private long dirtySinceTicks;
    private DateTime nextDeliveryCheckUtc;

    /// Holds back only the same digest that failed; a new digest is tried right away.
    private readonly ConcurrentDictionary<Guid, (DateTime NotBefore, string Digest)> subRetry = new();
    private readonly ConcurrentDictionary<Guid, DateTime> nextOwnerCheckUtc = new();

    public CatalogAutoSync(PluginConfig config, CatalogMailboxService mailbox, CatalogSyncService catalogSync,
        OutfitCommand outfit, GestureCommand gesture, RestraintCommand restraints, MoodlesCommand moodles,
        Func<CancellationToken> backgroundToken)
    {
        this.config = config;
        this.mailbox = mailbox;
        this.catalogSync = catalogSync;
        this.backgroundToken = backgroundToken;
        rescanSteps = [outfit.Rescan, gesture.Rescan, restraints.RescanCatalog, moodles.Rescan];
        config.Changed += MarkDirty;
        ScheduleStartup();
    }

    public void Dispose() => config.Changed -= MarkDirty;

    /// A short delay lets the other plugins finish loading first.
    public void OnLogin() => ScheduleStartup();

    private void ScheduleStartup()
    {
        var now = DateTime.UtcNow;
        nextRescanUtc = now.AddSeconds(30);
        nextDeliveryCheckUtc = now.AddSeconds(45);
        nextOwnerCheckUtc.Clear(); // Lazily re-seeded ~15s out on the next tick.
    }

    private void MarkDirty()
    {
        var now = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref lastChangeTicks, now);
        if (Interlocked.Exchange(ref catalogDirty, 1) == 0)
            Interlocked.Exchange(ref dirtySinceTicks, now);
    }

    private static TimeSpan Jitter() => TimeSpan.FromSeconds(Random.Shared.Next(0, 300));

    public void OnFrameworkUpdate()
    {
        if (!Plugin.ClientState.IsLoggedIn)
            return;
        var now = DateTime.UtcNow;
        TickRescan(now);
        TickSubPublish(now);
        TickOwnerChecks(now);
    }

    // ---- Sub: periodic rescan ----

    private bool HasSubPairing => config.Pairings.Any(p => p is { Direction: PairingDirection.SubSide, IsPaired: true });

    private void TickRescan(DateTime now)
    {
        if (rescanStage < 0)
        {
            if (now < nextRescanUtc) return;
            nextRescanUtc = now + Hourly + Jitter();
            if (!config.AutoRescanCatalogs || !HasSubPairing) return;
            rescanStage = 0;
        }

        // The catch only stops one category's unexpected failure from blocking the others.
        try
        {
            rescanSteps[rescanStage]();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Scheduled catalog rescan step {rescanStage} failed; that category was left as it was.");
        }
        rescanStage = rescanStage + 1 < rescanSteps.Length ? rescanStage + 1 : -1;
    }

    // ---- Sub: change-driven publish + hourly delivery check ----

    private void TickSubPublish(DateTime now)
    {
        if (rescanStage >= 0) return; // Let a rescan finish so its results go out in one publish.

        var deliveryDue = now >= nextDeliveryCheckUtc;
        var changeReady = Volatile.Read(ref catalogDirty) == 1 &&
                          (now - new DateTime(Interlocked.Read(ref lastChangeTicks), DateTimeKind.Utc) >= ChangeQuietPeriod ||
                           now - new DateTime(Interlocked.Read(ref dirtySinceTicks), DateTimeKind.Utc) >= ChangeMaxWait);
        if (!deliveryDue && !changeReady) return;

        if (changeReady) Interlocked.Exchange(ref catalogDirty, 0);
        if (deliveryDue) nextDeliveryCheckUtc = now + Hourly + Jitter();

        // Turning it back on saves the config, which marks the catalog dirty.
        if (!config.Permissions.RelayCatalogSync) return;
        var subPairings = config.Pairings
            .Where(p => p is { Direction: PairingDirection.SubSide, IsPaired: true, PairIdHash.Length: > 0 })
            .ToList();
        if (subPairings.Count == 0) return;

        if (!catalogSync.TryBuildBoundedExport(out var exportText, out var exportError))
        {
            Plugin.Log.Warning(exportError ?? "Catalog export exceeded a local size limit; not published.");
            return;
        }
        var digest = RelayCrypto.Sha256Hex(exportText);

        foreach (var pairing in subPairings)
        {
            var held = subRetry.TryGetValue(pairing.Id, out var retry) && now < retry.NotBefore;
            if (deliveryDue)
            {
                // The hourly pass still respects a relay "wait" for the same content.
                if (held && retry.Digest == digest) continue;
            }
            else
            {
                if (digest == pairing.LastPublishedCatalogDigest) continue; // A save that didn't change the export.
                if (held && retry.Digest == digest) continue;
            }
            Publish(pairing, exportText, digest);
        }
    }

    private void Publish(PairingState pairing, string exportText, string digest)
    {
        var task = mailbox.PublishAsync(pairing, exportText, digest, backgroundToken());
        Plugin.FireAndForget(task.ContinueWith(t =>
        {
            if (t.IsCanceled || t.IsFaulted) return;
            var result = t.Result;
            var now = DateTime.UtcNow;
            switch (result.Outcome)
            {
                case MailboxPublishOutcome.Published:
                    subRetry.TryRemove(pairing.Id, out _);
                    break;
                case MailboxPublishOutcome.RateLimited:
                    subRetry[pairing.Id] = (now.AddSeconds(result.RetryAfterSeconds + 1), digest);
                    MarkDirtyAt(now.AddSeconds(result.RetryAfterSeconds + 1) - ChangeQuietPeriod);
                    break;
                default:
                    // The hourly pass retries; a newer change is still tried as soon as it settles.
                    subRetry[pairing.Id] = (now + Hourly, digest);
                    break;
            }
        }, TaskScheduler.Default));
    }

    /// Used to come back right after a relay-requested wait.
    private void MarkDirtyAt(DateTime changeUtc)
    {
        Interlocked.Exchange(ref lastChangeTicks, changeUtc.Ticks);
        Interlocked.Exchange(ref dirtySinceTicks, changeUtc.Ticks);
        Interlocked.Exchange(ref catalogDirty, 1);
    }

    // ---- Owner: hourly mailbox check ----

    private void TickOwnerChecks(DateTime now)
    {
        foreach (var pairing in config.Pairings)
        {
            if (pairing is not { Direction: PairingDirection.OwnerSide, IsPaired: true, PairIdHash.Length: > 0 })
                continue;
            var due = nextOwnerCheckUtc.GetOrAdd(pairing.Id, _ => now.AddSeconds(15));
            if (now < due) continue;
            nextOwnerCheckUtc[pairing.Id] = now + Hourly + Jitter();
            Plugin.FireAndForget(mailbox.CheckAsync(pairing, backgroundToken()));
        }
    }

    /// `force` skips the one-minute cooldown. Either way the next scheduled check moves a full hour out.
    public void RequestOwnerCheck(PairingState pairing, bool force)
    {
        if (pairing is not { Direction: PairingDirection.OwnerSide, IsPaired: true, PairIdHash.Length: > 0 } || mailbox.IsChecking(pairing.Id))
            return;
        var now = DateTime.UtcNow;
        if (!force && now - DateTimeOffset.FromUnixTimeSeconds(pairing.LastMailboxCheckOkUnixSeconds).UtcDateTime < OnDemandMinimumGap)
            return;
        nextOwnerCheckUtc[pairing.Id] = now + Hourly + Jitter();
        Plugin.FireAndForget(mailbox.CheckAsync(pairing, backgroundToken()));
    }
}
