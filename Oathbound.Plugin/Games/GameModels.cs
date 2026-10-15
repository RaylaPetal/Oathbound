using System;
using System.Collections.Generic;
using System.Linq;

namespace Oathbound.Plugin.Games;

public enum GameKind
{
    Deathroll,
    HighRoll,
    HigherLower,
    TwentyOne,
    TruthOrDare,
}

public enum RollChannel
{
    /// `/random <N>`, seen by everyone nearby.
    Nearby,
    /// `/dice party <N>`, seen by the party.
    Party,
}

public enum LossRule
{
    Strip,
    Forfeit,
    /// A piece and a forfeit for every lost round.
    Both,
}

public enum PromptRating
{
    Mild,
    Spicy,
    Explicit,
}

public enum GameEndKind
{
    /// A player who loses a round with nothing left to take off loses the game. Strip rules only.
    OutOfClothes,
    /// The first player to lose this many rounds loses the game.
    Rounds,
    /// Only Quit ends it.
    Never,
}

/// When a game is over. Both clients judge it from the same events, so both reach the same end.
public readonly record struct GameEnd(GameEndKind Kind, int Rounds = 0)
{
    public static readonly GameEnd Clothes = new(GameEndKind.OutOfClothes);
    public static readonly GameEnd Never = new(GameEndKind.Never);
    public static readonly int[] RoundChoices = [3, 5, 10];
    public static readonly GameEnd DefaultRounds = new(GameEndKind.Rounds, 5);

    /// The choices for a loss rule, in menu order.
    public static GameEnd[] Choices(LossRule loss) =>
        [.. loss == LossRule.Forfeit ? [] : new[] { Clothes }, .. RoundChoices.Select(n => new GameEnd(GameEndKind.Rounds, n)), Never];

    /// One of Choices(loss). Out of clothes means nothing under a forfeit-only game, so it falls back to rounds there.
    public GameEnd For(LossRule loss) => Kind switch
    {
        GameEndKind.OutOfClothes => loss == LossRule.Forfeit ? DefaultRounds : Clothes,
        GameEndKind.Rounds => RoundChoices.Contains(Rounds) ? this : DefaultRounds,
        _ => Never,
    };

    /// `end:clothes`, `end:<rounds>` or `end:never` in an invite.
    public string Word => Kind switch
    {
        GameEndKind.OutOfClothes => "end:clothes",
        GameEndKind.Rounds => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"end:{Rounds}"),
        _ => "end:never",
    };

    public static bool TryParse(string word, out GameEnd end)
    {
        end = Never;
        if (!word.StartsWith("end:", StringComparison.OrdinalIgnoreCase))
            return false;
        var value = word[4..];
        if (value.Equals("clothes", StringComparison.OrdinalIgnoreCase))
            end = Clothes;
        else if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rounds) && RoundChoices.Contains(rounds))
            end = new GameEnd(GameEndKind.Rounds, rounds);
        else if (!value.Equals("never", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public string Describe() => Kind switch
    {
        GameEndKind.OutOfClothes => "When someone runs out of clothes",
        GameEndKind.Rounds => $"First to lose {Rounds} rounds",
        _ => "Only when we quit",
    };
}

/// The inviter always goes first; both clients agree on seats from who sent the invite.
public enum Seat
{
    Inviter,
    Guest,
}

public static class Seats
{
    public static Seat Other(this Seat seat) => seat == Seat.Inviter ? Seat.Guest : Seat.Inviter;
}

/// The words each value travels as in a `collargame` tell.
public static class GameWords
{
    public static readonly IReadOnlyDictionary<GameKind, string> Kinds = new Dictionary<GameKind, string>
    {
        [GameKind.Deathroll] = "deathroll",
        [GameKind.HighRoll] = "highroll",
        [GameKind.HigherLower] = "hilo",
        [GameKind.TwentyOne] = "21",
        [GameKind.TruthOrDare] = "tod",
    };

    public static readonly IReadOnlyDictionary<RollChannel, string> Channels = new Dictionary<RollChannel, string>
    {
        [RollChannel.Nearby] = "near",
        [RollChannel.Party] = "party",
    };

    public static readonly IReadOnlyDictionary<LossRule, string> Losses = new Dictionary<LossRule, string>
    {
        [LossRule.Strip] = "strip",
        [LossRule.Forfeit] = "forfeit",
        [LossRule.Both] = "both",
    };

    public static readonly IReadOnlyDictionary<PromptRating, string> Ratings = new Dictionary<PromptRating, string>
    {
        [PromptRating.Mild] = "mild",
        [PromptRating.Spicy] = "spicy",
        [PromptRating.Explicit] = "explicit",
    };

    public static bool TryParse<T>(IReadOnlyDictionary<T, string> words, string word, out T value) where T : struct
    {
        foreach (var (key, text) in words)
        {
            if (!text.Equals(word, StringComparison.OrdinalIgnoreCase))
                continue;
            value = key;
            return true;
        }
        value = default;
        return false;
    }

    public static string Name(GameKind kind) => kind switch
    {
        GameKind.Deathroll => "Deathroll",
        GameKind.HighRoll => "High Roll",
        GameKind.HigherLower => "Higher or Lower",
        GameKind.TwentyOne => "Twenty-One",
        _ => "Truth or Dare",
    };

    public static string Name(LossRule rule) => rule switch
    {
        LossRule.Strip => "Strip",
        LossRule.Forfeit => "Forfeit",
        _ => "Strip and forfeit",
    };

    public static string Name(PromptRating rating) => rating switch
    {
        PromptRating.Mild => "Mild",
        PromptRating.Spicy => "Spicy",
        _ => "Explicit",
    };

    /// The game's roll commands go up to 999.
    public const int MaxRoll = 999;
    public static readonly int[] DeathrollStarts = [100, 500, MaxRoll];
    public const int DefaultDeathrollStart = MaxRoll;
}

/// What a game action did, for the log and for the session to act on.
public readonly record struct GameOutcome(bool Accepted, string? Note = null, Seat? Loser = null)
{
    public static GameOutcome Refused(string why) => new(false, why);
    public static GameOutcome Ok(string? note = null) => new(true, note);
    public static GameOutcome Lost(Seat loser, string note) => new(true, note, loser);
}

/// One game's rules. Deterministic, so both clients reach the same state from the same counted events.
public abstract class GameRules
{
    protected readonly Func<Seat, string> Name;

    protected GameRules(Func<Seat, string> name) => Name = name;

    public abstract GameKind Kind { get; }

    /// The maximum `seat` may roll now, or null when it isn't theirs to roll.
    public virtual int? ExpectedMax(Seat seat) => null;

    /// Why `seat` can't roll now, for a roll that doesn't count.
    public virtual string WhyNoRoll(Seat seat) => $"it's {Name(seat.Other())}'s turn";

    /// Called only with a roll whose maximum matched ExpectedMax.
    public virtual GameOutcome Roll(Seat seat, int value) => GameOutcome.Refused("this game has no rolls");

    public virtual bool CanCall(Seat seat) => false;
    public virtual GameOutcome Call(Seat seat, bool higher) => GameOutcome.Refused("no call is due");
    public virtual bool CanStand(Seat seat) => false;
    public virtual GameOutcome Stand(Seat seat) => GameOutcome.Refused("no stand is due");
    public virtual bool CanPick(Seat seat) => false;
    public virtual GameOutcome Pick(Seat seat, bool dare) => GameOutcome.Refused("no pick is due");
    public virtual bool CanPrompt(Seat seat) => false;
    public virtual GameOutcome Prompt(Seat seat, string text) => GameOutcome.Refused("no prompt is due");
    public virtual bool CanAnswer(Seat seat) => false;
    public virtual GameOutcome Answer(Seat seat, bool done) => GameOutcome.Refused("no answer is due");

    /// Who is up and for what, in a few words.
    public abstract string Status();

    public abstract bool IsTurnOf(Seat seat);

    public static GameRules Create(GameKind kind, Func<Seat, string> name, int deathrollStart) => kind switch
    {
        GameKind.Deathroll => new DeathrollRules(name, deathrollStart),
        GameKind.HighRoll => new HighRollRules(name),
        GameKind.HigherLower => new HigherLowerRules(name),
        GameKind.TwentyOne => new TwentyOneRules(name),
        _ => new TruthOrDareRules(name),
    };
}

public sealed class DeathrollRules : GameRules
{
    public int Start { get; }
    public int Current { get; private set; }
    public Seat Turn { get; private set; } = Seat.Inviter;

    public DeathrollRules(Func<Seat, string> name, int start) : base(name) => Current = Start = start;

    public override GameKind Kind => GameKind.Deathroll;
    public override int? ExpectedMax(Seat seat) => seat == Turn ? Current : null;
    public override bool IsTurnOf(Seat seat) => seat == Turn;
    public override string Status() => $"{Name(Turn)} rolls up to {Current}.";

    public override GameOutcome Roll(Seat seat, int value)
    {
        if (value <= 1)
        {
            Current = Start;
            Turn = seat;
            return GameOutcome.Lost(seat, $"{Name(seat)} rolled 1.");
        }
        Current = value;
        Turn = seat.Other();
        return GameOutcome.Ok($"{Name(seat)} rolled {value}.");
    }
}

public sealed class HighRollRules : GameRules
{
    public const int Max = GameWords.MaxRoll;
    private readonly int?[] rolls = new int?[2];

    public HighRollRules(Func<Seat, string> name) : base(name) { }

    public int? RollOf(Seat seat) => rolls[(int)seat];
    public override GameKind Kind => GameKind.HighRoll;
    public override int? ExpectedMax(Seat seat) => rolls[(int)seat] is null ? Max : null;
    public override string WhyNoRoll(Seat seat) => "you've already rolled this round";
    public override bool IsTurnOf(Seat seat) => rolls[(int)seat] is null;

    public override string Status()
    {
        var waiting = new[] { Seat.Inviter, Seat.Guest }.Where(s => rolls[(int)s] is null).Select(Name).ToList();
        return waiting.Count == 2 ? $"Both roll up to {Max}." : $"Waiting for {waiting[0]}'s roll.";
    }

    public override GameOutcome Roll(Seat seat, int value)
    {
        rolls[(int)seat] = value;
        if (rolls[0] is not { } a || rolls[1] is not { } b)
            return GameOutcome.Ok($"{Name(seat)} rolled {value}.");
        rolls[0] = rolls[1] = null;
        if (a == b)
            return GameOutcome.Ok($"{Name(seat)} rolled {value} - a tie, both roll again.");
        var loser = a < b ? Seat.Inviter : Seat.Guest;
        return GameOutcome.Lost(loser, $"{Name(seat)} rolled {value}. {Name(loser)} rolled lower.");
    }
}

public sealed class HigherLowerRules : GameRules
{
    public const int Max = 100;

    private enum Phase { Start, Guess, GuessRoll }

    private Phase phase = Phase.Start;
    private Seat roller = Seat.Inviter;
    private bool higher;

    public int? Number { get; private set; }
    public int Streak { get; private set; }
    public Seat Guesser { get; private set; } = Seat.Guest;

    public HigherLowerRules(Func<Seat, string> name) : base(name) { }

    public override GameKind Kind => GameKind.HigherLower;

    public override int? ExpectedMax(Seat seat) => phase switch
    {
        Phase.Start when seat == roller => Max,
        Phase.GuessRoll when seat == Guesser => Max,
        _ => null,
    };

    public override string WhyNoRoll(Seat seat) =>
        phase == Phase.Guess && seat == Guesser ? "call higher or lower first" : base.WhyNoRoll(seat);

    public override bool IsTurnOf(Seat seat) => phase == Phase.Start ? seat == roller : seat == Guesser;

    public override string Status() => phase switch
    {
        Phase.Start => $"{Name(roller)} rolls the starting number (up to {Max}).",
        Phase.Guess => $"{Number} to beat. {Name(Guesser)} calls higher or lower.",
        _ => $"{Number} to beat. {Name(Guesser)} called {(higher ? "higher" : "lower")} and rolls.",
    };

    public override bool CanCall(Seat seat) => phase == Phase.Guess && seat == Guesser;

    public override GameOutcome Call(Seat seat, bool callHigher)
    {
        if (!CanCall(seat))
            return GameOutcome.Refused("no call is due");
        higher = callHigher;
        phase = Phase.GuessRoll;
        return GameOutcome.Ok($"{Name(seat)} called {(callHigher ? "higher" : "lower")} than {Number}.");
    }

    public override GameOutcome Roll(Seat seat, int value)
    {
        if (phase == Phase.Start)
        {
            Number = value;
            Guesser = seat.Other();
            phase = Phase.Guess;
            return GameOutcome.Ok($"{Name(seat)} rolled {value} to start.");
        }
        if (value == Number)
            return GameOutcome.Ok($"{Name(seat)} rolled {value} - the same number, roll again.");
        var right = higher ? value > Number : value < Number;
        if (right)
        {
            Number = value;
            Streak++;
            Guesser = seat.Other();
            phase = Phase.Guess;
            return GameOutcome.Ok($"{Name(seat)} rolled {value} - right. Streak {Streak}.");
        }
        var streak = Streak;
        Streak = 0;
        Number = null;
        roller = seat;
        phase = Phase.Start;
        return GameOutcome.Lost(seat, $"{Name(seat)} rolled {value} - wrong{(streak > 0 ? $", ending a streak of {streak}" : "")}.");
    }
}

public sealed class TwentyOneRules : GameRules
{
    public const int Max = 10;
    public const int Limit = 21;
    private readonly int[] totals = new int[2];
    private readonly bool[] stood = new bool[2];
    private Seat starter = Seat.Inviter;

    public Seat Playing { get; private set; } = Seat.Inviter;

    public TwentyOneRules(Func<Seat, string> name) : base(name) { }

    public int Total(Seat seat) => totals[(int)seat];
    public bool HasStood(Seat seat) => stood[(int)seat];
    public override GameKind Kind => GameKind.TwentyOne;
    public override int? ExpectedMax(Seat seat) => seat == Playing ? Max : null;
    public override bool IsTurnOf(Seat seat) => seat == Playing;
    public override string Status() => $"{Name(Playing)} is at {Total(Playing)}: hit (roll up to {Max}) or stand.";
    public override bool CanStand(Seat seat) => seat == Playing && totals[(int)seat] > 0;

    public override GameOutcome Roll(Seat seat, int value)
    {
        totals[(int)seat] += value;
        var total = totals[(int)seat];
        if (total <= Limit)
            return GameOutcome.Ok($"{Name(seat)} drew {value}, {total} in all.");
        NewRound(seat);
        return GameOutcome.Lost(seat, $"{Name(seat)} drew {value} and went over with {total}.");
    }

    public override GameOutcome Stand(Seat seat)
    {
        if (!CanStand(seat))
            return GameOutcome.Refused("no stand is due");
        stood[(int)seat] = true;
        var mine = totals[(int)seat];
        if (!stood[(int)seat.Other()])
        {
            Playing = seat.Other();
            return GameOutcome.Ok($"{Name(seat)} stands at {mine}.");
        }
        var theirs = totals[(int)seat.Other()];
        if (mine == theirs)
        {
            NewRound(starter.Other());
            return GameOutcome.Ok($"{Name(seat)} stands at {mine} - a tie, nobody loses.");
        }
        var loser = mine < theirs ? seat : seat.Other();
        NewRound(loser);
        return GameOutcome.Lost(loser, $"{Name(seat)} stands at {mine} against {theirs}.");
    }

    private void NewRound(Seat first)
    {
        totals[0] = totals[1] = 0;
        stood[0] = stood[1] = false;
        starter = Playing = first;
    }
}

public sealed class TruthOrDareRules : GameRules
{
    public const int Max = GameWords.MaxRoll;

    private enum Phase { Roll, Pick, Prompt, Answer }

    private Phase phase = Phase.Roll;
    private readonly int?[] rolls = new int?[2];

    public Seat Answerer { get; private set; } = Seat.Inviter;
    public bool Dare { get; private set; }
    public string? PromptText { get; private set; }

    public TruthOrDareRules(Func<Seat, string> name) : base(name) { }

    public override GameKind Kind => GameKind.TruthOrDare;
    public override int? ExpectedMax(Seat seat) => phase == Phase.Roll && rolls[(int)seat] is null ? Max : null;
    public override string WhyNoRoll(Seat seat) => phase == Phase.Roll ? "you've already rolled this round" : "the next roll comes after this truth or dare";

    public override bool IsTurnOf(Seat seat) => phase switch
    {
        Phase.Roll => rolls[(int)seat] is null,
        Phase.Prompt => seat != Answerer,
        _ => seat == Answerer,
    };

    public override GameOutcome Roll(Seat seat, int value)
    {
        rolls[(int)seat] = value;
        if (rolls[0] is not { } a || rolls[1] is not { } b)
            return GameOutcome.Ok($"{Name(seat)} rolled {value}.");
        rolls[0] = rolls[1] = null;
        if (a == b)
            return GameOutcome.Ok($"{Name(seat)} rolled {value} - a tie, both roll again.");
        Answerer = a < b ? Seat.Inviter : Seat.Guest;
        phase = Phase.Pick;
        return GameOutcome.Ok($"{Name(seat)} rolled {value}. {Name(Answerer)} rolled lower and answers.");
    }

    public override string Status() => phase switch
    {
        Phase.Roll => rolls[0] is null && rolls[1] is null
            ? $"Both roll up to {Max}. The lower roll answers."
            : $"Waiting for {Name(rolls[0] is null ? Seat.Inviter : Seat.Guest)}'s roll.",
        Phase.Pick => $"{Name(Answerer)} picks truth or dare.",
        Phase.Prompt => $"{Name(Answerer)} picked {(Dare ? "dare" : "truth")}. {Name(Answerer.Other())} sends one.",
        _ => $"{Name(Answerer)}'s {(Dare ? "dare" : "truth")}: {PromptText}",
    };

    public override bool CanPick(Seat seat) => phase == Phase.Pick && seat == Answerer;
    public override bool CanPrompt(Seat seat) => phase == Phase.Prompt && seat == Answerer.Other();
    public override bool CanAnswer(Seat seat) => phase == Phase.Answer && seat == Answerer;

    public override GameOutcome Pick(Seat seat, bool dare)
    {
        if (!CanPick(seat))
            return GameOutcome.Refused("no pick is due");
        Dare = dare;
        phase = Phase.Prompt;
        return GameOutcome.Ok($"{Name(seat)} picked {(dare ? "dare" : "truth")}.");
    }

    public override GameOutcome Prompt(Seat seat, string text)
    {
        if (!CanPrompt(seat))
            return GameOutcome.Refused("no prompt is due");
        PromptText = text;
        phase = Phase.Answer;
        return GameOutcome.Ok($"{Name(seat)}: {text}");
    }

    public override GameOutcome Answer(Seat seat, bool done)
    {
        if (!CanAnswer(seat))
            return GameOutcome.Refused("no answer is due");
        var kind = Dare ? "dare" : "truth";
        phase = Phase.Roll;
        PromptText = null;
        return done
            ? GameOutcome.Ok($"{Name(seat)} did the {kind}.")
            : GameOutcome.Lost(seat, $"{Name(seat)} refused the {kind}.");
    }
}
