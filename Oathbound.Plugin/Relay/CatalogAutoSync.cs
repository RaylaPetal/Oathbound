using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// When automatic sync runs, driven from the framework tick (only relay work goes to the thread pool).
/// Sub: rescans locally at login and every ~15 min (one category per tick); publishes once edits go quiet, at most
/// once per relay upload interval; delivery is confirmed from the pair status poll. Owner: the pair status poll says
/// whether anything is waiting, plus on-demand checks. A relay that doesn't report mailbox state in the pair status
/// puts a pairing back on the hourly checks. Every schedule has 0-5 min jitter.
public sealed class CatalogAutoSync
{
    private static readonly TimeSpan ChangeQuietPeriod = TimeSpan.FromMinutes(3);
    /// A pending change still goes out this long after it appeared, even if saves keep landing.
    private static readonly TimeSpan ChangeMaxWait = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan UploadInterval = TimeSpan.FromSeconds(RelayProtocolConstants.CatalogMailboxMinUploadIntervalSeconds + 1);
    private static readonly TimeSpan Hourly = TimeSpan.FromSeconds(RelayProtocolConstants.CatalogMailboxOwnerPollIntervalSeconds);
    private static readonly TimeSpan OnDemandMinimumGap = TimeSpan.FromSeconds(60);
    /// Long enough for the login pair status poll to answer first, so fallback checks only run when it couldn't.
    private static readonly TimeSpan FallbackStartDelay = TimeSpan.FromSeconds(90);

    private readonly PluginConfig config;
    private readonly CatalogMailboxService mailbox;
    private readonly CatalogSyncService catalogSync;
    private readonly Action[] rescanSteps;
    private readonly Func<CancellationToken> backgroundToken;

    private DateTime nextRescanUtc;
    private int rescanStage = -1;
    private DateTime? startupDigestCheckUtc;

    // Written from the thread pool, read on the framework thread.
    private int catalogDirty;
    private long lastChangeTicks;
    private long dirtySinceTicks;
    private DateTime nextFallbackDeliveryUtc;
    private DateTime nextPendingScanUtc;

    /// Pairings whose relay reports mailbox state in the pair status poll; the rest use the hourly fallback checks.
    private readonly ConcurrentDictionary<Guid, byte> mailboxAware = new();
    /// Sub pairings with a change to compare against what was last published, once the upload interval allows.
    private readonly ConcurrentDictionary<Guid, byte> pendingChange = new();
    /// Sub pairings whose last publish didn't arrive; sent again regardless of the local interval or digest.
    private readonly ConcurrentDictionary<Guid, byte> pendingRedelivery = new();
    /// Only a relay-requested wait survives a pair status poll; other holds are lifted when the poll shows a reason to retry.
    private readonly ConcurrentDictionary<Guid, (DateTime NotBefore, bool RateLimited)> subRetry = new();
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
        // After the startup rescan: picks up changes made offline or left pending at logout, without a relay call.
        startupDigestCheckUtc = now.AddSeconds(45);
        nextFallbackDeliveryUtc = now + FallbackStartDelay;
        nextOwnerCheckUtc.Clear(); // Lazily re-seeded on the next tick.
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

    /// From the pair status poll, on the framework thread.
    public void OnPairStatus(PairingState pairing, PairEnvelope pair)
    {
        if (!pairing.IsPaired || pairing.PairIdHash is not { Length: > 0 })
            return;
        if (pair.CatalogMailbox is not { } state)
        {
            if (mailboxAware.TryRemove(pairing.Id, out _))
                Plugin.Log.Information($"Catalog sync for {pairing.PeerName}: relay doesn't report mailbox state, using hourly checks.");
            return;
        }
        if (mailboxAware.TryAdd(pairing.Id, 0))
            Plugin.Log.Debug($"Catalog sync for {pairing.PeerName}: driven by the pair status poll.");

        if (pairing.Direction == PairingDirection.OwnerSide)
        {
            if (!mailbox.IsChecking(pairing.Id))
                Plugin.FireAndForget(mailbox.ApplyPairStatusAsync(pairing, state, backgroundToken()));
            return;
        }

        if (!state.Exists)
            return; // No Owner receive key yet; a pending change stays pending.
        var lastPublished = pairing.LastPublishedMailboxSnapshotId;
        var delivered = (state.WaitingSnapshotId ?? 0) >= lastPublished || (state.LastConsumedSnapshotId ?? 0) >= lastPublished;
        if (lastPublished > 0 && !delivered)
        {
            Plugin.Log.Debug($"Catalog sync for {pairing.PeerName}: snapshot #{lastPublished} never arrived, sending again.");
            pendingRedelivery[pairing.Id] = 0;
        }
        else
        {
            // Local compare only; covers a first publish and a change held back while no key existed.
            pendingChange[pairing.Id] = 0;
        }
        if (subRetry.TryGetValue(pairing.Id, out var hold) && !hold.RateLimited)
            subRetry.TryRemove(pairing.Id, out _);
    }

    // ---- Sub: periodic rescan ----

    private bool HasSubPairing => config.Pairings.Any(p => p is { Direction: PairingDirection.SubSide, IsPaired: true });

    private void TickRescan(DateTime now)
    {
        if (rescanStage < 0)
        {
            if (now < nextRescanUtc) return;
            nextRescanUtc = now + RescanInterval + Jitter();
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

    // ---- Sub: debounced, interval-limited publish ----

    private void TickSubPublish(DateTime now)
    {
        if (rescanStage >= 0) return; // Let a rescan finish so its results go out in one publish.

        var changeReady = Volatile.Read(ref catalogDirty) == 1 &&
                          (now - new DateTime(Interlocked.Read(ref lastChangeTicks), DateTimeKind.Utc) >= ChangeQuietPeriod ||
                           now - new DateTime(Interlocked.Read(ref dirtySinceTicks), DateTimeKind.Utc) >= ChangeMaxWait);
        var startupCheck = startupDigestCheckUtc is { } at && now >= at;
        var fallbackDue = now >= nextFallbackDeliveryUtc;
        // Held changes wait up to the whole upload interval, so they're re-examined once a second, not every frame.
        var pendingScan = (!pendingChange.IsEmpty || !pendingRedelivery.IsEmpty) && now >= nextPendingScanUtc;
        if (!changeReady && !startupCheck && !fallbackDue && !pendingScan) return;
        nextPendingScanUtc = now.AddSeconds(1);

        var subPairings = config.Pairings
            .Where(p => p is { Direction: PairingDirection.SubSide, IsPaired: true, PairIdHash.Length: > 0 })
            .ToList();

        if (changeReady || startupCheck)
        {
            if (changeReady) Interlocked.Exchange(ref catalogDirty, 0);
            startupDigestCheckUtc = null;
            foreach (var pairing in subPairings)
                pendingChange[pairing.Id] = 0;
        }
        if (fallbackDue)
        {
            nextFallbackDeliveryUtc = now + Hourly + Jitter();
            foreach (var pairing in subPairings.Where(p => !mailboxAware.ContainsKey(p.Id)))
                pendingRedelivery[pairing.Id] = 0;
        }
        foreach (var id in pendingChange.Keys.Concat(pendingRedelivery.Keys).Where(id => subPairings.All(p => p.Id != id)).ToList())
        {
            pendingChange.TryRemove(id, out _);
            pendingRedelivery.TryRemove(id, out _);
        }
        if (pendingChange.IsEmpty && pendingRedelivery.IsEmpty) return;

        var due = subPairings.Where(p => !IsHeld(p, now) &&
            (pendingRedelivery.ContainsKey(p.Id) || (pendingChange.ContainsKey(p.Id) && UploadWindowOpen(p, now)))).ToList();
        if (due.Count == 0) return;

        // Turning it back on saves the config, which marks the catalog dirty.
        if (!config.Permissions.RelayCatalogSync)
        {
            pendingChange.Clear();
            pendingRedelivery.Clear();
            return;
        }

        if (!catalogSync.TryBuildBoundedExport(out var exportText, out var exportError))
        {
            Plugin.Log.Warning(exportError ?? "Catalog export exceeded a local size limit; not published.");
            foreach (var pairing in due)
            {
                pendingChange.TryRemove(pairing.Id, out _);
                pendingRedelivery.TryRemove(pairing.Id, out _);
            }
            return;
        }
        var digest = RelayCrypto.Sha256Hex(exportText);

        foreach (var pairing in due)
        {
            pendingChange.TryRemove(pairing.Id, out _);
            if (!pendingRedelivery.TryRemove(pairing.Id, out _) && digest == pairing.LastPublishedCatalogDigest)
                continue; // Saves that didn't change the export, or a change that was reverted.
            Publish(pairing, exportText, digest);
        }
    }

    /// A change waiting to go out to this pairing, and when the upload interval lets it.
    public (bool Pending, DateTime OpensAtUtc) PendingChange(PairingState pairing)
    {
        var pending = pendingChange.ContainsKey(pairing.Id) || pendingRedelivery.ContainsKey(pairing.Id);
        var opensAt = DateTimeOffset.FromUnixTimeSeconds(pairing.LastPublishedCatalogUnixSeconds).UtcDateTime + UploadInterval;
        return (pending, opensAt);
    }

    private bool IsHeld(PairingState pairing, DateTime now) =>
        subRetry.TryGetValue(pairing.Id, out var hold) && now < hold.NotBefore;

    /// The relay enforces the same interval; this just doesn't spend a request on a certain rate-limit.
    private static bool UploadWindowOpen(PairingState pairing, DateTime now) =>
        now >= DateTimeOffset.FromUnixTimeSeconds(pairing.LastPublishedCatalogUnixSeconds).UtcDateTime + UploadInterval;

    private void Publish(PairingState pairing, string exportText, string digest)
    {
        var task = mailbox.PublishAsync(pairing, exportText, digest, backgroundToken());
        Plugin.FireAndForget(task.ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            var result = t.IsFaulted ? new MailboxPublishResult(MailboxPublishOutcome.Failed, Error: "Unexpected error - see /xllog.") : t.Result;
            if (t.IsFaulted)
                Plugin.Log.Warning(t.Exception!, $"Catalog sync for {pairing.PeerName}: publish failed unexpectedly.");
            var now = DateTime.UtcNow;
            Plugin.Log.Debug($"Catalog sync for {pairing.PeerName}: publish {result.Outcome}.");
            DateTime? retryAt = null;
            switch (result.Outcome)
            {
                case MailboxPublishOutcome.Published:
                    subRetry.TryRemove(pairing.Id, out _);
                    break;
                case MailboxPublishOutcome.RateLimited:
                    retryAt = now.AddSeconds(result.RetryAfterSeconds + 1);
                    subRetry[pairing.Id] = (retryAt.Value, true);
                    MarkDirtyAt(retryAt.Value - ChangeQuietPeriod);
                    break;
                default:
                    // The next pair status poll (or hourly fallback pass) retries.
                    retryAt = now + (mailboxAware.ContainsKey(pairing.Id) ? UploadInterval : Hourly);
                    subRetry[pairing.Id] = (retryAt.Value, false);
                    break;
            }
            pairing.LastPublishAttemptUnixSeconds = new DateTimeOffset(now).ToUnixTimeSeconds();
            pairing.LastPublishOutcome = result.Outcome;
            pairing.LastPublishError = result.Error;
            pairing.NextPublishRetryUnixSeconds = retryAt is { } at ? new DateTimeOffset(at).ToUnixTimeSeconds() : 0;
            config.SaveDisplayState();
        }, TaskScheduler.Default));
    }

    /// Used to come back right after a relay-requested wait.
    private void MarkDirtyAt(DateTime changeUtc)
    {
        Interlocked.Exchange(ref lastChangeTicks, changeUtc.Ticks);
        Interlocked.Exchange(ref dirtySinceTicks, changeUtc.Ticks);
        Interlocked.Exchange(ref catalogDirty, 1);
    }

    // ---- Owner: hourly mailbox check, only where the pair status poll can't drive it ----

    private void TickOwnerChecks(DateTime now)
    {
        foreach (var pairing in config.Pairings)
        {
            if (pairing is not { Direction: PairingDirection.OwnerSide, IsPaired: true, PairIdHash.Length: > 0 })
                continue;
            var due = nextOwnerCheckUtc.GetOrAdd(pairing.Id, _ => now + FallbackStartDelay);
            if (now < due) continue;
            nextOwnerCheckUtc[pairing.Id] = now + Hourly + Jitter();
            if (mailboxAware.ContainsKey(pairing.Id)) continue;
            Plugin.FireAndForget(mailbox.CheckAsync(pairing, backgroundToken()));
        }
    }

    /// `force` skips the one-minute cooldown. Either way the next scheduled fallback check moves a full hour out.
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
