using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Owner side: after arriving in a new area (or jumping far inside one), sends one `leash travel` to every Sub it
/// shows as leashed - an automatic tell the README documents. Where the Sub can't be sent, the leash pauses.
public sealed class LeashTravelWatcher
{
    /// Farther than this between two updates is a relocation (an aethernet hop), not walking.
    private const float JumpDistance = 30f;
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
}
