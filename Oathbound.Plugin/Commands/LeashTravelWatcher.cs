using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Owner side: after arriving in a new area (or jumping far inside one), sends one `leash travel` to every Sub it
/// shows as leashed - an automatic tell the README documents. Where the Sub can't be sent, the leash pauses, except
/// that entering a duty sends `unleash` instead when the Owner opted in.
public sealed class LeashTravelWatcher
{
    /// Farther than this between two updates is a relocation (an aethernet hop), not walking.
    private const float JumpDistance = 30f;
    /// Matches the Sub's own re-attach reach: this close, the Sub just re-attaches, so no travel is sent.
    private const float BesideReach = 30f;
    /// Lets the position settle after loading in.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);
    /// A tell sent during the duty's entry fade, or before a Sub in the party has loaded in, gets lost.
    private static readonly TimeSpan UnleashMinDelay = TimeSpan.FromSeconds(3);
    /// A Sub who never shows up isn't in the duty; send anyway.
    private static readonly TimeSpan UnleashMaxWait = TimeSpan.FromSeconds(15);

    private readonly PluginConfig config;
    private readonly OwnerStatusEstimateTracker estimates;
    private readonly ChatComposer composer;
    private readonly ChatSender sender;

    private (string World, uint Territory, int Instance)? lastArea;
    private Vector3 lastPosition;
    private DateTime? loadedAt;
    private bool pendingSend;
    /// A jump inside a duty (a boss-arena teleport) isn't entering one.
    private bool pendingAreaChange;
    private DateTime movedAt;
    private List<PairingState>? pendingUnleash;

    public LeashTravelWatcher(PluginConfig config, OwnerStatusEstimateTracker estimates, ChatComposer composer, ChatSender sender)
    {
        this.config = config;
        this.estimates = estimates;
        this.composer = composer;
        this.sender = sender;
    }

    public void OnFrameworkUpdate()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            // The start of the load: a leash-on notice arriving while loading was started at the destination.
            if (loadedAt is not null)
                movedAt = DateTime.UtcNow;
            loadedAt = null;
            return;
        }

        var now = DateTime.UtcNow;
        loadedAt ??= now;
        var area = (player.CurrentWorld.Value.Name.ExtractText(), Plugin.ClientState.TerritoryType, TeleportDestinations.CurrentPublicInstance());
        var position = player.Position;

        if (lastArea is null)
        {
            // First sighting: nothing moved yet.
            lastArea = area;
            lastPosition = position;
            return;
        }

        if (area != lastArea.Value)
        {
            lastArea = area;
            pendingSend = true;
            pendingAreaChange = true;
            pendingUnleash = null;
        }
        else if (!pendingSend && Vector2.Distance(Ground(position), Ground(lastPosition)) > JumpDistance)
        {
            movedAt = now;
            pendingSend = true;
        }
        lastPosition = position;

        if (pendingUnleash is not null)
            TryUnleash(now - loadedAt.Value);

        if (!pendingSend || now - loadedAt.Value < SettleDelay)
            return;
        pendingSend = false;
        var areaChanged = pendingAreaChange;
        pendingAreaChange = false;
        Fire(areaChanged);
    }

    private void Fire(bool areaChanged)
    {
        var territory = Plugin.ClientState.TerritoryType;
        // The bound-by-duty flags can lag the load; the territory's own duty link can't.
        var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56] || Plugin.Condition[ConditionFlag.BoundByDuty95]
            || TeleportDestinations.IsDuty(territory);
        if (inDuty && areaChanged && config.QuickCommands.UnleashInDuties)
        {
            // No IsBeside filter: a Sub in the party loads into the duty right next to the Owner.
            var toUnleash = LeashedSince(movedAt).ToList();
            if (toUnleash.Count > 0)
                pendingUnleash = toUnleash;
            return;
        }

        var leashed = LeashedSince(movedAt).Where(p => !IsBeside(p)).ToList();
        if (leashed.Count == 0)
            return;

        var cantFollow = TeleportDestinations.IsInsideHousing() || TeleportDestinations.IsInnRoom(territory)
            || inDuty;
        if (cantFollow || !TeleportSendAction.TryResolveTarget(out var target, out _))
        {
            // The Sub's leash pauses on its own and picks back up once they're together again.
            Plugin.NotificationManager.AddNotification(new Notification
            {
                Title = "Leash paused",
                Content = leashed.Count == 1
                    ? $"{leashed[0].PeerName} can't be brought here, so their leash is paused until they join you or you come back out."
                    : "Your Subs can't be brought here, so their leashes are paused until they join you or you come back out.",
                Type = NotificationType.Info,
            });
            return;
        }

        foreach (var pairing in leashed)
            sender.Send(composer.ComposeLeashTravel(pairing, target));
    }

    private IEnumerable<PairingState> LeashedSince(DateTime cutoff) =>
        config.Pairings.Where(p => p.Direction == PairingDirection.OwnerSide && p.IsPaired
            && estimates.For(p) is { Leashed: true } estimate && estimate.LeashedAtUtc < cutoff);

    private void TryUnleash(TimeSpan sinceLoad)
    {
        if (sinceLoad < UnleashMinDelay || InCutscene())
            return;
        // Waits for the Subs to load in, since a tell to a Sub still on a loading screen can be lost.
        if (sinceLoad < UnleashMaxWait && !pendingUnleash!.All(p => FindPeer(p) is not null))
            return;

        var leashed = pendingUnleash!.Where(p => estimates.For(p) is { Leashed: true }).ToList();
        pendingUnleash = null;
        if (leashed.Count == 0)
            return;

        foreach (var pairing in leashed)
        {
            sender.Send(composer.ComposeUnleash(pairing));
            estimates.MarkUnleashed(pairing.Id);
        }
        Plugin.Log.Information($"Entered a duty: sent unleash to {leashed.Count} leashed Sub(s) {sinceLoad.TotalSeconds:0.0}s after loading in.");
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Leash off",
            Content = leashed.Count == 1
                ? $"You entered a duty, so {leashed[0].PeerName} was unleashed."
                : "You entered a duty, so your Subs were unleashed.",
            Type = NotificationType.Info,
        });
    }

    private static bool InCutscene() =>
        Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.WatchingCutscene78];

    private static bool IsBeside(PairingState pairing) =>
        Plugin.ObjectTable.LocalPlayer is { } me && FindPeer(pairing) is { } pc
        && Vector2.Distance(Ground(pc.Position), Ground(me.Position)) <= BesideReach;

    /// Matched by name and home world, never by name alone.
    private static IPlayerCharacter? FindPeer(PairingState pairing)
    {
        if (Plugin.ObjectTable.LocalPlayer is not { } me)
            return null;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is IPlayerCharacter pc && pc.GameObjectId != me.GameObjectId
                && string.Equals(pc.Name.TextValue, pairing.PeerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pc.HomeWorld.ValueNullable?.Name.ExtractText(), pairing.PeerWorld, StringComparison.OrdinalIgnoreCase))
                return pc;
        }
        return null;
    }

    private static Vector2 Ground(Vector3 v) => new(v.X, v.Z);
}
