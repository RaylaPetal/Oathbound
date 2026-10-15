using System;
using Dalamud.Game.Config;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

namespace Oathbound.Plugin.Commands;

/// Shows Action Block on the hotbars; ActionBlockService does the actual blocking. Nothing is drawn: blockable
/// slots are swapped in memory to an always-unusable action, so the game renders its greyed icon. SavedHotbars is
/// never touched, so a reload restores the layout and a crash needs no cleanup. The hotbar is locked while shown
/// so a placeholder can't be dragged into the saved layout. Every entry point swallows its own failures - the
/// visuals must never affect the block itself.
public sealed unsafe class HotbarBlockVisuals
{
    /// An always-unusable action the game renders greyed out with a red slash on every job.
    private const uint PlaceholderActionId = 68;

    /// `_ActionBar`'s lock toggle node, hidden while shown.
    private const uint LockToggleNodeId = 21;

    private bool shown;
    private bool? lockedBefore;

    public bool IsShown => shown;

    /// Deferred to the next tick: this runs inside the chat hook, and changing hotbar UI state there crashed the game.
    public void Show()
    {
        shown = true;
        SwapBlockableSlots();
        Plugin.Framework.RunOnTick(() =>
        {
            if (shown)
                LockAndHideToggle();
        });
    }

    public void Hide()
    {
        if (!shown)
            return;
        shown = false;
        RestoreSavedHotbars();
        Plugin.Framework.RunOnTick(() =>
        {
            if (!shown)
                RestoreLock();
        });
    }

    /// Job, gearset and PvP changes reload the saved layout, so re-swap any blockable slot while shown. Idempotent.
    public void OnFrameworkUpdate()
    {
        if (shown)
            SwapBlockableSlots();
    }

    private static bool IsBlockable(RaptureHotbarModule.HotbarSlot* slot) =>
        slot->CommandType switch
        {
            RaptureHotbarModule.HotbarSlotType.Action => slot->CommandId != PlaceholderActionId,
            RaptureHotbarModule.HotbarSlotType.GeneralAction
                or RaptureHotbarModule.HotbarSlotType.Item
                or RaptureHotbarModule.HotbarSlotType.CraftAction
                or RaptureHotbarModule.HotbarSlotType.PetAction => true,
            _ => false,
        };

    private static RaptureHotbarModule* HotbarModule()
    {
        var framework = Framework.Instance();
        if (framework == null)
            return null;
        var uiModule = framework->GetUIModule();
        return uiModule == null ? null : uiModule->GetRaptureHotbarModule();
    }

    private static void SwapBlockableSlots()
    {
        try
        {
            var module = HotbarModule();
            if (module == null)
                return;

            var hotbars = module->Hotbars;
            for (var i = 0; i < hotbars.Length; i++)
            {
                var hotbar = hotbars.GetPointer(i);
                var slots = hotbar->Slots;
                for (var j = 0; j < slots.Length; j++)
                {
                    var slot = slots.GetPointer(j);
                    if (IsBlockable(slot))
                        slot->Set(module->UIModule, RaptureHotbarModule.HotbarSlotType.Action, PlaceholderActionId);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "HotbarBlockVisuals: failed to grey out hotbar slots.");
        }
    }

    private static void RestoreSavedHotbars()
    {
        try
        {
            var module = HotbarModule();
            var playerState = PlayerState.Instance();
            if (module == null || playerState == null)
                return;

            var jobId = (uint)playerState->CurrentClassJobId;
            for (var i = 0; i < module->Hotbars.Length; i++)
                module->LoadSavedHotbar(jobId, (uint)i);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "HotbarBlockVisuals: failed to restore saved hotbars - relogging restores them.");
        }
    }

    private static AddonActionBarBase* ActionBar()
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("_ActionBar").Address;
        return addon == null || !addon->IsReady ? null : (AddonActionBarBase*)addon;
    }

    private void LockAndHideToggle()
    {
        try
        {
            var bar = ActionBar();
            if (bar == null)
            {
                Plugin.Log.Warning("HotbarBlockVisuals: _ActionBar not ready - hotbar left unlocked.");
                return;
            }

            var locked = IsLocked();
            lockedBefore ??= locked;
            if (!locked)
                SetLocked(true);
            SetToggleVisible(bar, false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "HotbarBlockVisuals: failed to lock the hotbar.");
        }
    }

    private void RestoreLock()
    {
        try
        {
            var wasLocked = lockedBefore;
            lockedBefore = null;
            if (wasLocked == false && IsLocked())
                SetLocked(false);
            var bar = ActionBar();
            if (bar != null)
                SetToggleVisible(bar, true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "HotbarBlockVisuals: failed to restore the hotbar lock state.");
        }
    }

    /// Via the "Lock hotbar" option rather than a fake toggle click, which could crash the game.
    private static bool IsLocked() => Plugin.GameConfig.TryGet(UiConfigOption.HotbarLock, out bool locked) && locked;

    private static void SetLocked(bool locked) => Plugin.GameConfig.Set(UiConfigOption.HotbarLock, locked);

    private static void SetToggleVisible(AddonActionBarBase* bar, bool visible)
    {
        var node = ((AtkUnitBase*)bar)->GetNodeById(LockToggleNodeId);
        if (node != null)
            node->ToggleVisibility(visible);
    }
}
