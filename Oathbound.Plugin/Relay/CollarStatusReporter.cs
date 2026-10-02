using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Relay;

/// Sub side: tells the relay whether the collar its owning pairing locked is really on, so that Owner can see it
/// even while offline. Sends on every state change, at login, and as a periodic check-in.
public sealed class CollarStatusReporter
{
    public const string Locked = "locked";
    public const string Unlocked = "unlocked";
    public const string Broken = "broken";

    private static readonly TimeSpan EvaluateInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    /// An older relay without the endpoint: back off for a long time instead of retrying every few minutes.
    private static readonly TimeSpan UnsupportedRetryDelay = TimeSpan.FromHours(1);

    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly SlotLockManager slotLocks;
    private readonly GlamourerIpc glamourer;
    private readonly SubRuntimeState runtimeState;

    private DateTime nextEvaluateUtc;
    private DateTime? lockMissingSinceUtc;
    private DateTime retryNotBeforeUtc;
    private int busy;
    private bool wasLoggedIn;

    /// The pairing reported for last. Kept after an unlock, which clears the collar's owning pairing.
    private Guid? reportedPairingId;
    private string? reportedState;
    private long reportedStateAt;
    private DateTime lastAcceptedUtc;

    public CollarStatusReporter(PluginConfig config, RelayClient relay, SlotLockManager slotLocks, GlamourerIpc glamourer, SubRuntimeState runtimeState)
    {
        this.config = config;
        this.relay = relay;
        this.slotLocks = slotLocks;
        this.glamourer = glamourer;
        this.runtimeState = runtimeState;
    }

    public void OnFrameworkUpdate(CancellationToken ct)
    {
        var loggedIn = Plugin.ClientState.IsLoggedIn;
        if (loggedIn && !wasLoggedIn)
            lastAcceptedUtc = DateTime.MinValue; // Check in at login.
        wasLoggedIn = loggedIn;

        var now = DateTime.UtcNow;
        if (!loggedIn || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            // A zone load can briefly drop the piece; only time spent in the world counts toward broken.
            lockMissingSinceUtc = null;
            return;
        }
        if (now < nextEvaluateUtc)
            return;
        nextEvaluateUtc = now + EvaluateInterval;

        if (Target() is not { } pairing)
            return;
        if (reportedPairingId != pairing.Id)
        {
            reportedPairingId = pairing.Id;
            reportedState = null;
        }

        if (Desired(now) is not { } state)
            return;
        var changed = state != reportedState;
        var due = now - lastAcceptedUtc >= TimeSpan.FromSeconds(RelayProtocolConstants.CollarCheckinIntervalSeconds);
        if (!(changed || due) || now < retryNotBeforeUtc || Interlocked.Exchange(ref busy, 1) == 1)
            return;

        // A check-in repeats the time the current state began, so it never looks like a new change.
        var stateAt = changed ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : reportedStateAt;
        Plugin.FireAndForget(SendAsync(pairing, state, stateAt, ct));
    }

    /// The collar-owning pairing, or the one just unlocked (its owning id is cleared on unlock).
    private PairingState? Target()
    {
        var id = config.CollarOwningPairingId ?? reportedPairingId;
        if (id is null || config.FindPairingById(id.Value) is not { IsPaired: true, Direction: PairingDirection.SubSide, PairIdHash: not null } pairing)
            return null;
        // Once "unlocked" was reported for a pairing that no longer owns the collar, there's nothing left to say.
        if (config.CollarOwningPairingId is null && reportedState == Unlocked && pairing.Id == reportedPairingId)
            return null;
        return pairing;
    }

    /// Null while a missing lock is still inside its grace period.
    private string? Desired(DateTime now)
    {
        if (config.CollarOwningPairingId is null || !runtimeState.CollarForceLocked)
        {
            lockMissingSinceUtc = null;
            return Unlocked;
        }
        if (slotLocks.HasLock(CollarCommand.Owner) && glamourer.IsAvailable)
        {
            lockMissingSinceUtc = null;
            return Locked;
        }
        lockMissingSinceUtc ??= now;
        return now - lockMissingSinceUtc.Value >= TimeSpan.FromSeconds(RelayProtocolConstants.CollarBrokenGraceSeconds)
            ? Broken
            : null;
    }

    private async Task SendAsync(PairingState pairing, string state, long stateAt, CancellationToken ct)
    {
        try
        {
            await relay.PutCollarStatusAsync(pairing.PairIdHash!, pairing.PairEpoch, state, stateAt, ct).ConfigureAwait(false);
            reportedState = state;
            reportedStateAt = stateAt;
            lastAcceptedUtc = DateTime.UtcNow;
            Plugin.Log.Information($"Collar status reported: {state}.");
        }
        catch (RelayException ex)
        {
            retryNotBeforeUtc = DateTime.UtcNow + (ex.Code == "not_found" ? UnsupportedRetryDelay : RetryDelay);
            Plugin.Log.Information($"Collar status report failed ({ex.Code}); retrying later.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            retryNotBeforeUtc = DateTime.UtcNow + RetryDelay;
            Plugin.Log.Warning(ex, "Collar status report failed; retrying later.");
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }
}
