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
    /// A note only - the toggle stays usable so a permission can be set before installing the plugin.
    private void DrawPermissionDependencyNote(params DependencyId[] required)
    {
        if (DependencyGates.PermissionNote(plugin, required) is { } note)
            IconGlyph.WrappedColored(Theme.StatusMissing, note);
    }

    internal void DrawPermissionsCard()
    {
        var permissions = plugin.Configuration.Permissions;
        IconGlyph.Text(FontAwesomeIcon.ShieldAlt, "Permissions");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("What you'll accept from a paired Owner while you're set to Sub - each category is independent.");
        if (plugin.CageService.IsCaged)
            IconGlyph.WrappedColored(Theme.Warning, "You're caged, so your permissions are locked as they are until the cage opens. Panic still opens it.");
        using var frozen = ImRaii.Disabled(plugin.CageService.IsCaged);
        var group = Section.Begin("permBasic", "Everyday");

        if (ImGuiCheckbox("Title", permissions.Title, out var newTitle))
            SavePermission(() => permissions.Title = newTitle);
        IconGlyph.HelpMarker("Lets a paired Owner apply or clear your Honorific title via a trigger tell.");
        DrawPermissionDependencyNote(DependencyId.Honorific);

        if (ImGuiCheckbox("Outfit / Wardrobe", permissions.Outfit, out var newOutfit))
            SavePermission(() => permissions.Outfit = newOutfit);
        IconGlyph.HelpMarker("Lets a paired Owner apply or unlock a Glamourer design via a trigger tell.");
        DrawPermissionDependencyNote(DependencyId.Glamourer);

        group.Dispose();
        group = Section.Begin("permAutomation", "Automation (needs the ToS acknowledgement)");
        var config = plugin.Configuration;
        if (!config.TosAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Animation/Follow/Restraints/Teleport require the acknowledgement on the ToS tab first.");

        using (ImRaii.Disabled(!config.TosAcknowledged))
        {
            if (ImGuiCheckbox("Animation", permissions.Gesture, out var newGesture))
                SavePermission(() => permissions.Gesture = newGesture);
            IconGlyph.HelpMarker("Lets your Owner play your shared animations on you.");
            DrawPermissionDependencyNote(DependencyId.Penumbra);

            if (ImGuiCheckbox("Follow / Leash", permissions.Follow, out var newFollow))
                SavePermission(() => permissions.Follow = newFollow);
            IconGlyph.HelpMarker("Lets a paired Owner lock your movement to follow them, blocking your own WASD input until released. Heavier automation footprint than the other three - see the README's Automation risk section.");

            if (ImGuiCheckbox("Restraints", permissions.Restraints, out var newRestraints))
                SavePermission(() => permissions.Restraints = newRestraints);
            IconGlyph.HelpMarker("Lets a paired Owner apply or release a restraint device via a trigger tell. Restraint devices can suppress movement, force walking, block actions, garble your outgoing chat (Gagged), or hold you in a chosen animation (Arms/Legs/Full Body Cuffed) while active - Gagged rewrites content you actually typed, a heavier automation footprint than the others - see the Restraints tab and the README's Automation risk section.");
            DrawPermissionDependencyNote(DependencyId.Glamourer, DependencyId.Penumbra);

            if (ImGuiCheckbox("Teleport", permissions.Teleport, out var newTeleport))
                SavePermission(() => permissions.Teleport = newTeleport);
            IconGlyph.HelpMarker("Lets a paired Owner summon you to their side via a trigger tell: your client changes world, teleports to your nearest attuned aetheryte (or travels to their housing ward), then walks, rides or flies to them. Your movement is locked the whole way - press Stop teleport in the header (or panic) to end it. Refused automatically while you're bound by duty, in combat, or when they're inside a house. Requires Lifestream and vnavmesh.");
            DrawPermissionDependencyNote(DependencyGates.Teleport);
        }

        group.Dispose();
        group = Section.Begin("permCollarMoodles", "Collar & Moodles");
        if (ImGuiCheckbox("Collar", permissions.Collar, out var newCollar))
            SavePermission(() => permissions.Collar = newCollar);
        IconGlyph.HelpMarker("Locks your collar on automatically when you accept a pairing.");

        if (ImGuiCheckbox("Moodles", permissions.Moodles, out var newMoodles))
            SavePermission(() => permissions.Moodles = newMoodles);
        IconGlyph.HelpMarker("Lets your Owner add or clear your Moodles statuses.");
        DrawPermissionDependencyNote(DependencyId.Moodles);
        ImGui.Indent();
        using (ImRaii.Disabled(!permissions.Moodles))
        {
            if (ImGuiCheckbox("Allow moodles my Owner writes", permissions.OwnerWrittenMoodles, out var newOwnerWritten))
                SavePermission(() => permissions.OwnerWrittenMoodles = newOwnerWritten);
        }
        IconGlyph.HelpMarker("Your Owner can put moodles they wrote themselves on you - their own title, description and icon - without you making them first. Other players see moodles through sync tools. Moodles also has to allow it: in Moodles' settings, \"Allow other plugins apply Moodles.\" and allow your Owner (friends, party members or everyone).");
        ImGui.Unindent();

        group.Dispose();
        group = Section.Begin("permCatalog", "Catalog sync");
        if (ImGuiCheckbox("Catalog sync", permissions.RelayCatalogSync, out var newRelayCatalogSync))
            SavePermission(() => permissions.RelayCatalogSync = newRelayCatalogSync);
        IconGlyph.HelpMarker("Shares your catalog with your Owner automatically when it changes. Off by default.");

        group.Dispose();
        group = Section.Begin("permCustomChat", "Custom chat (own acknowledgement)");
        if (!config.CustomChatAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Needs its own acknowledgement on the ToS tab first.");

        using (ImRaii.Disabled(!config.CustomChatAcknowledged))
        {
            if (ImGuiCheckbox("Custom chat messages", permissions.CustomChatMessages, out var newCustomChat))
                SavePermission(() => permissions.CustomChatMessages = newCustomChat);
            IconGlyph.HelpMarker("Lets a Custom Trigger's chat action send arbitrary text to any channel (including public chat) as your own character. A materially broader automation surface than Animation's closed set of self-targeting commands - see the README's Automation risk section.");
        }

        group.Dispose();
        group = Section.Begin("permToyControl", "Toy control (own acknowledgement)");
        if (!config.ToyControlAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Needs its own acknowledgement on the ToS tab first.");

        using (ImRaii.Disabled(!config.ToyControlAcknowledged))
        {
            if (ImGuiCheckbox("Toy control", permissions.ToyControl, out var newToyControl))
                SavePermission(() => permissions.ToyControl = newToyControl);
            IconGlyph.HelpMarker("Lets a paired Owner remotely vibrate or run a pattern on a toy you have connected via Intiface Central, until it stops itself, you stop it, or a maximum duration elapses. Directly actuates a physical device, not just in-game state - the single heaviest automation footprint in this plugin - see the README's Automation risk section.");
        }

        group.Dispose();
        group = Section.Begin("permRulebook", "Rulebook (own acknowledgement)");
        if (!config.RulebookAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Needs its own acknowledgement on the ToS tab first.");

        using (ImRaii.Disabled(!config.RulebookAcknowledged))
        {
            if (ImGuiCheckbox("Rulebook", permissions.Rulebook, out var newRulebook))
                SavePermission(() => permissions.Rulebook = newRulebook);
            IconGlyph.HelpMarker("Lets a rulebook you accepted run by itself: oaths, card draws, place and presence rules and the ledger. Each consequence still needs its own permission above. Turning this off voids your open oaths.");
        }

        group.Dispose();
        group = Section.Begin("permCage", "Cage (own acknowledgement)");
        if (!config.CageAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Needs its own acknowledgement on the ToS tab first.");

        using (ImRaii.Disabled(!config.CageAcknowledged))
        {
            if (ImGuiCheckbox("Cage", permissions.Cage, out var newCage))
                SavePermission(() => permissions.Cage = newCage);
            IconGlyph.HelpMarker("Lets a paired Owner put you in a cage they saved: you're walked to it and kept inside until its time is up or they open it, even while you're away from the keyboard. While caged you can't leave, change these permissions, struggle, use keys, take restraints off or stop what your Owner does - only what you allow here applies. If you aren't there yet, the cage waits for you to arrive; turning this off while it waits calls it off. Panic always opens it. Requires vnavmesh to walk you in.");
            DrawPermissionDependencyNote(DependencyId.Vnavmesh);
        }
        group.Dispose();
    }

    private void SavePermission(Action apply)
    {
        if (plugin.CageService.IsCaged)
            return;
        apply();
        plugin.Configuration.Save();
    }

    private static bool ImGuiCheckbox(string label, bool value, out bool newValue)
    {
        newValue = value;
        var changed = ImGui.Checkbox(label, ref newValue);
        return changed;
    }
}
