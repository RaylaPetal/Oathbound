using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Safety;

/// collar/status-indicators + collar/leash-visual: one answer per character for both renderers. The local
/// player gets the Sub's actual live state; any other player gets the estimate of the Owner-side pairing
/// whose peer name and home world match them. A Switch naturally gets both. Read-only - nothing here ever
/// sends anything or changes state.
public sealed class StatusIndicatorState
{
    private readonly PluginConfig config;
    private readonly SubRuntimeState runtimeState;
    private readonly RestraintCommand restraints;
    private readonly RestrictionRuleManager restrictionRules;
    private readonly FollowCommand follow;
    private readonly OwnerStatusEstimateTracker estimates;

    public StatusIndicatorState(PluginConfig config, SubRuntimeState runtimeState, RestraintCommand restraints, RestrictionRuleManager restrictionRules, FollowCommand follow, OwnerStatusEstimateTracker estimates)
    {
        this.config = config;
        this.runtimeState = runtimeState;
        this.restraints = restraints;
        this.restrictionRules = restrictionRules;
        this.follow = follow;
        this.estimates = estimates;
    }

    public CharacterStatus Get(IPlayerCharacter character)
    {
        if (Plugin.ObjectTable.LocalPlayer is { } me && me.Address == character.Address)
        {
            // collar/leash-travel: still leashed while waiting for or traveling to the Owner (no hand end then).
            var leashed = runtimeState.MovementLockActive && follow.IsLeashed;
            return new CharacterStatus(
                restrictionRules.IsActive(RestraintRuleKind.Gagged),
                restraints.ActiveDeviceIds.Count > 0,
                leashed,
                leashed ? follow.FollowedObjectId : 0);
        }

        // Called per visible nameplate every frame: match the cheap name first, and only resolve the home
        // world (a sheet lookup) for a character that already shares a paired Sub's name.
        var name = character.Name.TextValue;
        PairingState? pairing = null;
        string? world = null;
        foreach (var p in config.Pairings)
        {
            if (p.Direction != PairingDirection.OwnerSide || !p.IsPaired || !string.Equals(p.PeerName, name, StringComparison.OrdinalIgnoreCase)) continue;
            world ??= character.HomeWorld.ValueNullable?.Name.ExtractText();
            if (!string.Equals(p.PeerWorld, world, StringComparison.OrdinalIgnoreCase)) continue;
            pairing = p;
            break;
        }
        if (pairing is null || estimates.For(pairing) is not { } estimate) return CharacterStatus.None;

        var localId = Plugin.ObjectTable.LocalPlayer?.GameObjectId ?? 0;
        return new CharacterStatus(estimate.Gagged, estimate.Restrained, estimate.Leashed, estimate.Leashed ? localId : 0);
    }

    /// collar/leash-visual: every (Sub, Owner) pair whose leash this client should draw, with both
    /// characters present in the current area, and the leash length to draw it at (collar/leash). Sub side
    /// from live state (the length after the Sub's own limit); Owner side from each Owner-side pairing's
    /// estimate (the length it sent). A pair whose other end isn't in the object table is simply not yielded.
    public IEnumerable<(IPlayerCharacter Sub, IPlayerCharacter Owner, float Length)> LeashedPairs()
    {
        if (Plugin.ObjectTable.LocalPlayer is not { } me) yield break;

        if (runtimeState.MovementLockActive && follow.FollowedObjectId != 0 &&
            Plugin.ObjectTable.SearchById(follow.FollowedObjectId) is IPlayerCharacter owner)
            yield return (me, owner, follow.EffectiveLength);

        foreach (var pairing in config.Pairings)
        {
            if (pairing.Direction != PairingDirection.OwnerSide || !pairing.IsPaired) continue;
            if (estimates.For(pairing) is not { Leashed: true } estimate) continue;
            if (FindPlayer(pairing.PeerName!, pairing.PeerWorld!) is { } sub)
                yield return (sub, me, estimate.LeashLength);
        }
    }

    private static IPlayerCharacter? FindPlayer(string name, string world)
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter pc || !string.Equals(pc.Name.TextValue, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(pc.HomeWorld.ValueNullable?.Name.ExtractText(), world, StringComparison.OrdinalIgnoreCase))
                return pc;
        }
        return null;
    }
}

/// `LeashPeerObjectId` is the character holding the leash's hand end (the Owner), or 0 when not leashed.
public readonly record struct CharacterStatus(bool Gagged, bool Restrained, bool Leashed, ulong LeashPeerObjectId)
{
    public static readonly CharacterStatus None = new(false, false, false, 0);

    public bool Any => Gagged || Restrained || Leashed;

    /// Small bitmask for change detection (collar/status-indicators: redraw nameplates only on a change).
    public int Bits => (Gagged ? 1 : 0) | (Restrained ? 2 : 0) | (Leashed ? 4 : 0);
}
