using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Glamourer.Api.Enums;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    private int newCollarMoodleStatusIndex;

    /// Capture-only, and editing is disabled while the collar is locked.
    private void DrawCollarModule()
    {
        var config = plugin.Configuration;
        var locked = plugin.RuntimeState.CollarForceLocked;
        IconGlyph.Text(FontAwesomeIcon.Lock, "Collar");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Choose a Neck collar, a left ring, or both. They lock on when you accept a pairing.");

        if (locked)
            IconGlyph.WrappedColored(Theme.Danger, "Locked - applied at pairing. Only your Owner's \"collar unlock\" or ending that pairing releases it. Your safeword leaves the collar on.");

        using var disabled = ImRaii.Disabled(locked);

        TwoColumns("collarColumns", () => DrawCollarGearSections(config), () => DrawCollarMoodleSection(config));
    }

    private void DrawCollarGearSections(PluginConfig config)
    {
        using (Section.Begin("collarItem", "Collar item"))
        {
            var collarChosenLabel = config.Collar.ItemId is { } collarItemId ? GetItemName(collarItemId) : "(none chosen)";
            TextWithActions($"Item: {collarChosenLabel}", ButtonWidth("Choose item..."));
            if (ImGui.Button("Choose item...##collar"))
                plugin.ItemPickerWindow.Open(ApiEquipSlot.Neck, (chosenId, _) => plugin.CollarCommand.ConfigureFromItem(chosenId));
            IconGlyph.HelpMarker("Any Neck item - it doesn't need to be owned.");

            if (config.Collar.HasNeckItem)
            {
                ContinueRowOrWrap(ButtonWidth("Clear"));
                if (ImGui.Button("Clear"))
                    plugin.CollarCommand.ClearConfiguredCollar();
            }
            else
            {
                IconGlyph.WrappedDisabled("No Neck item chosen.");
            }
        }

        using (Section.Begin("collarRing", "Ring"))
        {
            var ringChosenLabel = config.Collar.RingItemId is { } ringItemId ? GetItemName(ringItemId) : "(none chosen)";
            TextWithActions($"Ring: {ringChosenLabel}", ButtonWidth("Choose item..."));
            if (ImGui.Button("Choose item...##collarRing"))
                plugin.ItemPickerWindow.Open(ApiEquipSlot.LFinger, (chosenId, _) => plugin.CollarCommand.ConfigureRingFromItem(chosenId));
            IconGlyph.HelpMarker("Optional ring that locks with your collar. It doesn't need to be owned.");

            if (config.Collar.HasRing)
            {
                ContinueRowOrWrap(ButtonWidth("Clear"));
                if (ImGui.Button("Clear##collarRing"))
                    plugin.CollarCommand.ClearConfiguredRing();
            }
        }
    }

    private void DrawCollarMoodleSection(PluginConfig config)
    {
        using (Section.Begin("collarMoodle", "Collar moodle"))
        {
            var collarMoodleLabel = config.Collar.MoodleStatusName is { } assignedMoodleName ? MoodlesTextFormat.StripMarkup(assignedMoodleName) : "(none assigned)";
            TextWithActions($"Moodle: {collarMoodleLabel}", ImGui.CalcTextSize("(?)").X);
            IconGlyph.HelpMarker("Optional status shown while your collar is locked. Your safeword leaves it on.");

            var collarMoodleStatuses = config.MoodlesMapping.LocalCatalog.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            // Clearing an existing assignment stays available without Moodles.
            if (DependencyGates.FeatureBlockedReason(plugin, DependencyId.Moodles) is { } collarMoodleBlocked)
            {
                IconGlyph.WrappedColored(Theme.StatusMissing, collarMoodleBlocked);
            }
            else if (collarMoodleStatuses.Count == 0)
            {
                IconGlyph.WrappedDisabled("No scanned Moodles statuses yet - rescan in Settings (gear icon) first.");
            }
            else
            {
                var collarMoodleStatusNames = collarMoodleStatuses.Select(s => MoodlesTextFormat.StripMarkup(s.Name)).ToArray();
                newCollarMoodleStatusIndex = Math.Clamp(newCollarMoodleStatusIndex, 0, collarMoodleStatusNames.Length - 1);
                ItemWidth(220);
                ImGui.Combo("##newCollarMoodle", ref newCollarMoodleStatusIndex, collarMoodleStatusNames, collarMoodleStatusNames.Length);
                ContinueRowOrWrap(ButtonWidth("Assign"));
                if (ImGui.Button("Assign##collarMoodle"))
                {
                    var chosen = collarMoodleStatuses[newCollarMoodleStatusIndex];
                    config.Collar.MoodleStatusId = chosen.StatusId;
                    config.Collar.MoodleStatusName = chosen.Name;
                    config.Save();
                }
            }

            if (config.Collar.HasMoodleAssigned)
            {
                ContinueRowOrWrap(ButtonWidth("Clear"));
                if (ImGui.Button("Clear##collarMoodle"))
                {
                    config.Collar.MoodleStatusId = null;
                    config.Collar.MoodleStatusName = null;
                    config.Save();
                }
            }
        }
    }

    /// The collar applies at pairing acceptance or via `collar lock`; only the fixed rows are needed.
    private void DrawCollarQuickSection(bool canSend)
    {
        IconGlyph.Text(FontAwesomeIcon.Lock, "Collar");
        ImGui.Separator();
        DrawCommandRow("collarQuick", canSend,
            new RowCommand("Collar lock", "collar lock", FixedActionIds.CollarLock, "Puts your Sub's collar on and locks it."),
            new RowCommand("Collar unlock", "collar unlock", FixedActionIds.CollarUnlock, "Unlocks your Sub's collar. It stays on, just unlocked."));
    }
}
