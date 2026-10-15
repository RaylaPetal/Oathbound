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
    private static readonly string[] PoseNames = ["Ground Sit", "Sit", "Doze"];

    private readonly Dictionary<string, RestraintRuleEditState> restraintRuleEdits = new();

    private sealed class RestraintRuleEditState
    {
        public bool ForcedPose;
        public int PoseIndex;
        public bool ForcedPoseIsMod;
        public string? ForcedPoseAnimationId;
        public bool WalkOnly;
        public bool ActionBlock;
        public bool Gagged;
        public string? GagAnimationId;
        public string? GagCustomizePresetId;
        public string? GagCustomizePresetLabel;
        public GagLevel GagLevel;
        public bool ArmsCuffed;
        public string? ArmsCuffedAnimationId;
        public bool LegsCuffed;
        public string? LegsCuffedAnimationId;
        public bool FullBodyCuffed;
        public string? FullBodyCuffedAnimationId;
        /// Device and mod restraints only; a rules-only restraint's cuffs are always drawn.
        public bool ArmsCuffedDrawn;
        public bool LegsCuffedDrawn;
        public bool FullBodyCuffedDrawn;
    }

    private static bool DrawRedrawOnApply(string id, ref bool redraw)
    {
        var changed = ImGui.Checkbox($"Redraw on apply##redraw_{id}", ref redraw);
        IconGlyph.HelpMarker("Redraws your character when this restraint goes on and when it comes off, even with \"Redraw after a mod change\" off in Settings. Turn it on if the restraint's mod or animation doesn't show until you redraw. Sync plugins re-send your character on every redraw.");
        return changed;
    }

    private const string OwnerRedrawHelp = "Redraws your Sub's character when this restraint goes on and when it comes off. Turn it on if the restraint's mod or animation doesn't show until they redraw. Sync plugins re-send their character on every redraw. Older versions of the plugin ignore it.";

    /// The saved command carries the request, so it's rebuilt as soon as the box changes.
    private void DrawOwnerRedraw(QuickCommand cmd)
    {
        var redraw = cmd.RestraintRedraw;
        var changed = ImGui.Checkbox($"Redraw on apply##ownerRedraw_{cmd.Label}", ref redraw);
        IconGlyph.HelpMarker(OwnerRedrawHelp);
        if (!changed)
            return;
        cmd.RestraintRedraw = redraw;
        if (cmd.RestraintRules is { Count: > 0 } rules)
            cmd.Command = cmd.RestraintCatalogId is { } catalogId && cmd.RestraintItemId is { } itemId
                ? RestraintCommand.BuildCatalogLockCommand(catalogId, cmd.Label, itemId, rules, redraw)
                : RestraintCommand.BuildLockCommand(cmd.Label, rules, redraw);
        plugin.Configuration.Save();
    }

    private static string PoseName(int poseModeId) => poseModeId is >= 1 and <= 3 ? PoseNames[poseModeId - 1] : "unknown";

    private static bool HasAnyRule(RestraintRuleEditState edit) =>
        edit.ForcedPose || edit.WalkOnly || edit.ActionBlock || edit.Gagged || edit.ArmsCuffed || edit.LegsCuffed || edit.FullBodyCuffed;

    /// Gagged's animation stays optional: its chat garble applies on its own. So do a rules-only restraint's cuff
    /// animations, since its cuffs are drawn.
    private bool BoundAnimationsConfigured(RestraintRuleEditState edit, bool rulesOnly = false)
    {
        bool Contains(string id) => ResolveOwnerModeView()
            ? plugin.Configuration.GestureMapping.ImportedPeerCatalog.ContainsKey(id)
            : plugin.Configuration.GestureMapping.LocalCatalog.ContainsKey(id);
        bool Valid(bool enabled, string? id) => !enabled || id is not null && Contains(id);
        bool ValidCuff(bool enabled, string? id) => rulesOnly && id is null || Valid(enabled, id);
        return ValidCuff(edit.ArmsCuffed, edit.ArmsCuffedAnimationId)
            && ValidCuff(edit.LegsCuffed, edit.LegsCuffedAnimationId)
            && ValidCuff(edit.FullBodyCuffed, edit.FullBodyCuffedAnimationId)
            && Valid(edit.ForcedPose && edit.ForcedPoseIsMod, edit.ForcedPoseAnimationId);
    }

    private static readonly string[] ForcedPoseSourceNames = ["Vanilla pose", "Animation mod"];

    /// `allowCustomizePreset` is only for the Sub's own editors - only the Sub's client can list their profiles.
    /// `rulesOnly` makes the cuffs drawn, with an optional animation.
    private void DrawRestraintRuleCheckboxes(RestraintRuleEditState edit, string idSuffix, bool allowCustomizePreset = false, bool rulesOnly = false)
    {
        void Cuff(string label, ref bool enabled, ref bool drawn, string? animationId, Action<string?> setAnimation, string cuffSuffix, string help)
        {
            if (rulesOnly)
            {
                ImGui.Checkbox($"{label} (drawn)##{idSuffix}{cuffSuffix}", ref enabled);
                IconGlyph.HelpMarker($"Draws cuffs on you until released. {help} Optionally also holds an animation.");
                if (enabled)
                {
                    ImGui.Indent();
                    DrawAnimationChooser(animationId, id => setAnimation(id), $"{idSuffix}{cuffSuffix}", () => setAnimation(null));
                    ImGui.Unindent();
                }
            }
            else
            {
                DrawBoundAnimationPicker(label, ref enabled, animationId, id => setAnimation(id), $"{idSuffix}{cuffSuffix}");
                IconGlyph.HelpMarker($"Holds you in the chosen animation until released. {help}");
                if (enabled)
                {
                    ImGui.Indent();
                    ImGui.Checkbox($"Draw cuffs##{idSuffix}{cuffSuffix}Drawn", ref drawn);
                    IconGlyph.HelpMarker("Also draws cuffs and chains for this rule. Leave it off if this restraint's own gear already shows them.");
                    ImGui.Unindent();
                }
            }
        }

        var cells = new List<Action>
        {
            () =>
            {
                ImGui.Checkbox($"Forced pose##{idSuffix}", ref edit.ForcedPose);
                IconGlyph.HelpMarker("Places you into the chosen pose (or holds a chosen animation) and fully blocks movement input until released.");
                if (edit.ForcedPose)
                {
                    ImGui.Indent();
                    var sourceIndex = edit.ForcedPoseIsMod ? 1 : 0;
                    if (ImGui.Combo($"Source##{idSuffix}ForcedPose", ref sourceIndex, ForcedPoseSourceNames, ForcedPoseSourceNames.Length))
                        edit.ForcedPoseIsMod = sourceIndex == 1;
                    if (edit.ForcedPoseIsMod)
                        DrawAnimationChooser(edit.ForcedPoseAnimationId, id => edit.ForcedPoseAnimationId = id, $"{idSuffix}ForcedPose");
                    else
                        ImGui.Combo($"Pose##{idSuffix}", ref edit.PoseIndex, PoseNames, PoseNames.Length);
                    ImGui.Unindent();
                }
            },
        };
        cells.Add(() => Cuff("Arms Cuffed", ref edit.ArmsCuffed, ref edit.ArmsCuffedDrawn, edit.ArmsCuffedAnimationId, id => edit.ArmsCuffedAnimationId = id,
            "Arms", "Cuffs your wrists together."));
        cells.Add(() => Cuff("Legs Cuffed", ref edit.LegsCuffed, ref edit.LegsCuffedDrawn, edit.LegsCuffedAnimationId, id => edit.LegsCuffedAnimationId = id,
            "Legs", "Cuffs your ankles together."));
        cells.Add(() =>
        {
            ImGui.Checkbox($"Walk-only##{idSuffix}", ref edit.WalkOnly);
            IconGlyph.HelpMarker("Forces walking and blocks running, without blocking directional movement input.");
        });
        cells.Add(() => DrawGaggedPicker(edit, idSuffix, allowCustomizePreset));
        cells.Add(() => Cuff("Fully Restrain", ref edit.FullBodyCuffed, ref edit.FullBodyCuffedDrawn, edit.FullBodyCuffedAnimationId, id => edit.FullBodyCuffedAnimationId = id,
            "FullBody", "Cuffs your wrists and ankles and chains them together, and blocks movement."));
        cells.Add(() =>
        {
            ImGui.Checkbox($"Action block##{idSuffix}", ref edit.ActionBlock);
            IconGlyph.HelpMarker("Blocks hotbar action/skill usage until released, without affecting movement.");
        });

        // A table, not legacy Columns: Columns break inside a ListDetail table cell and hide the left column. One row
        // per pair keeps each toggle's dependent controls under it.
        if (!ImGui.BeginTable($"restraintRules_{idSuffix}", 2, ImGuiTableFlags.SizingStretchSame))
            return;
        for (var i = 0; i < cells.Count; i += 2)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            cells[i]();
            ImGui.TableNextColumn();
            if (i + 1 < cells.Count)
                cells[i + 1]();
        }
        ImGui.EndTable();
    }

    private static readonly string[] GagLevelNames = ["Heavy", "Medium", "Light"];

    private void DrawGaggedPicker(RestraintRuleEditState edit, string idSuffix, bool allowCustomizePreset)
    {
        ImGui.Checkbox($"Gagged##{idSuffix}", ref edit.Gagged);
        IconGlyph.HelpMarker("Garbles your outgoing chat text - the actual transmitted message, not just your own display - until released. See the README's Automation risk section before enabling.");
        if (!edit.Gagged)
            return;

        ImGui.Indent();
        var level = (int)edit.GagLevel;
        ItemWidth(140);
        if (ImGui.Combo($"level##{idSuffix}GagLevel", ref level, GagLevelNames, GagLevelNames.Length))
            edit.GagLevel = (GagLevel)level;
        IconGlyph.HelpMarker("Heavy garbles every word. Medium muffles each word but keeps its first letter and length. Light garbles about one longer word in three. A Sub on an older Oathbound version is gagged at Heavy whatever you pick.");
        DrawAnimationChooser(edit.GagAnimationId, id => edit.GagAnimationId = id, $"{idSuffix}Gag", () => edit.GagAnimationId = null);
        if (allowCustomizePreset)
        {
            DrawCustomizePresetChooser(edit.GagCustomizePresetId, edit.GagCustomizePresetLabel, (id, label) =>
            {
                edit.GagCustomizePresetId = id;
                edit.GagCustomizePresetLabel = label;
            }, $"{idSuffix}GagCustomize");
        }
        ImGui.Unindent();
    }

    /// A delegate rather than `ref`, since the picker's callback fires on a later frame.
    private void DrawBoundAnimationPicker(string label, ref bool enabled, string? currentAnimationId, Action<string> onChosen, string idSuffix)
    {
        ImGui.Checkbox($"{label}##{idSuffix}", ref enabled);
        if (!enabled)
            return;

        ImGui.Indent();
        DrawAnimationChooser(currentAnimationId, onChosen, idSuffix);
        ImGui.Unindent();
    }

    private void DrawAnimationChooser(string? currentAnimationId, Action<string> onChosen, string idSuffix, Action? onCleared = null)
    {
        var ownerMode = ResolveOwnerModeView();
        var localCatalog = plugin.Configuration.GestureMapping.LocalCatalog;
        var peerCatalog = plugin.Configuration.GestureMapping.ImportedPeerCatalog;
        string? chosenLabel = null;
        var chosenMode = "";
        var stale = false;
        if (currentAnimationId is { } id)
        {
            if (ownerMode && peerCatalog.TryGetValue(id, out var peer))
            {
                chosenLabel = CommandPresentation.AnimationDisplayName(peer.GroupName, peer.AnimationName);
                chosenMode = $"{peer.ModName} — {(peer.Trigger is null ? "Enable option only" : peer.Trigger.Label)}";
            }
            else if (!ownerMode && localCatalog.TryGetValue(id, out var local))
            {
                chosenLabel = CommandPresentation.AnimationDisplayName(local.GroupName, local.AnimationName);
                chosenMode = $"{local.ModName} — {(local.Trigger is null ? "Enable option only" : local.Trigger.Label)}";
            }
            else
            {
                stale = true;
            }
        }

        var buttonText = stale ? "Missing - choose again..."
            : chosenLabel ?? "Choose animation...";
        if (DrawChoiceButton(buttonText, idSuffix, stale))
        {
            if (ownerMode) plugin.AnimationPickerWindow.OpenImportedForRestraint(chosen => onChosen(chosen.Id));
            else plugin.AnimationPickerWindow.OpenForRestraint(chosen => onChosen(chosen.Id));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(stale ? "This animation is no longer in the scanned catalog - click to choose again."
                : chosenLabel is null ? "Click to choose an animation."
                : $"{chosenLabel}{(chosenMode.Length > 0 ? $"\n{chosenMode}" : "")}\n\nClick to change.");
        if (onCleared is not null && currentAnimationId is not null)
            DrawChoiceClearButton(idSuffix, onCleared);
    }

    private static bool DrawChoiceButton(string text, string idSuffix, bool warn = false)
    {
        var clearWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
        var width = Math.Max(120f, ImGui.GetContentRegionAvail().X - clearWidth);
        if (warn)
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warning);
        var clicked = ImGui.Button($"{text}##choice_{idSuffix}", new Vector2(width, 0));
        if (warn)
            ImGui.PopStyleColor();
        return clicked;
    }

    private static void DrawChoiceClearButton(string idSuffix, Action onCleared)
    {
        ImGui.SameLine();
        // IconGlyph.Button's label is only the glyph, so scope its ID - a gag row has two of these.
        ImGui.PushID($"choiceClear_{idSuffix}");
        if (IconGlyph.Button(FontAwesomeIcon.Times, new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight())))
            onCleared();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clear");
        ImGui.PopID();
    }

    /// Customize+ profiles are flat and never shared via catalog sync, so there's no Owner mode here.
    private void DrawCustomizePresetChooser(string? currentPresetId, string? currentPresetLabel, Action<string?, string?> onChosen, string idSuffix)
    {
        var blocked = DependencyGates.FeatureBlockedReason(plugin, DependencyId.CustomizePlus);
        var buttonText = currentPresetId is null ? "Choose Customize+ preset..." : $"C+: {currentPresetLabel ?? currentPresetId}";
        using (ImRaii.Disabled(blocked is not null))
            if (DrawChoiceButton(buttonText, idSuffix))
                plugin.CustomizePresetPickerWindow.Open(profile => onChosen(profile.UniqueId.ToString(), profile.Name));
        if (blocked is not null)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(blocked);
            IconGlyph.WrappedColored(Theme.StatusMissing, blocked);
            return;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(currentPresetId is null ? "Optional - click to choose a Customize+ preset." : $"Customize+ preset: {currentPresetLabel ?? currentPresetId}\n\nClick to change.");
        if (currentPresetId is not null)
            DrawChoiceClearButton(idSuffix, () => onChosen(null, null));
    }

    private static void CopyRuleEdit(RestraintRuleEditState source, RestraintRuleEditState target)
    {
        target.ForcedPose = source.ForcedPose;
        target.PoseIndex = source.PoseIndex;
        target.ForcedPoseIsMod = source.ForcedPoseIsMod;
        target.ForcedPoseAnimationId = source.ForcedPoseAnimationId;
        target.WalkOnly = source.WalkOnly;
        target.ActionBlock = source.ActionBlock;
        target.Gagged = source.Gagged;
        target.GagAnimationId = source.GagAnimationId;
        target.GagCustomizePresetId = source.GagCustomizePresetId;
        target.GagCustomizePresetLabel = source.GagCustomizePresetLabel;
        target.GagLevel = source.GagLevel;
        target.ArmsCuffed = source.ArmsCuffed;
        target.ArmsCuffedAnimationId = source.ArmsCuffedAnimationId;
        target.LegsCuffed = source.LegsCuffed;
        target.LegsCuffedAnimationId = source.LegsCuffedAnimationId;
        target.FullBodyCuffed = source.FullBodyCuffed;
        target.FullBodyCuffedAnimationId = source.FullBodyCuffedAnimationId;
        target.ArmsCuffedDrawn = source.ArmsCuffedDrawn;
        target.LegsCuffedDrawn = source.LegsCuffedDrawn;
        target.FullBodyCuffedDrawn = source.FullBodyCuffedDrawn;
    }

    /// Falls back to the numeric id for sentinel values that don't resolve to a row.
    private static string GetItemName(ulong itemId)
    {
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault((uint)itemId);
        var name = row?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? $"Item #{itemId}" : name;
    }

    private static RestraintRuleEditState FromRules(List<RestraintRuleAssignment>? rules)
    {
        var edit = new RestraintRuleEditState();
        foreach (var rule in rules ?? [])
        {
            switch (rule.Kind)
            {
                case RestraintRuleKind.ForcedPose:
                    edit.ForcedPose = true;
                    if (rule.PoseModeId == 0)
                    {
                        edit.ForcedPoseIsMod = true;
                        edit.ForcedPoseAnimationId = rule.AnimationId;
                    }
                    else
                    {
                        edit.PoseIndex = Math.Clamp(rule.PoseModeId - 1, 0, PoseNames.Length - 1);
                    }
                    break;
                case RestraintRuleKind.WalkOnly: edit.WalkOnly = true; break;
                case RestraintRuleKind.ActionBlock: edit.ActionBlock = true; break;
                case RestraintRuleKind.Gagged:
                    edit.Gagged = true;
                    edit.GagAnimationId = rule.AnimationId;
                    edit.GagCustomizePresetId = rule.CustomizePresetId;
                    edit.GagCustomizePresetLabel = rule.CustomizePresetLabel;
                    edit.GagLevel = rule.GagLevel;
                    break;
                case RestraintRuleKind.ArmsCuffed: edit.ArmsCuffed = true; edit.ArmsCuffedAnimationId = NullIfBlank(rule.AnimationId); edit.ArmsCuffedDrawn = rule.Drawn; break;
                case RestraintRuleKind.LegsCuffed: edit.LegsCuffed = true; edit.LegsCuffedAnimationId = NullIfBlank(rule.AnimationId); edit.LegsCuffedDrawn = rule.Drawn; break;
                case RestraintRuleKind.FullBodyCuffed: edit.FullBodyCuffed = true; edit.FullBodyCuffedAnimationId = NullIfBlank(rule.AnimationId); edit.FullBodyCuffedDrawn = rule.Drawn; break;
            }
        }
        return edit;
    }

    /// A rules-only restraint's cuffs are always drawn.
    private List<RestraintRuleAssignment> ToRules(RestraintRuleEditState edit, bool rulesOnly = false)
    {
        string? LabelFor(string? id)
        {
            if (id is null) return null;
            if (ResolveOwnerModeView() && plugin.Configuration.GestureMapping.ImportedPeerCatalog.TryGetValue(id, out var peer))
                return CommandSelector.GestureSelector(peer, plugin.Configuration.GestureMapping.ImportedPeerCatalog.Values);
            return plugin.Configuration.GestureMapping.LocalCatalog.TryGetValue(id, out var local)
                ? CommandSelector.GestureLabel(local.ModName, local.GroupName, local.AnimationName, local.Trigger) : null;
        }
        var rules = new List<RestraintRuleAssignment>();
        if (edit.ForcedPose && edit.ForcedPoseIsMod)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ForcedPose, PoseModeId = 0, AnimationId = edit.ForcedPoseAnimationId, AnimationLabel = LabelFor(edit.ForcedPoseAnimationId) });
        else if (edit.ForcedPose)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ForcedPose, PoseModeId = edit.PoseIndex + 1 });
        if (edit.WalkOnly)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.WalkOnly });
        if (edit.ActionBlock)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ActionBlock });
        if (edit.Gagged)
        {
            rules.Add(new RestraintRuleAssignment
            {
                Kind = RestraintRuleKind.Gagged,
                AnimationId = edit.GagAnimationId,
                AnimationLabel = LabelFor(edit.GagAnimationId),
                CustomizePresetId = edit.GagCustomizePresetId,
                CustomizePresetLabel = edit.GagCustomizePresetLabel,
                GagLevel = edit.GagLevel,
            });
        }
        if (edit.ArmsCuffed)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ArmsCuffed, AnimationId = edit.ArmsCuffedAnimationId, AnimationLabel = LabelFor(edit.ArmsCuffedAnimationId), Drawn = rulesOnly || edit.ArmsCuffedDrawn });
        if (edit.LegsCuffed)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.LegsCuffed, AnimationId = edit.LegsCuffedAnimationId, AnimationLabel = LabelFor(edit.LegsCuffedAnimationId), Drawn = rulesOnly || edit.LegsCuffedDrawn });
        if (edit.FullBodyCuffed)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.FullBodyCuffed, AnimationId = edit.FullBodyCuffedAnimationId, AnimationLabel = LabelFor(edit.FullBodyCuffedAnimationId), Drawn = rulesOnly || edit.FullBodyCuffedDrawn });
        return rules;
    }
}
