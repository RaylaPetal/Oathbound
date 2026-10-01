using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// collar/leash-travel "Owner's client sends leash travel automatically" (design D1): Owner side. After this
/// client arrives in a new area (or jumps a long way inside one), every Sub it shows as leashed gets one
/// `leash travel` with the Owner's new location - the third automatic tell the README documents. Nothing is
/// sent for a Sub that isn't leashed, and nothing at all where the leash can't follow (design D3).
public sealed class LeashTravelWatcher
{
    /// A same-area move farther than this between two updates is a relocation (an aethernet hop), not walking.
    private const float JumpDistance = 30f;
    /// Wait after loading in so the position has settled before it's sent.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    private readonly PluginConfig config;
    private readonly OwnerStatusEstimateTracker estimates;
    private readonly ChatComposer composer;
    private readonly ChatSender sender;

    private (string World, uint Territory, int Instance)? lastArea;
    private Vector3 lastPosition;
    private DateTime? loadedAt;
    private bool pendingSend;

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
            loadedAt = null;
            return;
        }

        var now = DateTime.UtcNow;
        loadedAt ??= now;
        var area = (player.CurrentWorld.Value.Name.ExtractText(), Plugin.ClientState.TerritoryType, TeleportDestinations.CurrentPublicInstance());
        var position = player.Position;

        if (lastArea is null)
        {
            // First sighting (plugin load, login): nothing moved yet.
            lastArea = area;
            lastPosition = position;
            return;
        }

        if (area != lastArea.Value)
        {
            lastArea = area;
            pendingSend = true;
        }
        else if (!pendingSend && Vector2.Distance(new Vector2(position.X, position.Z), new Vector2(lastPosition.X, lastPosition.Z)) > JumpDistance)
        {
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
            .Where(p => p.Direction == PairingDirection.OwnerSide && p.IsPaired && estimates.For(p) is { Leashed: true })
            .ToList();
        if (leashed.Count == 0)
            return;

        var territory = Plugin.ClientState.TerritoryType;
        var cantFollow = TeleportDestinations.IsInsideHousing() || TeleportDestinations.IsInnRoom(territory)
            || Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56] || Plugin.Condition[ConditionFlag.BoundByDuty95];
        if (cantFollow || !TeleportSendAction.TryResolveTarget(out var target, out _))
        {
            foreach (var pairing in leashed)
                estimates.MarkUnleashed(pairing.Id);
            Plugin.NotificationManager.AddNotification(new Notification
            {
                Title = "Leash came off",
                Content = leashed.Count == 1
                    ? $"{leashed[0].PeerName} can't follow you here, so their leash came off."
                    : "Your Subs can't follow you here, so their leashes came off.",
                Type = NotificationType.Info,
            });
            return;
        }

        foreach (var pairing in leashed)
            sender.Send(composer.ComposeLeashTravel(pairing, target));
    }
}
