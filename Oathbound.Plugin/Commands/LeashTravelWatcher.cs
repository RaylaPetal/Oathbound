using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Owner side: after arriving in a new area (or jumping far inside one), sends one `leash travel` to every Sub it
/// shows as leashed - an automatic tell the README documents. Where the Sub can't be sent, the leash pauses.
public sealed class LeashTravelWatcher
{
    /// Farther than this between two updates is a relocation (an aethernet hop), not walking.
    private const float JumpDistance = 30f;
    /// Matches the Sub's own re-attach reach: this close, the Sub just re-attaches, so no travel is sent.
    private const float BesideReach = 30f;
    /// Lets the position settle after loading in.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    private readonly PluginConfig config;
    private readonly OwnerStatusEstimateTracker estimates;
    private readonly ChatComposer composer;
    private readonly ChatSender sender;

    private (string World, uint Territory, int Instance)? lastArea;
    private Vector3 lastPosition;
    private DateTime? loadedAt;
    private bool pendingSend;
    private DateTime movedAt;

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
        }
        else if (!pendingSend && Vector2.Distance(Ground(position), Ground(lastPosition)) > JumpDistance)
        {
            movedAt = now;
            pendingSend = true;
        }
        lastPosition = position;

        if (!pendingSend || now - loadedAt.Value < SettleDelay)
            return;
        pendingSend = false;
        Fire();
    }

    private void Fire()
    {
        var leashed = config.Pairings
            .Where(p => p.Direction == PairingDirection.OwnerSide && p.IsPaired
                && estimates.For(p) is { Leashed: true } estimate && estimate.LeashedAtUtc < movedAt && !IsBeside(p))
            .ToList();
        if (leashed.Count == 0)
            return;

        var territory = Plugin.ClientState.TerritoryType;
        var cantFollow = TeleportDestinations.IsInsideHousing() || TeleportDestinations.IsInnRoom(territory)
            || Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56] || Plugin.Condition[ConditionFlag.BoundByDuty95];
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

    /// Matched by name and home world, never by name alone.
    private static bool IsBeside(PairingState pairing)
    {
        if (Plugin.ObjectTable.LocalPlayer is not { } me)
            return false;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is IPlayerCharacter pc && pc.GameObjectId != me.GameObjectId
                && string.Equals(pc.Name.TextValue, pairing.PeerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pc.HomeWorld.ValueNullable?.Name.ExtractText(), pairing.PeerWorld, StringComparison.OrdinalIgnoreCase))
                return Vector2.Distance(Ground(pc.Position), Ground(me.Position)) <= BesideReach;
        }
        return false;
    }

    private static Vector2 Ground(Vector3 v) => new(v.X, v.Z);
}
