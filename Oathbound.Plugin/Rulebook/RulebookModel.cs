using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.Rulebook;

/// What the Owner writes and the Sub accepts. Travels encrypted as JSON (RulebookJson); also persisted in config.
/// Every rule carries a stable random id so the Sub's switches and oath state survive new versions.
[Serializable]
public sealed class RulebookDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int Version { get; set; }
    /// Raised by the Owner's "Start over". A counter rather than a flag, so a later send before the Sub accepts
    /// still carries the reset; the Sub wipes its side when it accepts a version with a higher count.
    public int ResetCount { get; set; }
    public List<Oath> Oaths { get; set; } = new();
    public List<DeckCard> Deck { get; set; } = new();
    public List<PlaceRule> Places { get; set; } = new();
    public List<PresenceRule> Presence { get; set; } = new();
    public List<LedgerThreshold> Thresholds { get; set; } = new();
    public DrawOnSettings DrawOn { get; set; } = new();

    public IEnumerable<(string Id, string Name)> AllRules()
    {
        foreach (var o in Oaths) yield return (o.Id, o.Name);
        foreach (var c in Deck) yield return (c.Id, c.Name);
        foreach (var p in Places) yield return (p.Id, p.Name);
        foreach (var p in Presence) yield return (p.Id, p.Name);
        foreach (var t in Thresholds) yield return (t.Id, t.Name);
        yield return (DrawOnSettings.RuleId, "Deck draws on events");
    }

    public RulebookDocument Clone() => RulebookJson.Deserialize(RulebookJson.Serialize(this))!;
}

public static class RuleIds
{
    /// 96 random bits; ids only need to be unique within one rulebook.
    public static string New() => RelayCrypto.Base64UrlEncode(RelayCrypto.RandomBytes(12));
}

/// Values are persisted as numbers in config, so new ones are only ever appended.
public enum OathCondition
{
    NoDeaths,
    NoWipes,
    DutyTimeLimit,
    StayInPlaces,
    AvoidPlaces,
    Curfew,
    GreetOwner,
    MessageOwner,
    CheckIn,
    SayGoodnight,
    AddressOwner,
    ForbiddenWord,
    QuietInPublic,
}

public static class OathConditions
{
    /// Only these can be scoped to "the next duty"; everything else lasts a set time.
    public static bool IsDuty(OathCondition c) => c is OathCondition.NoDeaths or OathCondition.NoWipes or OathCondition.DutyTimeLimit;

    /// Something to do a number of times every period, rather than something not to do.
    public static bool IsRitual(OathCondition c) => c is OathCondition.GreetOwner or OathCondition.MessageOwner or OathCondition.CheckIn;

    public static bool NeedsPhrase(OathCondition c) => c is OathCondition.AddressOwner or OathCondition.ForbiddenWord;

    public static bool TakesPhrase(OathCondition c) => NeedsPhrase(c) || c is OathCondition.MessageOwner or OathCondition.SayGoodnight;

    public static bool CountsTimes(OathCondition c) => c is OathCondition.GreetOwner or OathCondition.MessageOwner;
}

public enum OathScope
{
    NextDuty,
    ForATime,
}

[Serializable]
public sealed class Oath
{
    public string Id { get; set; } = RuleIds.New();
    public string Name { get; set; } = "";
    public OathCondition Condition { get; set; }
    public OathScope Scope { get; set; }
    /// ForATime scope, 10 minutes to 7 days.
    public int DurationMinutes { get; set; } = 60;
    /// DutyTimeLimit only.
    public int TimeLimitMinutes { get; set; } = 30;
    /// StayInPlaces / AvoidPlaces only.
    public List<PlaceRef> Places { get; set; } = new();
    /// Curfew only: local minutes after midnight. The window may wrap past midnight.
    public int CurfewStartMinutes { get; set; } = 23 * 60;
    public int CurfewEndMinutes { get; set; } = 7 * 60;
    /// GreetOwner: the emote to use on the Owner.
    public uint EmoteId { get; set; }
    /// GreetOwner: a modded animation from the Sub's own catalog instead of the emote. Only Perform counts it.
    public string AnimationId { get; set; } = "";
    public string AnimationLabel { get; set; } = "";
    /// GreetOwner: how long Perform holds the Sub in place; it counts once the hold runs its full time.
    public int HoldSeconds { get; set; } = 10;
    /// MessageOwner / SayGoodnight: optional words the tell must contain. AddressOwner / ForbiddenWord: required.
    public string Phrase { get; set; } = "";
    /// Rituals: how many times, every how many days.
    public int TimesPerPeriod { get; set; } = 1;
    public int PeriodDays { get; set; } = 1;
    public List<string> Kept { get; set; } = new();
    public List<string> Broken { get; set; } = new();
    public int KeptLedger { get; set; }
    public int BrokenLedger { get; set; }
    /// Rituals only: applied per counted action, and per required action still missing when a period closes.
    public int LedgerPerDone { get; set; }
    public int LedgerPerMissed { get; set; }
}

/// Good things and bad things never share a draw: a kept oath can't come up with a punishment.
public enum CardPile
{
    Punishment,
    Reward,
}

[Serializable]
public sealed class DeckCard
{
    public string Id { get; set; } = RuleIds.New();
    public string Name { get; set; } = "";
    public CardPile Pile { get; set; }
    public int Weight { get; set; } = 1;
    public bool Once { get; set; }
    public List<string> Consequence { get; set; } = new();
}

public enum PlaceKind
{
    Territory,
    MainCity,
    Residential,
    HouseInterior,
    Duty,
}

[Serializable]
public sealed class PlaceRef
{
    public PlaceKind Kind { get; set; }
    /// Territory kind only.
    public uint TerritoryId { get; set; }
    /// Display only; the id decides.
    public string Name { get; set; } = "";
}

[Serializable]
public sealed class PlaceRule
{
    public string Id { get; set; } = RuleIds.New();
    public string Name { get; set; } = "";
    public int CooldownSeconds { get; set; } = 60;
    public List<PlaceRef> Places { get; set; } = new();
    public List<string> Enter { get; set; } = new();
    public List<string> Leave { get; set; } = new();
}

[Serializable]
public sealed class PresenceRule
{
    public string Id { get; set; } = RuleIds.New();
    public string Name { get; set; } = "";
    public int CooldownSeconds { get; set; } = 60;
    public float RangeYalms { get; set; } = 10;
    public List<string> Arrive { get; set; } = new();
    public List<string> Depart { get; set; } = new();
    /// The one rule type allowed to leash or send chat, each still behind the Sub's own permission for it.
    public bool LeashOnArrive { get; set; }
    public int LeashLengthYalms { get; set; } = Commands.LengthOption.DefaultYalms;
    public bool UnleashOnDepart { get; set; }
    /// Sent as a /tell to the Owner, never anywhere else.
    public string ArriveTell { get; set; } = "";
    public string DepartTell { get; set; } = "";

    public bool DoesAnything => Arrive.Count + Depart.Count > 0 || LeashOnArrive || UnleashOnDepart ||
        !string.IsNullOrWhiteSpace(ArriveTell) || !string.IsNullOrWhiteSpace(DepartTell);
}

public enum ThresholdDirection
{
    AtOrAbove,
    AtOrBelow,
}

[Serializable]
public sealed class LedgerThreshold
{
    public string Id { get; set; } = RuleIds.New();
    public string Name { get; set; } = "";
    public int CooldownSeconds { get; set; } = 60;
    public int Score { get; set; }
    public ThresholdDirection Direction { get; set; }
    public List<string> Consequence { get; set; } = new();
    /// From the reward pile at or above, the punishment pile at or below.
    public bool DrawCard { get; set; }

    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public CardPile Pile => Direction == ThresholdDirection.AtOrAbove ? CardPile.Reward : CardPile.Punishment;
    public int? ResetTo { get; set; }
}

[Serializable]
public sealed class DrawOnSettings
{
    /// The Sub's switch and cooldown bookkeeping treat the draw-on events as one rule. Oath kept draws a reward;
    /// the others draw a punishment.
    public const string RuleId = "draw-on-events";

    public int CooldownSeconds { get; set; } = 60;
    public bool OathBroken { get; set; }
    public bool OathKept { get; set; }
    public bool Wipe { get; set; }
    public bool Death { get; set; }

    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public bool Any => OathBroken || OathKept || Wipe || Death;
}

public static class CardPiles
{
    /// The word after `deck draw`.
    public static string Word(CardPile pile) => pile == CardPile.Reward ? "reward" : "punishment";

    public static string DrawCommand(CardPile pile) => $"{ConsequenceValidator.DeckWord} draw {Word(pile)}";

    /// Parses the text after `deck`.
    public static bool TryParseDraw(string rest, out CardPile pile)
    {
        pile = CardPile.Punishment;
        var parts = rest.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].Equals("draw", StringComparison.OrdinalIgnoreCase))
            return false;
        if (parts[1].Equals("reward", StringComparison.OrdinalIgnoreCase))
        {
            pile = CardPile.Reward;
            return true;
        }
        return parts[1].Equals("punishment", StringComparison.OrdinalIgnoreCase);
    }
}

public static class RulebookLimits
{
    public const int MinCooldownSeconds = 60;
    public const int MaxDeckCards = 50;
    public const int MinCardWeight = 1;
    public const int MaxCardWeight = 100;
    public const int MaxThresholds = 10;
    public const int MinOathMinutes = 10;
    public const int MaxOathMinutes = 30 * 24 * 60;
    public const int MaxTimesPerPeriod = 10;
    public const int MaxPeriodDays = 7;
    public const int MaxPhraseLength = 40;
    public const int MaxPresenceTellLength = 200;
    /// SayGoodnight: how long before logging off the goodnight tell may be sent.
    public const int GoodnightWindowMinutes = 15;
    public const float MinRangeYalms = 3;
    public const float MaxRangeYalms = 50;
    public const int MinLedger = -999;
    public const int MaxLedger = 999;
    public const int MaxLedgerChange = 99;
    public const int MaxNameLength = 60;
    /// Room to spare under the relay's 64 KiB ciphertext cap even before gzip.
    public const int MaxPlaintextBytes = 256 * 1024;
}

public static class RulebookJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
        MaxDepth = 16,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static RulebookDocument? Deserialize(string json) => JsonSerializer.Deserialize<RulebookDocument>(json, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
