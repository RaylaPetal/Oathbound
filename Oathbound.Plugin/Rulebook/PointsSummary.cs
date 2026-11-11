using System.Collections.Generic;
using System.Linq;

namespace Oathbound.Plugin.Rulebook;

/// Where a rulebook's points come from and where they go, read straight from its rules.
public sealed record PointsSummary(
    IReadOnlyList<PointsSummary.Line> Earn,
    IReadOnlyList<PointsSummary.Line> Lose,
    IReadOnlyList<ShopItem> Spend,
    IReadOnlyList<LedgerThreshold> Thresholds)
{
    public sealed record Line(string Rule, string When, int Points);

    public bool Empty => Earn.Count == 0 && Lose.Count == 0 && Spend.Count == 0 && Thresholds.Count == 0;

    public static PointsSummary From(RulebookDocument doc, string owner)
    {
        var lines = new List<Line>();
        void Add(string rule, string when, int points)
        {
            if (points != 0)
                lines.Add(new Line(rule, when, points));
        }
        static string Name(string name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();

        foreach (var o in doc.Oaths)
        {
            var name = Name(o.Name, "Oath");
            Add(name, "each time it's done", o.LedgerPerDone);
            Add(name, "each time it's missed", o.LedgerPerMissed);
            Add(name, "per strike", o.LedgerPerStrike);
            Add(name, $"every {o.StreakEvery} in a row", o.StreakEvery > 0 ? o.StreakLedger : 0);
            Add(name, "when kept", o.KeptLedger + RuleSentence.PointsIn(o.Kept));
            Add(name, "when broken", o.BrokenLedger + RuleSentence.PointsIn(o.Broken));
        }
        foreach (var p in doc.Places)
        {
            Add(Name(p.Name, "Place rule"), "on entering", RuleSentence.PointsIn(p.Enter));
            Add(Name(p.Name, "Place rule"), "on leaving", RuleSentence.PointsIn(p.Leave));
        }
        foreach (var p in doc.Presence)
        {
            Add(Name(p.Name, "Presence rule"), $"when {owner} arrives", RuleSentence.PointsIn(p.Arrive));
            Add(Name(p.Name, "Presence rule"), $"when {owner} leaves", RuleSentence.PointsIn(p.Depart));
        }
        foreach (var t in doc.Times)
            Add(Name(t.Name, "Schedule"), t.Trigger == TimeTrigger.OnLogin ? "at login" : $"{RuleText.Days(t.Weekdays).ToLowerInvariant()} at {OathText.Clock(t.Minutes)}", RuleSentence.PointsIn(t.Consequence));
        foreach (var c in doc.Deck)
            Add(Name(c.Name, "Card"), $"when drawn ({CardPiles.Word(c.Pile)})", RuleSentence.PointsIn(c.Consequence));
        foreach (var i in doc.Shop)
            Add(Name(i.Name, "Shop item"), "when bought, besides its price", RuleSentence.PointsIn(i.Consequence));

        return new PointsSummary(
            lines.Where(l => l.Points > 0).OrderByDescending(l => l.Points).ToList(),
            lines.Where(l => l.Points < 0).OrderBy(l => l.Points).ToList(),
            doc.Shop.OrderBy(i => i.Price).ToList(),
            doc.Thresholds.OrderBy(t => t.Score).ToList());
    }
}
