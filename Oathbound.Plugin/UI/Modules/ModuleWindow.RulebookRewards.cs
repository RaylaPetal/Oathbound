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
    private void DrawDeckTab(RulebookDocument draft)
    {
        var config = plugin.Configuration;
        using (Section.Begin("rbDeckDraws", "Draw automatically"))
        {
            IconGlyph.WrappedDisabled("These apply to the whole deck. Bad things draw from your punishments, good things from your rewards.");
            var d = draft.DrawOn;
            var changed = false;
            if (ImGui.BeginTable("rbDrawOn", 2, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted("A punishment when:");
                var b = d.OathBroken; if (ImGui.Checkbox("an oath is broken", ref b)) { d.OathBroken = b; changed = true; }
                var w = d.Wipe; if (ImGui.Checkbox("the party wipes", ref w)) { d.Wipe = w; changed = true; }
                var de = d.Death; if (ImGui.Checkbox("their character dies", ref de)) { d.Death = de; changed = true; }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted("A reward when:");
                var k = d.OathKept; if (ImGui.Checkbox("an oath is kept", ref k)) { d.OathKept = k; changed = true; }
                ImGui.EndTable();
            }
            var cd = d.CooldownSeconds;
            if (DrawCooldown("drawOnCooldown", ref cd, "At most one automatic draw in this many seconds.")) { d.CooldownSeconds = cd; changed = true; }
            if (changed) config.Save();
        }

        using var section = Section.Begin("rbDeck", $"Cards ({draft.Deck.Count}/{RulebookLimits.MaxDeckCards})");
        IconGlyph.WrappedDisabled("A draw picks one card from its pile at random. You can also draw by hand from the Overview tab.");
        float Chance(DeckCard card)
        {
            var total = draft.Deck.Where(c => c.Pile == card.Pile).Sum(c => c.Weight);
            return total > 0 ? card.Weight * 100f / total : 0;
        }
        DrawRuleListDetail("rbCardList", draft.Deck, c => c.Id, c => c.Name, "card",
            c => [("Does", RuleText.Cap(ConsequenceText.Describe(c.Consequence))), ("Chance", $"{Chance(c):0}%{(c.Once ? ", only once" : "")}")],
            c => DrawCardEditor(draft, c),
            () =>
            {
                using var full = ImRaii.Disabled(draft.Deck.Count >= RulebookLimits.MaxDeckCards);
                if (ImGui.Button("+ Punishment"))
                    StartNew(draft.Deck, new DeckCard { Name = "Punishment", Pile = CardPile.Punishment }, c => c.Id);
                ImGui.SameLine();
                if (ImGui.Button("+ Reward"))
                    StartNew(draft.Deck, new DeckCard { Name = "Reward", Pile = CardPile.Reward }, c => c.Id);
            },
            "No cards yet - a draw from an empty pile does nothing.",
            group: c => c.Pile == CardPile.Punishment ? "Punishments" : "Rewards",
            badge: c => $"{Chance(c):0}%",
            display: draft.Deck.OrderBy(c => c.Pile == CardPile.Punishment ? 0 : 1));
    }

    private void DrawShopTab(RulebookDocument draft)
    {
        using var section = Section.Begin("rbShop", $"Shop ({draft.Shop.Count}/{RulebookLimits.MaxShopItems})");
        IconGlyph.WrappedDisabled("Things your Sub can buy with ledger points, when they choose. Nothing - not you, not a rule - buys for them.");
        if (draft.Shop.Count > 0)
            IconGlyph.WrappedColored(Theme.Warning, NewSubNote);
        DrawRuleListDetail("rbShopList", draft.Shop, i => i.Id, i => i.Name, "shop item", RuleText.Shop, x => DrawShopEditor(x),
            () =>
            {
                using var full = ImRaii.Disabled(draft.Shop.Count >= RulebookLimits.MaxShopItems);
                if (ImGui.Button("+ New shop item"))
                    StartNew(draft.Shop, new ShopItem { Name = "A treat", Price = 10, DrawReward = true }, i => i.Id);
            },
            "Nothing for sale yet.", badge: i => $"{i.Price} pts");
    }

    private static readonly string[] TimeTriggerNames = ["At a time", "When they log in"];

    private void DrawShopEditor(ShopItem item, EditorPart part = EditorPart.All)
    {
        ImGui.PushID(item.Id);
        var changed = false;
        if (part != EditorPart.Outcomes)
        {
            if (DrawName(item.Name, out var name)) { item.Name = name; changed = true; }
            var price = item.Price;
            ItemWidth(110);
            if (ImGui.InputInt("price (points)##price", ref price, 1, 10)) { item.Price = Math.Clamp(price, RulebookLimits.MinPrice, RulebookLimits.MaxPrice); changed = true; }
        }
        if (part != EditorPart.Details)
        {
            var draw = item.DrawReward;
            if (ImGui.RadioButton("Draw a reward card##shopDraw", draw) && !draw)
            {
                item.DrawReward = true;
                item.Consequence.Clear();
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Run something##shopRun", !draw) && draw)
            {
                item.DrawReward = false;
                changed = true;
            }
            if (!item.DrawReward)
                changed |= DrawConsequenceEditor("gives", item.Consequence);
            else
                IconGlyph.WrappedDisabled("If the reward pile is empty they can't buy it, and keep their points.");
            var hasCooldown = item.CooldownSeconds > 0;
            if (ShowAdvanced(hasCooldown) && ImGui.Checkbox("Cooldown##shopHasCooldown", ref hasCooldown))
            {
                item.CooldownSeconds = hasCooldown ? 24 * 3600 : 0;
                changed = true;
            }
            if (hasCooldown)
            {
                ImGui.SameLine();
                var hours = Math.Max(1, item.CooldownSeconds / 3600);
                ItemWidth(100);
                if (ImGui.InputInt("hour(s) between buys##shopHours", ref hours, 1, 24)) { item.CooldownSeconds = Math.Clamp(hours, 1, 24 * 30) * 3600; changed = true; }
            }
        }
        if (changed) plugin.Configuration.Save();
        if (part == EditorPart.All)
            DrawDoneButton();
        ImGui.PopID();
    }

    private void DrawCardEditor(RulebookDocument draft, DeckCard card, EditorPart part = EditorPart.All)
    {
        ImGui.PushID(card.Id);
        var changed = false;
        if (part != EditorPart.Outcomes)
        {
            if (DrawName(card.Name, out var name)) { card.Name = name; changed = true; }

            var pile = (int)card.Pile;
            ItemWidth(160);
            if (ImGui.Combo("Pile##card", ref pile, CardPileNames, CardPileNames.Length)) { card.Pile = (CardPile)pile; changed = true; }
            IconGlyph.HelpMarker("Punishments come up when something bad happens, rewards when something good does.");

            if (ShowAdvanced(card.Weight != 1 || card.Once))
            {
            var weight = card.Weight;
            ItemWidth(120);
            if (ImGui.InputInt("Weight##card", ref weight, 1, 5)) { card.Weight = Math.Clamp(weight, RulebookLimits.MinCardWeight, RulebookLimits.MaxCardWeight); changed = true; }
            var total = draft.Deck.Where(c => c.Pile == card.Pile).Sum(c => c.Weight);
            ImGui.SameLine();
            ImGui.TextDisabled($"= {(total > 0 ? card.Weight * 100f / total : 0):0}% of {(card.Pile == CardPile.Reward ? "reward" : "punishment")} draws");
            IconGlyph.HelpMarker("Higher weight comes up more often. A weight 3 card comes up three times as often as a weight 1 card.");

            var once = card.Once;
            if (ImGui.Checkbox("Only once##card", ref once)) { card.Once = once; changed = true; }
            IconGlyph.HelpMarker("Leaves the pile after it's drawn, until you send the rulebook again.");
            }

        }
        if (part != EditorPart.Details)
        {
            Section.SubHeading("Does");
            changed |= DrawConsequenceEditor("does", card.Consequence);
        }
        if (changed) plugin.Configuration.Save();
        if (part == EditorPart.All)
            DrawDoneButton();
        ImGui.PopID();
    }
}
