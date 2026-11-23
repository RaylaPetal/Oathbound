using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;
using Oathbound.Plugin.Rulebook;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    private void DrawLedgerTab(RulebookDocument draft)
    {
        using var section = Section.Begin("rbLedger", "Milestones");
        IconGlyph.WrappedDisabled("Points add up as rules run. Oaths and your Award/Penalize buttons move them; a milestone runs something when the points reach it.");
        DrawRuleListDetail("rbThresholdList", draft.Thresholds, t => t.Id, t => t.Name, "milestone", RuleText.Threshold, x => DrawThresholdEditor(x),
            () =>
            {
                using var full = ImRaii.Disabled(draft.Thresholds.Count >= RulebookLimits.MaxThresholds);
                if (ImGui.Button("+ New milestone"))
                    StartNew(draft.Thresholds, new LedgerThreshold { Name = "Too few points", Score = -5, Direction = ThresholdDirection.AtOrBelow, DrawCard = true, ResetTo = 0 }, t => t.Id);
            },
            "No milestones yet.");
    }

    private const string NewSubNote = "Needs your Sub on this version of Oathbound or newer - an older one refuses the whole rulebook until they update.";

    private void DrawThresholdEditor(LedgerThreshold t, EditorPart part = EditorPart.All)
    {
        ImGui.PushID(t.Id);
        var changed = false;
        if (part != EditorPart.Outcomes)
        {
            if (DrawName(t.Name, out var name)) { t.Name = name; changed = true; }

            ImGui.TextUnformatted("When the points are");
            var direction = (int)t.Direction;
            ItemWidth(130);
            if (ImGui.Combo("##direction", ref direction, ThresholdDirectionNames, ThresholdDirectionNames.Length)) { t.Direction = (ThresholdDirection)direction; changed = true; }
            ImGui.SameLine();
            var score = t.Score;
            ItemWidth(110);
            if (ImGui.InputInt("##score", ref score, 1, 5)) { t.Score = Math.Clamp(score, RulebookLimits.MinLedger, RulebookLimits.MaxLedger); changed = true; }

        }
        if (part != EditorPart.Details)
        {
            Section.SubHeading("Do");
            changed |= DrawConsequenceEditor("does", t.Consequence);
            var draw = t.DrawCard;
            if (ImGui.Checkbox($"Also draw a {CardPiles.Word(t.Pile)} card", ref draw)) { t.DrawCard = draw; changed = true; }
            IconGlyph.HelpMarker("At or above draws a reward, at or below draws a punishment.");
            var reset = t.ResetTo is not null;
            if (ImGui.Checkbox("Then reset the points", ref reset)) { t.ResetTo = reset ? 0 : null; changed = true; }
            if (t.ResetTo is { } to)
            {
                ImGui.SameLine();
                ItemWidth(110);
                if (ImGui.InputInt("to##reset", ref to, 1, 5)) { t.ResetTo = Math.Clamp(to, RulebookLimits.MinLedger, RulebookLimits.MaxLedger); changed = true; }
            }
            IconGlyph.HelpMarker("Without a reset, it fires once and fires again only after the points have moved away from the milestone and come back.");
            ImGui.Spacing();
            var cd = t.CooldownSeconds;
            if (DrawCooldown("thresholdCooldown", ref cd, "It won't fire again within this time.")) { t.CooldownSeconds = cd; changed = true; }
        }
        if (changed) plugin.Configuration.Save();
        if (part == EditorPart.All)
            DrawDoneButton();
        ImGui.PopID();
    }

    private static bool DrawLedgerDelta(string id, int value, Action<int> set)
    {
        ItemWidth(110);
        if (!ImGui.InputInt($"points##{id}", ref value, 1, 5))
            return false;
        set(Math.Clamp(value, -RulebookLimits.MaxLedgerChange, RulebookLimits.MaxLedgerChange));
        return true;
    }
}
