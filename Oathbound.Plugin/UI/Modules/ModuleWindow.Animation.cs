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
    private string newGestureAlias = "";

    private string gestureModSearch = "";

    private readonly ListDetail gestureList = new();

    private void DrawGestureModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.TheaterMasks, "Animation");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Select animation mods and scan them in Settings. Pick an animation to see it and give it an alias.");

        var held = plugin.GestureCommand.IsHeld;
        if (plugin.GestureCommand.HasActiveTemporary && !held)
        {
            using (ImRaii.Disabled(plugin.CageService.IsCaged))
            {
                if (ImGui.Button("Reset active animation"))
                    plugin.GestureCommand.ResetActiveTemporary();
            }
            IconGlyph.HelpMarker("Puts the animation mod back to its saved settings now, instead of after the ~30s idle timeout.");
        }

        var gestures = config.Aliases.Gestures;
        var catalog = config.GestureMapping.LocalCatalog.Values.Where(e => e.Trigger != null)
            .OrderBy(e => e.ModName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.GroupOrder).ThenBy(e => e.OptionOrder).ThenBy(e => e.TriggerOrder)
            .ToList();
        var items = catalog.GroupBy(e => e.ModDirectory).Select(mod =>
        {
            var withAlias = mod.Count(e => gestures.Any(g => g.GestureId == e.Id));
            return new ListDetailItem($"mod:{mod.Key}", mod.First().ModName, $"{mod.Count()}",
                Tooltip: $"{mod.Count()} animation{(mod.Count() == 1 ? "" : "s")}, {withAlias} with an alias");
        }).ToList();
        // Aliases whose animation is gone from the latest scan, so they can still be found and deleted.
        var staleCount = gestures.Count(g => string.IsNullOrEmpty(g.GestureId) || !config.GestureMapping.LocalCatalog.ContainsKey(g.GestureId));
        if (staleCount > 0)
            items.Add(new ListDetailItem(StaleGesturesKey, "Missing from the scan", $"{staleCount}", Group: "Needs rescan"));

        using (Section.Begin("gestureAliases", "Your animations"))
        {
            gestureList.Draw("gestures", items, null, item => DrawGestureModDetail(config, gestures, catalog, item),
                () => newGestureAlias.Trim().Length > 0,
                _ =>
                {
                    newGestureAlias = "";
                    gestureAnimId = null;
                },
                "No scanned animations yet - rescan in Settings (gear icon) first.");
        }
    }

    private const string StaleGesturesKey = "stale";
    private string? gestureAnimId;

    private void DrawGestureModDetail(PluginConfig config, List<GestureAliasDefinition> gestures, List<GestureCatalogEntry> catalog, ListDetailItem? item)
    {
        if (item is null)
        {
            IconGlyph.WrappedDisabled("Choose an animation mod.");
            return;
        }

        if (item.Key == StaleGesturesKey)
        {
            IconGlyph.WrappedColored(Theme.Warning, "These aliases point at animations that aren't in the latest scan. Rescan, or delete them and make them again.");
            foreach (var (stale, index) in gestures.Select((g, i) => (g, i)).ToList())
            {
                if (!string.IsNullOrEmpty(stale.GestureId) && config.GestureMapping.LocalCatalog.ContainsKey(stale.GestureId))
                    continue;
                ImGui.PushID($"staleGesture_{index}");
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.AccentHover, stale.Alias);
                ImGui.SameLine();
                ImGui.TextDisabled($"{(stale.AnimationName.Length > 0 ? stale.AnimationName : stale.EmoteName)} ({stale.ModName})");
                ContinueRowOrWrap(ButtonWidth("Delete"));
                if (ImGui.Button("Delete"))
                {
                    gestures.RemoveAt(index);
                    config.Save();
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }
            return;
        }

        var anims = catalog.Where(e => $"mod:{e.ModDirectory}" == item.Key).ToList();
        if (anims.Count == 0)
            return;
        var selected = anims.FirstOrDefault(e => e.Id == gestureAnimId) ?? anims[0];
        ImGui.TextColored(Theme.AccentHover, anims[0].ModName);
        ImGui.TextDisabled($"{anims.Count} animation{(anims.Count == 1 ? "" : "s")}");

        Section.SubHeading("Animations");
        DrawAnimationPicker(anims, selected.Id, e => gestures.Where(g => g.GestureId == e.Id).Select(g => g.Alias), e => e.GroupName,
            e => $"{e.AnimationName} - {e.Trigger!.Label}", e => e.Id, id =>
            {
                gestureAnimId = id;
                newGestureAlias = "";
            });

        DrawGestureDetail(config, gestures, selected);
    }

    private void DrawGestureDetail(PluginConfig config, List<GestureAliasDefinition> gestures, GestureCatalogEntry entry)
    {
        if (entry.Trigger is null)
            return;
        ImGui.PushID($"gesture_{entry.Id}");
        ImGui.Spacing();
        ImGui.TextColored(Theme.AccentHover, entry.AnimationName);
        if (entry.GroupName.Length > 0)
            ImGui.TextDisabled(entry.GroupName);
        ImGui.TextUnformatted($"Plays: {entry.Trigger.DisplayName}");
        if (entry.Trigger.Label != entry.Trigger.DisplayName)
            ImGui.TextDisabled(entry.Trigger.Label);
        if (entry.GroupSelections.Count > 0)
        {
            Section.SubHeading("Mod options it turns on");
            foreach (var (group, options) in entry.GroupSelections)
                IconGlyph.WrappedDisabled($"{group}: {string.Join(", ", options)}");
        }

        Section.SubHeading("Aliases");
        var bound = gestures.Select((g, i) => (g, i)).Where(x => x.g.GestureId == entry.Id).ToList();
        if (bound.Count == 0)
            IconGlyph.WrappedDisabled("No alias plays this yet.");
        foreach (var (alias, index) in bound)
        {
            ImGui.PushID($"gestureAlias_{index}");
            if (DrawDeferredTextInput("##aliasEdit", (alias, "alias"), alias.Alias, 32, out var renamed) && renamed.Trim().Length > 0 && !IsReserved(renamed))
            {
                alias.Alias = renamed.Trim();
                config.Save();
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                gestures.RemoveAt(index);
                config.Save();
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        Section.SubHeading("Add an alias");
        ItemWidth(160);
        ImGui.InputTextWithHint("##newGesture", "word", ref newGestureAlias, 32);
        IconGlyph.HelpMarker("Short word your Owner sends to play this animation.");
        DrawReservedWordWarning(newGestureAlias);
        using (ImRaii.Disabled(newGestureAlias.Trim().Length == 0 || IsReserved(newGestureAlias)))
        {
            if (ImGui.Button("Add alias##newGesture"))
            {
                gestures.Add(new GestureAliasDefinition
                {
                    Alias = newGestureAlias.Trim(),
                    GestureId = entry.Id,
                    AnimationName = entry.AnimationName,
                    ModDirectory = entry.ModDirectory,
                    ModName = entry.ModName,
                    EmoteName = entry.Trigger.DisplayName.TrimStart('/'),
                });
                config.Save();
                newGestureAlias = "";
            }
        }
        ImGui.PopID();
    }

    private void DrawGestureQuickSection(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Gestures;
        // No clear-all here: wiping the imported list was too easy to hit while trying to stop an animation.
        IconGlyph.Text(FontAwesomeIcon.TheaterMasks, "Animation");
        ImGui.Separator();

        // Drawn even with nothing imported - the Sub may be held by an alias or a Custom Trigger.
        DrawCommandRow("gestureQuickFixed", canSend, new RowCommand("Stop animation", $"gesture {ChatComposer.StopGestureWord}", FixedActionIds.StopAnimation,
            "An animation you send holds your Sub in place. This stops it and gives them their movement back."));

        if (quick.Count == 0)
        {
            DrawGoToSyncTabPrompt("No animations imported yet.");
            return;
        }

        // Manifest order within a mod, not alphabetical - "10" would sort before "2". Sorting the saved list itself
        // keeps the list keys (positions) matching what's shown.
        var ordered = quick.OrderBy(c => c.GestureModName ?? "￿", StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.GestureGroupOrder).ThenBy(c => c.GestureOptionOrder).ToList();
        if (!ordered.SequenceEqual(quick))
        {
            quick.Clear();
            quick.AddRange(ordered);
        }

        using var _ = Section.Begin("gestureQuickList", "Your Sub's animations");
        static string ModOf(QuickCommand c) => c.GestureModName ?? "Other";
        var items = quick.GroupBy(ModOf).Select(mod => new ListDetailItem($"mod:{mod.Key}", mod.Key, $"{mod.Count()}",
            Tooltip: $"{mod.Count()} animation{(mod.Count() == 1 ? "" : "s")}")).ToList();
        var listDetail = QuickList("gestureQuick");
        listDetail.Draw("gestureQuick", items, null, item =>
            {
                if (item is null)
                {
                    IconGlyph.WrappedDisabled("Choose an animation mod.");
                    return;
                }
                var anims = quick.Where(c => $"mod:{ModOf(c)}" == item.Key).ToList();
                if (anims.Count == 0)
                    return;
                var selected = ownerGestureAnim is { } current && anims.Contains(current) ? current : anims[0];
                ownerGestureAnim = selected;
                ImGui.TextColored(Theme.AccentHover, item.Label);
                ImGui.TextDisabled($"{anims.Count} animation{(anims.Count == 1 ? "" : "s")}");

                Section.SubHeading("Animations");
                // Switching would drop an unsaved edit, so the others wait until it's saved or cancelled.
                var dirty = QuickEditDirty(selected);
                DrawAnimationPicker(anims, quick.IndexOf(selected).ToString(), _ => [], c => c.GestureGroupName,
                    c => AutoLabeledGesture(plugin.Configuration, c) is { } entry ? $"{entry.AnimationName} - {entry.Trigger!.Label}" : c.Label,
                    c => quick.IndexOf(c).ToString(),
                    id =>
                    {
                        CancelQuickCommandEdit();
                        ownerGestureAnim = quick[int.Parse(id)];
                    },
                    c => dirty && !ReferenceEquals(c, selected));

                ImGui.Spacing();
                DrawSavedQuickDetail(selected, quick, canSend, listDetail, cmd =>
                {
                    if (cmd.GestureGroupName is { Length: > 0 } group)
                        ImGui.TextDisabled(group);
                    if (AutoLabeledGesture(plugin.Configuration, cmd) is { Trigger: { } trigger })
                        ImGui.TextUnformatted($"Plays: {trigger.Label}");
                });
            },
            () => ownerGestureAnim is { } c && QuickEditDirty(c),
            _ =>
            {
                CancelQuickCommandEdit();
                ownerGestureAnim = null;
            },
            "No animations imported yet.");
    }

    private QuickCommand? ownerGestureAnim;

    /// Null for manual or renamed entries. Also used by SubControlWindow.
    internal static GestureExportEntry? AutoLabeledGesture(PluginConfig config, QuickCommand cmd) =>
        cmd.Target is { } target &&
        config.GestureMapping.ImportedPeerCatalog.TryGetValue(target, out var entry) &&
        entry.Trigger is not null && cmd.Label == entry.DisplayLabel
            ? entry
            : null;
}
