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
    private string newOutfitAlias = "";
    private int newOutfitDesignIndex;
    private AttachedMoodleRef? newOutfitMoodle;

    private bool newOutfitLocked = true;

    private readonly ListDetail outfitList = new();
    private bool creatingOutfitAlias;
    private const string NewOutfitKey = "new:outfit";

    private void DrawWardrobeModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.Tshirt, "Outfit");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Design folder allowlist and scanning live in Settings (gear icon). Define your outfit aliases below.");

        using (Section.Begin("outfitFixed", "Fixed words"))
        {
            DrawFixedWord("Release outfit", ControlWords.Unlock);
            IconGlyph.HelpMarker("For example: kae unlock. Releases the current outfit's locks and its attached moodle - your look stays as it is.");
        }

        var outfits = config.Aliases.Outfits;
        var items = outfits.Select((o, i) => new ListDetailItem($"alias:{i}", o.Alias, o.Locked ? "locked" : null)).ToList();
        if (creatingOutfitAlias)
            items.Add(new ListDetailItem(NewOutfitKey, "New outfit alias"));

        using (Section.Begin("outfitAliases", "Your outfit aliases"))
        {
            outfitList.Draw("outfits", items, DrawOutfitToolbar, item => DrawOutfitDetail(config, item),
                () => outfitList.Selected == NewOutfitKey && newOutfitAlias.Trim().Length > 0,
                item =>
                {
                    if (item?.Key != NewOutfitKey)
                        creatingOutfitAlias = false;
                },
                "No outfit aliases yet. Use + New to add one.");
        }
    }

    private void DrawOutfitToolbar()
    {
        if (!ImGui.Button("+ New##outfitAlias"))
            return;
        creatingOutfitAlias = true;
        newOutfitAlias = "";
        newOutfitMoodle = null;
        newOutfitLocked = true;
        outfitList.Select(NewOutfitKey);
    }

    private void DrawOutfitDetail(PluginConfig config, ListDetailItem? item)
    {
        var designs = config.WardrobeMapping.LocalDesigns.Values.ToList();
        var designNames = designs.Select(d => d.Name).ToArray();
        if (item is null)
        {
            IconGlyph.WrappedDisabled("Choose an outfit alias, or use + New to add one.");
            return;
        }

        if (item.Key == NewOutfitKey)
        {
            DrawOutfitAddForm(config, designs, designNames);
            return;
        }

        var outfits = config.Aliases.Outfits;
        if (!int.TryParse(item.Key["alias:".Length..], out var index) || index >= outfits.Count)
            return;
        var o = outfits[index];
        ImGui.PushID($"outfitAlias_{index}");
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.AccentHover, o.Alias);
        ImGui.SameLine();
        if (ImGui.Button("Delete"))
        {
            outfits.RemoveAt(index);
            config.Save();
            outfitList.Select(null);
            ImGui.PopID();
            return;
        }

        if (DrawDeferredTextInput("Alias##outfitAliasEdit", (o, "alias"), o.Alias, 32, out var aliasBuffer) && aliasBuffer.Trim().Length > 0 && !IsReserved(aliasBuffer))
        {
            o.Alias = aliasBuffer.Trim();
            config.Save();
        }
        IconGlyph.HelpMarker("Short word the Owner types after the trigger phrase to apply this outfit.");

        var designIndex = designs.FindIndex(d => d.DesignId == o.DesignId);
        if (designs.Count > 0 && ImGui.Combo("Design##outfitAliasEdit", ref designIndex, designNames, designNames.Length) && designIndex >= 0)
        {
            o.DesignId = designs[designIndex].DesignId;
            o.DesignName = designs[designIndex].Name;
            config.Save();
        }
        if (designIndex < 0)
            IconGlyph.WrappedDisabled($"Design: {o.DesignName} (not in the latest scan)");

        var locked = o.Locked;
        if (ImGui.Checkbox("Lock##outfitAliasEdit", ref locked))
        {
            o.Locked = locked;
            config.Save();
        }
        IconGlyph.HelpMarker("Locks the design's slots so only an unlock can change them.");

        if (DrawAttachedMoodlePicker($"outfitAlias_{index}", o.AttachedMoodle, config, out var aliasMoodle))
        {
            o.AttachedMoodle = aliasMoodle;
            config.Save();
        }
        ImGui.PopID();
    }

    private void DrawOutfitAddForm(PluginConfig config, List<WardrobeDesignEntry> designs, string[] designNames)
    {
        if (designs.Count == 0)
        {
            IconGlyph.WrappedDisabled("No scanned designs yet - rescan in Settings (gear icon) first.");
            return;
        }

        using (Section.Begin("outfitAdd", "Add an outfit alias"))
        {
            newOutfitDesignIndex = Math.Clamp(newOutfitDesignIndex, 0, designNames.Length - 1);
            ImGui.InputText("Alias##newOutfit", ref newOutfitAlias, 32);
            IconGlyph.HelpMarker("Short word the Owner types after the trigger phrase to apply this outfit.");
            ImGui.Combo("Design##newOutfit", ref newOutfitDesignIndex, designNames, designNames.Length);
            IconGlyph.HelpMarker("The Glamourer design this alias applies. Rescan if it's missing.");
            if (DrawAttachedMoodlePicker("newOutfit", newOutfitMoodle, config, out var pickedOutfitMoodle, width: null))
                newOutfitMoodle = pickedOutfitMoodle;
            ImGui.Checkbox("Lock##newOutfit", ref newOutfitLocked);
            IconGlyph.HelpMarker("Locks the design's slots so only an unlock can change them.");
            DrawReservedWordWarning(newOutfitAlias);
            if (ImGui.Button("Add outfit alias") && newOutfitAlias.Trim().Length > 0 && !IsReserved(newOutfitAlias))
            {
                var design = designs[newOutfitDesignIndex];
                config.Aliases.Outfits.Add(new OutfitAliasDefinition
                {
                    Alias = newOutfitAlias.Trim(),
                    DesignId = design.DesignId,
                    DesignName = design.Name,
                    Locked = newOutfitLocked,
                    AttachedMoodle = newOutfitMoodle,
                });
                config.Save();
                newOutfitAlias = "";
                newOutfitMoodle = null;
                creatingOutfitAlias = false;
                outfitList.Select($"alias:{config.Aliases.Outfits.Count - 1}");
            }
        }
    }

    private void DrawOutfitQuickSection(bool canSend)
    {
        DrawOutfitQuickSectionBody(canSend);
    }

    private void DrawOutfitQuickSectionBody(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Outfits;
        DrawSectionTitleRow(FontAwesomeIcon.Tshirt, "Outfit", quick.Count > 0, "outfitQuick", () =>
        {
            quick.Clear();
            plugin.Configuration.Save();
        });

        DrawCommandRow("outfitQuickFixed", canSend, new RowCommand("Unlock outfit", "outfit unlock", FixedActionIds.UnlockOutfit, "Lets your Sub change outfits again."));

        if (quick.Count == 0)
        {
            DrawGoToSyncTabPrompt("No outfits imported yet.");
            return;
        }

        using var _ = Section.Begin("outfitQuickList", "Your Sub's outfits");
        DrawSavedQuickListDetail("outfitQuick", quick, canSend,
            cmd => new ListDetailItem("", cmd.Label, cmd.Command.StartsWith("outfit wear ", StringComparison.OrdinalIgnoreCase) ? "not locked" : null),
            "No outfits imported yet.");
    }
}
