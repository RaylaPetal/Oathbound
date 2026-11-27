using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    /// Every setting of one restraint while it's edited, kept until Save. The picture isn't part of it: it writes
    /// files, which a Discard would have to clean up. `Key` is the list key it was loaded for.
    private sealed class RestraintDraft
    {
        private readonly ModuleWindow window;

        public RestraintDraft(ModuleWindow window, string key, bool rulesOnly)
        {
            this.window = window;
            Key = key;
            RulesOnly = rulesOnly;
        }

        public string Key { get; set; }
        public bool RulesOnly { get; }
        public string Name = "";
        public string Alias = "";
        public ulong? ItemId;
        /// Owner mod restraints only: the piece is chosen at each send instead of ItemId.
        public bool PickAtSend;
        public AttachedMoodleRef? Moodle;
        public string? MoodleOverride;
        public bool Redraw;
        public RestraintRuleEditState Rules = new();

        public List<RestraintRuleAssignment> BuildRules() => window.ToRules(Rules, RulesOnly);

        private bool RulesDiffer(List<RestraintRuleAssignment>? saved) =>
            RestraintCommand.EncodeRuleTokens(BuildRules()) != RestraintCommand.EncodeRuleTokens(saved ?? []);

        public static RestraintDraft Load(ModuleWindow window, string key, ConfiguredModRestraint source) => new(window, key, rulesOnly: false)
        {
            Name = source.Name,
            Alias = source.Alias,
            ItemId = source.ItemId,
            Moodle = source.AttachedMoodle,
            Redraw = source.RedrawOnApply,
            Rules = FromRules(source.Rules),
        };

        /// A rules-only restraint's name is the word the Owner sends.
        public static RestraintDraft Load(ModuleWindow window, string key, RestraintDeviceDefinition source) => new(window, key, rulesOnly: true)
        {
            Alias = source.Name,
            Moodle = source.AttachedMoodle,
            Redraw = source.RedrawOnApply,
            Rules = FromRules(source.Rules),
        };

        public static RestraintDraft Load(ModuleWindow window, string key, QuickCommand source) => new(window, key, source.RestraintCatalogId is null)
        {
            Name = source.Label,
            ItemId = source.RestraintItemId,
            PickAtSend = source.RestraintPickAtSend,
            MoodleOverride = source.MoodleOverride,
            Redraw = source.RestraintRedraw,
            Rules = FromRules(source.RestraintRules),
        };

        public bool IsDirty(ConfiguredModRestraint source) =>
            Name.Trim() != source.Name.Trim() || Alias.Trim() != source.Alias.Trim() || ItemId != source.ItemId
            || Moodle?.StatusId != source.AttachedMoodle?.StatusId || Redraw != source.RedrawOnApply || RulesDiffer(source.Rules);

        public bool IsDirty(RestraintDeviceDefinition source) =>
            Alias.Trim() != source.Name.Trim() || Moodle?.StatusId != source.AttachedMoodle?.StatusId || Redraw != source.RedrawOnApply
            || RulesDiffer(source.Rules);

        public bool IsDirty(QuickCommand source) =>
            Name.Trim() != source.Label.Trim() || ItemId != source.RestraintItemId || PickAtSend != source.RestraintPickAtSend
            || MoodleOverride != source.MoodleOverride
            || Redraw != source.RestraintRedraw || RulesDiffer(source.RestraintRules);

        public void Apply(ConfiguredModRestraint target)
        {
            target.Name = Name.Trim();
            target.Alias = Alias.Trim();
            target.ItemId = ItemId;
            target.AttachedMoodle = Moodle;
            target.RedrawOnApply = Redraw;
            target.Rules = BuildRules();
        }

        /// An older device keeps its gear.
        public void Apply(RestraintDeviceDefinition target)
        {
            target.Name = Alias.Trim();
            target.AttachedMoodle = Moodle;
            target.RedrawOnApply = Redraw;
            target.Rules = BuildRules();
        }

        /// Rebuilds the command from the saved settings, so a send always matches what's saved.
        public void Apply(QuickCommand target)
        {
            var rules = BuildRules();
            target.Label = Name.Trim();
            target.RestraintPickAtSend = PickAtSend && target.RestraintCatalogId is not null;
            target.RestraintItemId = target.RestraintPickAtSend ? null : ItemId;
            target.MoodleOverride = MoodleOverride;
            target.RestraintRedraw = Redraw;
            target.RestraintRules = rules;
            if (target.RestraintPickAtSend)
            {
                target.Command = "";
                return;
            }
            if (rules.Count == 0)
                return;
            target.Command = target.RestraintCatalogId is { } catalogId
                ? ItemId is { } itemId ? RestraintCommand.BuildCatalogLockCommand(catalogId, target.Label, itemId, rules, Redraw) : target.Command
                : RestraintCommand.BuildWearCommand(null, null, target.Label, rules, Redraw);
        }
    }

    /// Save and Discard in the same place at the bottom of every restraint editor. `invalidReason` disables Save and
    /// says why.
    private static void DrawSaveBar(string id, bool dirty, string? invalidReason, Action onSave, Action onDiscard, string saveLabel = "Save")
    {
        ImGui.Spacing();
        ImGui.Separator();
        using (ImRaii.Disabled(!dirty || invalidReason is not null))
        {
            if (ImGui.Button($"{saveLabel}##saveBar_{id}"))
                onSave();
        }
        if (invalidReason is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(invalidReason);
        ImGui.SameLine();
        using (ImRaii.Disabled(!dirty))
        {
            if (ImGui.Button($"Discard##saveBar_{id}"))
                onDiscard();
        }
        if (dirty && invalidReason is not null)
            Status.Line(StatusKind.Caution, invalidReason);
        else if (dirty)
            Status.Line(StatusKind.Muted, "Unsaved changes.");
    }
}
