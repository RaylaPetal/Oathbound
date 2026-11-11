using System;
using System.Collections.Generic;
using System.Linq;

namespace Oathbound.Plugin.Rulebook;

/// What a Sub has to do now, what's close to breaking, and what's done today, read from their open oaths.
public sealed record TodayView(IReadOnlyList<TodayView.Item> DueNow, IReadOnlyList<TodayView.Item> AtRisk, IReadOnlyList<string> DoneToday)
{
    /// `When` is a unix time for "due/starts in", or null.
    public sealed record Item(string Name, string What, long? When, string? Stake);

    private static readonly TimeSpan RiskWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan RitualRiskWindow = TimeSpan.FromHours(2);

    public bool Empty => DueNow.Count == 0 && AtRisk.Count == 0;

    public static TodayView Build(RulebookPairingState state, DateTime utcNow)
    {
        var due = new List<Item>();
        var risk = new List<Item>();
        var done = new List<string>();
        var now = new DateTimeOffset(utcNow).ToUnixTimeSeconds();
        var localNow = utcNow.ToLocalTime();

        foreach (var (_, s) in state.Oaths)
        {
            if (s is not { Status: OathStatus.Open, Terms: { } terms })
                continue;
            var name = string.IsNullOrWhiteSpace(terms.Name) ? "Oath" : terms.Name.Trim();

            if (OathConditions.CountsTimes(terms.Condition))
            {
                var end = RitualCalendar.RepeatEnd(s.PeriodStartUnixSeconds, terms.PeriodDays);
                if (s.PeriodCount >= terms.TimesPerPeriod)
                {
                    done.Add(name);
                    continue;
                }
                var progress = terms.TimesPerPeriod > 1 ? $" ({s.PeriodCount}/{terms.TimesPerPeriod})" : "";
                var item = new Item(name, RuleText.Cap(OathText.Describe(terms)) + progress, end,
                    RuleSentence.Outcome([], terms.LedgerPerDone) is { } reward ? $"done: {reward}" : null);
                if (end - now <= RitualRiskWindow.TotalSeconds)
                    risk.Add(item with { Stake = RuleSentence.Outcome(terms.Broken, terms.BrokenLedger + terms.LedgerPerMissed) is { } cost ? $"missed: {cost}" : "missing it breaks the oath" });
                else
                    due.Add(item);
                continue;
            }

            if (terms.Condition == OathCondition.SayGoodnight)
                due.Add(new Item(name, "Say goodnight before you log off", null, BrokenStake(terms)));

            if (terms.Condition == OathCondition.Curfew)
            {
                var start = localNow.Date.AddMinutes(terms.CurfewStartMinutes);
                if (start < localNow)
                    start = start.AddDays(1);
                if (start - localNow <= RiskWindow)
                    risk.Add(new Item(name, $"Log off before {OathText.Clock(terms.CurfewStartMinutes)}", new DateTimeOffset(start.ToUniversalTime()).ToUnixTimeSeconds(), BrokenStake(terms)));
            }

            if (OathConditions.CanStrike(terms.Condition) && terms.Strikes > 0 && s.StrikesUsed >= terms.Strikes)
                risk.Add(new Item(name, "No strikes left: the next slip breaks it", null, BrokenStake(terms)));
        }

        var today = localNow.Date;
        foreach (var e in state.Activity.Where(e => e.Kind is RulebookEventKind.OathKept && DateTimeOffset.FromUnixTimeSeconds(e.At).LocalDateTime.Date == today))
            done.Add(e.Text);

        return new TodayView(due.OrderBy(i => i.When ?? long.MaxValue).ToList(), risk.OrderBy(i => i.When ?? long.MaxValue).ToList(), done.Distinct().ToList());
    }

    private static string? BrokenStake(Oath terms) =>
        RuleSentence.Outcome(terms.Broken, terms.BrokenLedger) is { } cost ? $"broken: {cost}" : null;
}
