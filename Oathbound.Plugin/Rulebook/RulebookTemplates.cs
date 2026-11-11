using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Oathbound.Plugin.Rulebook;

/// Starter rulebooks an Owner can fill an empty draft with. Each call builds fresh ids. Only points and titles, so they
/// work whatever the Sub has installed or synced.
public static class RulebookTemplates
{
    public sealed record Template(string Name, string Description, Func<RulebookDocument> Build);

    public static readonly IReadOnlyList<Template> All =
    [
        new("Gentle", "A daily check-in, a goodnight and a pet name, small rewards, a little shop.", Gentle),
        new("Strict", "A daily bow, a forbidden word, a curfew and asking before logging off, with real punishments.", Strict),
        new("Point economy", "Points for everything, a bigger shop, and milestones at 50, 100 and 200.", PointEconomy),
    ];

    private const int Week = 7 * 1440;

    private static string Points(int n) => $"{ConsequenceValidator.LedgerWord} {(n > 0 ? "+" : "")}{n}";

    private static string Title(string text, Vector3 color) => Commands.TitleCommand.BuildStyleCommand(text, false, color);

    private static readonly Vector3 Pink = new(1f, 0.6f, 0.8f);
    private static readonly Vector3 Gold = new(1f, 0.85f, 0.35f);
    private static readonly Vector3 Grey = new(0.6f, 0.6f, 0.65f);

    private static uint EmoteId(string command)
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()
                .FirstOrDefault(e => e.TextCommand.ValueNullable?.Command.ExtractText() == command).RowId;
        }
        catch
        {
            return 0;
        }
    }

    private static Oath Ritual(string name, OathCondition condition, int perDone, int kept, int broken) => new()
    {
        Name = name, Condition = condition, Scope = OathScope.ForATime, DurationMinutes = Week,
        PeriodDays = 1, TimesPerPeriod = 1, LedgerPerDone = perDone, KeptLedger = kept, BrokenLedger = broken,
    };

    private static List<DeckCard> Cards(params (string Name, CardPile Pile, string Command)[] cards) =>
        cards.Select(c => new DeckCard { Name = c.Name, Pile = c.Pile, Consequence = { c.Command } }).ToList();

    private static RulebookDocument Gentle()
    {
        var doc = new RulebookDocument();
        doc.Oaths.Add(Ritual("Check in every day", OathCondition.CheckIn, 5, 10, -5));
        doc.Oaths.Add(new Oath { Scope = OathScope.ForATime, Name = "Say goodnight", Condition = OathCondition.SayGoodnight, DurationMinutes = Week, KeptLedger = 10, BrokenLedger = -5 });
        doc.Oaths.Add(new Oath { Scope = OathScope.ForATime, Name = "Call me Owner", Condition = OathCondition.AddressOwner, Phrase = "Owner", DurationMinutes = Week, KeptLedger = 10, Broken = { CardPiles.DrawCommand(CardPile.Punishment) } });
        doc.Deck.AddRange(Cards(
            ("A little treat", CardPile.Reward, Points(5)),
            ("Good pet", CardPile.Reward, Title("Good Pet", Pink)),
            ("Bonus", CardPile.Reward, Points(10)),
            ("Tsk", CardPile.Punishment, Points(-5)),
            ("Naughty", CardPile.Punishment, Title("Naughty", Grey)),
            ("Lose a few", CardPile.Punishment, Points(-10))));
        doc.DrawOn.OathKept = true;
        doc.Shop.Add(new ShopItem { Name = "Draw a reward", Price = 30, DrawReward = true });
        doc.Shop.Add(new ShopItem { Name = "Wear a sweet title", Price = 20, Consequence = { Title("Spoiled", Pink) } });
        doc.Thresholds.Add(new LedgerThreshold { Name = "50 points", Score = 50, Direction = ThresholdDirection.AtOrAbove, DrawCard = true });
        return doc;
    }

    private static RulebookDocument Strict()
    {
        var doc = new RulebookDocument();
        var bow = EmoteId("/bow");
        if (bow != 0)
        {
            var greet = Ritual("Bow to me every day", OathCondition.GreetOwner, 5, 15, -15);
            greet.EmoteId = bow;
            greet.Broken.Add(CardPiles.DrawCommand(CardPile.Punishment));
            doc.Oaths.Add(greet);
        }
        doc.Oaths.Add(new Oath { Scope = OathScope.ForATime, Name = "Never say \"whatever\"", Condition = OathCondition.ForbiddenWord, Phrase = "whatever", DurationMinutes = Week, KeptLedger = 15, BrokenLedger = -20, Broken = { CardPiles.DrawCommand(CardPile.Punishment) } });
        doc.Oaths.Add(new Oath { Scope = OathScope.ForATime, Name = "Curfew", Condition = OathCondition.Curfew, CurfewStartMinutes = 1 * 60, CurfewEndMinutes = 7 * 60, DurationMinutes = Week, KeptLedger = 10, BrokenLedger = -20 });
        doc.Oaths.Add(new Oath { Scope = OathScope.ForATime, Name = "Ask before logging off", Condition = OathCondition.AskBeforeLogoff, Phrase = "granted", DurationMinutes = Week, KeptLedger = 15, BrokenLedger = -25 });
        doc.Deck.AddRange(Cards(
            ("Earned it", CardPile.Reward, Points(10)),
            ("Obedient", CardPile.Reward, Title("Obedient", Gold)),
            ("Well done", CardPile.Reward, Points(15)),
            ("Disappointing", CardPile.Punishment, Points(-15)),
            ("Disobedient", CardPile.Punishment, Title("Disobedient", Grey)),
            ("Pay up", CardPile.Punishment, Points(-25))));
        doc.DrawOn.OathBroken = true;
        doc.Shop.Add(new ShopItem { Name = "Draw a reward", Price = 60, DrawReward = true, CooldownSeconds = 24 * 3600 });
        doc.Thresholds.Add(new LedgerThreshold { Name = "Too far below zero", Score = -10, Direction = ThresholdDirection.AtOrBelow, DrawCard = true, ResetTo = 0 });
        return doc;
    }

    private static RulebookDocument PointEconomy()
    {
        var doc = new RulebookDocument();
        doc.Oaths.Add(Ritual("Check in every day", OathCondition.CheckIn, 5, 20, -10));
        doc.Oaths.Add(new Oath { Name = "Message me every day", Condition = OathCondition.MessageOwner, Scope = OathScope.ForATime, DurationMinutes = Week, PeriodDays = 1, TimesPerPeriod = 1, LedgerPerDone = 5, LedgerPerMissed = -5, KeptLedger = 15 });
        doc.Oaths.Add(new Oath { Scope = OathScope.ForATime, Name = "Say goodnight", Condition = OathCondition.SayGoodnight, DurationMinutes = Week, KeptLedger = 15, BrokenLedger = -10 });
        doc.Oaths.Add(new Oath { Name = "No deaths next duty", Condition = OathCondition.NoDeaths, Scope = OathScope.NextDuty, KeptLedger = 10, BrokenLedger = -10 });
        doc.Deck.AddRange(Cards(
            ("Small bonus", CardPile.Reward, Points(5)),
            ("Bonus", CardPile.Reward, Points(10)),
            ("Jackpot", CardPile.Reward, Points(25)),
            ("Small fine", CardPile.Punishment, Points(-5)),
            ("Fine", CardPile.Punishment, Points(-10)),
            ("Big fine", CardPile.Punishment, Points(-25))));
        doc.DrawOn.OathKept = true;
        doc.DrawOn.OathBroken = true;
        doc.Shop.Add(new ShopItem { Name = "Draw a reward", Price = 25, DrawReward = true });
        doc.Shop.Add(new ShopItem { Name = "A pretty title for a day", Price = 40, Consequence = { Title("Treasured", Gold) } });
        doc.Shop.Add(new ShopItem { Name = "Pick tonight's activity", Price = 80, Consequence = { Title("Chose tonight", Pink) } });
        doc.Thresholds.Add(new LedgerThreshold { Name = "50 points", Score = 50, Direction = ThresholdDirection.AtOrAbove, DrawCard = true });
        doc.Thresholds.Add(new LedgerThreshold { Name = "100 points", Score = 100, Direction = ThresholdDirection.AtOrAbove, Consequence = { Title("Centurion", Gold) } });
        doc.Thresholds.Add(new LedgerThreshold { Name = "200 points", Score = 200, Direction = ThresholdDirection.AtOrAbove, DrawCard = true, ResetTo = 100 });
        return doc;
    }
}
