using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Safety;

/// One answer per character for icons and leash line: the local player's live state, or the estimate of the
/// Owner-side pairing whose peer matches. Read-only.
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

    /// Set once by Plugin: other players' shared tags, for characters with no pairing to this client.
    public SharedStatusReader? Shared { get; set; }

    public CharacterStatus Get(IPlayerCharacter character)
    {
        if (Plugin.ObjectTable.LocalPlayer is { } me && me.Address == character.Address)
        {
            // Still leashed while waiting for or traveling to the Owner.
            var leashed = runtimeState.MovementLockActive && follow.IsLeashed;
            return new CharacterStatus(
                restrictionRules.IsActive(RestraintRuleKind.Gagged),
                restraints.ActiveDeviceIds.Count > 0,
                leashed,
                leashed ? follow.FollowedObjectId : 0);
        }

        // Runs per nameplate every frame, so match the cheap name first and only then resolve the home world.
        var name = character.Name.TextValue;
        PairingState? pairing = null;
        string? world = null;
        foreach (var p in config.Pairings)
        {
            if (p.Direction != PairingDirection.OwnerSide || !p.IsPaired || !p.HasConfirmedName(name)) continue;
            world ??= character.HomeWorld.ValueNullable?.Name.ExtractText();
            if (!p.IsConfirmedCharacter(name, world)) continue;
            pairing = p;
            break;
        }
        // A pairing's own state wins; only an unpaired character falls back to their shared tag.
        if (pairing is null)
            return Shared?.For(character.GameObjectId) is { } shared
                ? new CharacterStatus(shared.Gagged, shared.Restrained, shared.Leashed, shared.Leashed ? Shared.OwnerOf(character.GameObjectId) : 0)
                : CharacterStatus.None;
        if (estimates.For(pairing) is not { } estimate) return CharacterStatus.None;

        var localId = Plugin.ObjectTable.LocalPlayer?.GameObjectId ?? 0;
        return new CharacterStatus(estimate.Gagged, estimate.Restrained, estimate.Leashed, estimate.Leashed ? localId : 0);
    }

    /// Every leash that is on, keyed by pairing so zoning and render range don't read as on/off. A character is
    /// null while it can't be found. Sub side uses the live length; Owner side the length it sent.
    public IEnumerable<(Guid PairingId, IPlayerCharacter? Sub, IPlayerCharacter? Owner, float Length)> ActiveLeashes()
    {
        if (Plugin.ObjectTable.LocalPlayer is not { } me) yield break;
        // Pairs this client already draws from its own pairings, so a shared tag doesn't draw them twice.
        var drawn = new HashSet<ulong>();

        if (runtimeState.MovementLockActive && follow.LeashedPairingId is { } leashedId)
        {
            var owner = follow.FollowedObjectId != 0 ? Plugin.ObjectTable.SearchById(follow.FollowedObjectId) as IPlayerCharacter : null;
            yield return (leashedId, me, owner, follow.EffectiveLength);
        }

        foreach (var pairing in config.Pairings)
        {
            if (pairing.Direction != PairingDirection.OwnerSide || !pairing.IsPaired) continue;
            if (estimates.For(pairing) is not { Leashed: true } estimate) continue;
            // Paused on the Sub's side, so no line, even with the Sub standing right here.
            var sub = estimate.PauseInDuties && TeleportDestinations.InDuty() ? null : FindSub(pairing);
            yield return (pairing.Id, sub, me, estimate.LeashLength);
            if (sub is not null)
                drawn.Add(sub.GameObjectId);
        }

        if (Shared is null)
            yield break;
        foreach (var (subId, status) in Shared.All())
        {
            if (!status.Leashed || subId == me.GameObjectId || drawn.Contains(subId)) continue;
            if (Plugin.ObjectTable.SearchById(subId) is not IPlayerCharacter sub) continue;
            var ownerId = Shared.OwnerOf(subId);
            var owner = ownerId == 0 ? null : Plugin.ObjectTable.SearchById(ownerId) as IPlayerCharacter;
            yield return (SharedLeashId(sub), sub, owner, status.LeashLength!.Value);
        }
    }

    /// Stable per Sub, so render range and zoning don't read as the leash going on and off.
    private static Guid SharedLeashId(IPlayerCharacter sub) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"oathbound-shared-leash:{sub.Name.TextValue}@{sub.HomeWorld.ValueNullable?.Name.ExtractText()}")));

    /// Every rendered character with drawn cuffs: the local player's engaged restraints, and each Owner-side estimate's.
    public IEnumerable<(IPlayerCharacter Sub, CuffSet Cuffs)> ActiveCuffs()
    {
        if (Plugin.ObjectTable.LocalPlayer is not { } me) yield break;

        var own = restraints.DrawnCuffs;
        if (own != CuffSet.None)
            yield return (me, own);

        foreach (var pairing in config.Pairings)
        {
            if (pairing.Direction != PairingDirection.OwnerSide || !pairing.IsPaired) continue;
            if (estimates.For(pairing) is not { Restrained: true, Cuffs: not CuffSet.None } estimate) continue;
            if (FindSub(pairing) is { } sub)
                yield return (sub, estimate.Cuffs);
        }
    }

    /// The send target if it's here, else any other confirmed character of the Sub that is.
    private static IPlayerCharacter? FindSub(PairingState pairing) =>
        FindPlayer(pairing.PeerName!, pairing.PeerWorld!) ??
        pairing.ConfirmedCharacters.Select(c => FindPlayer(c.Name, c.World)).FirstOrDefault(pc => pc is not null);

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

/// The Owner holding the hand end, or 0 when not leashed.
public readonly record struct CharacterStatus(bool Gagged, bool Restrained, bool Leashed, ulong LeashPeerObjectId)
{
    public static readonly CharacterStatus None = new(false, false, false, 0);

    public bool Any => Gagged || Restrained || Leashed;

    /// Nameplates are only redrawn when this changes.
    public int Bits => (Gagged ? 1 : 0) | (Restrained ? 2 : 0) | (Leashed ? 4 : 0);
}
