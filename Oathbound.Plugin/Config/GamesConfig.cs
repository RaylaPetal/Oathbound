using System;
using System.Collections.Generic;
using Glamourer.Api.Enums;
using Oathbound.Plugin.Games;

namespace Oathbound.Plugin.Config;

public enum GameCardKind
{
    Truth,
    Dare,
    Forfeit,
}

/// Who a card is written for. Truths and dares go to whoever answers, forfeits to whoever lost.
public enum GameCardFor
{
    Anyone,
    Owner,
    Sub,
}

[Serializable]
public sealed class GameCard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public GameCardKind Kind { get; set; }
    public GameCardFor For { get; set; }
    public PromptRating Rating { get; set; }
    public string Text { get; set; } = "";
}

/// Each player's own: never sent, except a card's text when it's drawn.
[Serializable]
public sealed class GamesConfig
{
    /// Taken off first to last. Slots not listed are never stripped. Replaced on load, or the default would be kept
    /// in front of the saved order.
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public List<ApiEquipSlot> StripOrder { get; set; } = [ApiEquipSlot.Head, ApiEquipSlot.Hands, ApiEquipSlot.Feet, ApiEquipSlot.Legs, ApiEquipSlot.Body];
    public List<GameCard> Cards { get; set; } = new();
    public HashSet<string> DisabledStarterCards { get; set; } = new();
    public RollChannel LastChannel { get; set; }
    public LossRule LastLossRule { get; set; }
    public PromptRating LastRating { get; set; } = PromptRating.Spicy;
    public int LastDeathrollStart { get; set; } = GameWords.DefaultDeathrollStart;
}
