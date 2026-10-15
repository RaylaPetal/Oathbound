using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// The leash's mount handling: pillion behind the Owner when in a party with a spare seat, otherwise Mount
/// Roulette, taking off and landing with the Owner. Never dismounts the Sub on Reset.
public sealed class LeashMountController
{
    private enum Phase { OnFoot, TryPillion, Pillion, TryMount, Mounted, Dismounting }

    private const uint JumpAction = 2;
    private const uint MountRouletteAction = 9;
    private const uint DismountAction = 23;

    private static readonly TimeSpan PillionTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActionRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeightHold = TimeSpan.FromSeconds(1);
    /// Fallback "Owner is flying" signal when the game's flag can't be read for them.
    private const float FlyingHeight = 3f;

    private readonly PluginConfig config;

    private Phase phase;
    private DateTime phaseStartedAt;
    private DateTime lastActionAt;
    /// The Sub dismounted themselves; set until the Owner is seen unmounted.
    private bool optedOut;
    /// Game follow until the Owner dismounts.
    private bool mountFailed;
    private DateTime? ownerHighSince;

    public LeashMountController(PluginConfig config)
    {
        this.config = config;
    }

    /// Mounted following needs the automation acknowledgement.
    public bool AutomationAllowed => config.TosAcknowledged;

    /// Moving would cancel the mount cast.
    public bool HoldStill => phase is Phase.TryPillion or Phase.TryMount;

    /// The game moves the Sub with the Owner, so the leash neither clamps nor pulls.
    public bool IsPillion => phase == Phase.Pillion;

    /// The Owner landed or dismounted while the Sub is still in the air.
    public bool Descend { get; private set; }

    public bool HandlesOwnerMount => AutomationAllowed && !optedOut && !mountFailed;

    public void Reset()
    {
        phase = Phase.OnFoot;
        Descend = false;
        optedOut = false;
        mountFailed = false;
        ownerHighSince = null;
    }

    public void Tick(IGameObject owner, IPlayerCharacter player, DateTime now)
    {
        Descend = false;
        if (!AutomationAllowed)
        {
            phase = Phase.OnFoot;
            return;
        }

        var ownerMounted = IsMounted(owner);
        var subMounted = Plugin.Condition[ConditionFlag.Mounted];
        var pillion = Plugin.Condition[ConditionFlag.RidingPillion];
        var subFlying = Plugin.Condition[ConditionFlag.InFlight];
        if (!ownerMounted)
        {
            optedOut = false;
            mountFailed = false;
        }

        switch (phase)
        {
            case Phase.OnFoot:
                if (!ownerMounted || optedOut || mountFailed || subMounted || pillion || Plugin.Condition[ConditionFlag.InCombat])
                    break;
                if (CanRidePillion(owner))
                {
                    Plugin.TargetManager.Target = owner;
                    Chat.SendMessage("/ridepillion <t>");
                    Enter(Phase.TryPillion, now);
                }
                else StartMount(now);
                break;

            case Phase.TryPillion:
                if (pillion) Enter(Phase.Pillion, now);
                else if (!ownerMounted) Enter(Phase.OnFoot, now);
                else if (now - phaseStartedAt > PillionTimeout) StartMount(now);
                break;

            case Phase.Pillion:
                if (pillion) break;
                // The game drops the passenger when the Owner dismounts; leaving otherwise was the Sub's choice.
                if (ownerMounted) optedOut = true;
                Enter(Phase.OnFoot, now);
                break;

            case Phase.TryMount:
                if (subMounted) Enter(Phase.Mounted, now);
                else if (!ownerMounted) Enter(Phase.OnFoot, now);
                else if (now - phaseStartedAt > MountTimeout)
                {
                    Plugin.Log.Info("Leash mount: couldn't mount here - falling back to follow while the Owner rides.");
                    mountFailed = true;
                    Enter(Phase.OnFoot, now);
                }
                break;

            case Phase.Mounted:
                if (!subMounted)
                {
                    if (ownerMounted) optedOut = true;
                    Enter(Phase.OnFoot, now);
                    break;
                }
                if (!ownerMounted)
                {
                    Enter(Phase.Dismounting, now);
                    goto case Phase.Dismounting;
                }
                var ownerFlying = IsOwnerFlying(owner, player, subFlying, now);
                if (ownerFlying && !subFlying && Player.CanFly && now - lastActionAt > ActionRetry)
                    UseGeneralAction(JumpAction, now); // spec "Fly with the Owner": a mounted jump takes off
                Descend = !ownerFlying && subFlying;
                break;

            case Phase.Dismounting:
                // Land first, never drop from the air.
                if (!subMounted || now - phaseStartedAt > DismountTimeout) Enter(Phase.OnFoot, now);
                else if (subFlying) Descend = true;
                else if (now - lastActionAt > ActionRetry) UseGeneralAction(DismountAction, now);
                break;
        }
    }

    private void StartMount(DateTime now)
    {
        if (!TryUseGeneralAction(MountRouletteAction, now))
        {
            Plugin.Log.Info("Leash mount: Mount Roulette isn't usable here - falling back to follow while the Owner rides.");
            mountFailed = true;
            Enter(Phase.OnFoot, now);
            return;
        }
        Enter(Phase.TryMount, now);
    }

    private void Enter(Phase next, DateTime now)
    {
        phase = next;
        phaseStartedAt = now;
    }

    /// Pillion only works within a party, on a mount with a spare seat.
    private static unsafe bool CanRidePillion(IGameObject owner)
    {
        if (!Plugin.PartyList.Any(m => string.Equals(m.Name.TextValue, owner.Name.TextValue, StringComparison.OrdinalIgnoreCase)))
            return false;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)owner.Address;
        if (character == null) return false;
        var mount = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Mount>().GetRowOrDefault(character->Mount.MountId);
        return mount?.ExtraSeats > 0;
    }

    private static unsafe bool IsMounted(IGameObject owner)
    {
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)owner.Address;
        return character != null && character->IsMounted();
    }

    /// The game's flag when true, otherwise mounted and clearly above a grounded Sub. Once both fly, only the flag
    /// can say the Owner landed.
    private unsafe bool IsOwnerFlying(IGameObject owner, IPlayerCharacter player, bool subFlying, DateTime now)
    {
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)owner.Address;
        if (character != null && character->MoveController.MovementState == FFXIVClientStructs.FFXIV.Client.Game.Character.MovementStateOptions.Flying)
            return true;
        if (subFlying)
            return ownerHighSince is not null;

        if (owner.Position.Y - player.Position.Y > FlyingHeight)
        {
            ownerHighSince ??= now;
            return now - ownerHighSince.Value >= HeightHold;
        }
        ownerHighSince = null;
        return false;
    }

    private unsafe bool TryUseGeneralAction(uint action, DateTime now)
    {
        var actions = ActionManager.Instance();
        if (actions == null || actions->GetActionStatus(ActionType.GeneralAction, action) != 0)
            return false;
        actions->UseAction(ActionType.GeneralAction, action);
        lastActionAt = now;
        return true;
    }

    private void UseGeneralAction(uint action, DateTime now) => TryUseGeneralAction(action, now);
}
