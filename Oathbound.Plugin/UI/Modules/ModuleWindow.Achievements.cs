using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Achievements;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    private enum AchievementFilter { All, Unlocked, Locked }
    private static readonly string[] AchievementFilterNames = ["All", "Unlocked", "Locked"];
    private AchievementFilter achievementFilter;

    private void DrawAchievementsModule()
    {
        var service = plugin.Achievements;
        var forRole = AchievementCatalog.All.Where(ForMyRole).ToList();
        var total = forRole.Sum(a => a.Tiers.Length);
        var unlocked = forRole.Sum(service.UnlockedTiers);

        using (Section.Begin("achievementsSummary", "Achievements"))
        {
            ImGui.TextUnformatted($"{unlocked} / {total} unlocked");
            ImGui.ProgressBar(total == 0 ? 0f : (float)unlocked / total, new Vector2(-1, Layout.Scaled(8f)), "");
            IconGlyph.WrappedDisabled("Private to you: they're never sent to anyone, shown to your partner, or saved on the relay.");
            var filter = (int)achievementFilter;
            Layout.ItemWidth(160);
            if (ImGui.Combo("Show##achievementFilter", ref filter, AchievementFilterNames, AchievementFilterNames.Length))
                achievementFilter = (AchievementFilter)filter;
        }

        foreach (var group in forRole.Where(Visible).GroupBy(a => a.Category))
        {
            var defs = group.ToList();
            var done = defs.Count(d => service.UnlockedTiers(d) == d.Tiers.Length);
            using (ImRaii.PushId($"achCat_{group.Key}"))
            {
                if (!ImGui.CollapsingHeader($"{group.Key} ({done}/{defs.Count})###achCat", ImGuiTreeNodeFlags.DefaultOpen))
                    continue;
                foreach (var def in defs)
                    DrawAchievementRow(def);
            }
        }
    }

    private bool Visible(AchievementDef def)
    {
        var tiers = plugin.Achievements.UnlockedTiers(def);
        return achievementFilter switch
        {
            AchievementFilter.Unlocked => tiers > 0,
            AchievementFilter.Locked => tiers < def.Tiers.Length,
            _ => true,
        };
    }

    private bool ForMyRole(AchievementDef def) => AchievementCatalog.ForRole(def, plugin.Configuration.Role);

    private void DrawAchievementRow(AchievementDef def)
    {
        var service = plugin.Achievements;
        var tiers = service.UnlockedTiers(def);
        var hidden = def.Secret && tiers == 0;
        var color = tiers == 0 ? Theme.TextMuted : def.Tiered ? Theme.Tier(tiers - 1) : Theme.Accent;

        using var id = ImRaii.PushId(def.Id);
        ImGui.BeginGroup();
        using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
            ImGui.TextColored(color, (hidden ? FontAwesomeIcon.Question : def.Icon).ToIconString());
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(tiers == 0 ? Theme.TextMuted : ImGui.GetStyle().Colors[(int)ImGuiCol.Text], hidden ? "???" : def.Name);
        if (def.Tiered)
        {
            ImGui.SameLine();
            for (var t = 0; t < def.Tiers.Length; t++)
            {
                using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
                    ImGui.TextColored(t < tiers ? Theme.Tier(t) : Theme.TextMuted, FontAwesomeIcon.Circle.ToIconString());
                if (t < def.Tiers.Length - 1)
                    ImGui.SameLine(0, Layout.Scaled(2f));
            }
        }
        if (!hidden)
        {
            IconGlyph.WrappedDisabled(def.Description);
            if (tiers < def.Tiers.Length)
            {
                var value = Math.Min(service.Get(def.Stat), def.Tiers[tiers]);
                var target = def.Tiers[tiers];
                ImGui.ProgressBar(target == 0 ? 0f : (float)value / target, new Vector2(Layout.Scaled(220f), Layout.Scaled(14f)),
                    $"{AchievementCatalog.Format(value, def.Unit)} / {AchievementCatalog.Format(target, def.Unit)}");
            }
        }
        ImGui.EndGroup();
        ImGui.EndGroup();

        if (!hidden && tiers > 0 && ImGui.IsItemHovered())
        {
            var lines = Enumerable.Range(0, tiers)
                .Select(t => $"{(def.Tiered ? AchievementCatalog.TierName(t, def.Tiers.Length) : "Unlocked")}: {service.UnlockedAt(def, t)?.ToLocalTime():d}");
            ImGui.SetTooltip(string.Join("\n", lines));
        }
        ImGui.Spacing();
    }
}
