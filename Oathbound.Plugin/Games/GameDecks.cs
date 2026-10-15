using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Games;

/// Built-in cards plus the player's own. Starter ids are stable so turning one off survives updates.
public static class GameDecks
{
    /// Leaves room in a tell for the name, world, keyword and session id.
    public const int MaxTextLength = 200;

    private static GameCard Card(string id, GameCardKind kind, GameCardFor who, PromptRating rating, string text) =>
        new() { Id = "starter-" + id, Kind = kind, For = who, Rating = rating, Text = text };

    public static readonly IReadOnlyList<GameCard> Starter =
    [
        Card("t01", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Mild, "What was the first thing you noticed about me?"),
        Card("t02", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Mild, "Which outfit of mine do you like best, and why?"),
        Card("t03", GameCardKind.Truth, GameCardFor.Sub, PromptRating.Mild, "What's one rule you secretly like being given?"),
        Card("t04", GameCardKind.Truth, GameCardFor.Owner, PromptRating.Mild, "What do I do that makes you proud of me?"),
        Card("t05", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Spicy, "What's a scene you've thought about but never asked for?"),
        Card("t06", GameCardKind.Truth, GameCardFor.Sub, PromptRating.Spicy, "Which restraint do you hope I pick next?"),
        Card("t07", GameCardKind.Truth, GameCardFor.Owner, PromptRating.Spicy, "What's your favourite way to see me squirm?"),
        Card("t08", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Spicy, "Where is the most public place you've wanted to be teased?"),
        Card("t09", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Explicit, "Describe, in detail, the last time you thought about me that way."),
        Card("t10", GameCardKind.Truth, GameCardFor.Sub, PromptRating.Explicit, "Tell me exactly what you'd let me do to you tonight."),
        Card("t11", GameCardKind.Truth, GameCardFor.Owner, PromptRating.Explicit, "Tell me exactly what you plan to do with me once this game ends."),

        Card("d01", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Mild, "Strike your most flattering pose and hold it for a full minute."),
        Card("d02", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Mild, "Pay me three compliments, each one more embarrassing than the last."),
        Card("d03", GameCardKind.Dare, GameCardFor.Sub, PromptRating.Mild, "Kneel at my feet until your next turn."),
        Card("d04", GameCardKind.Dare, GameCardFor.Owner, PromptRating.Mild, "Pat my head and tell me I'm good, and mean it."),
        Card("d05", GameCardKind.Dare, GameCardFor.Sub, PromptRating.Spicy, "Beg for mercy in /say, loud enough for everyone nearby."),
        Card("d06", GameCardKind.Dare, GameCardFor.Owner, PromptRating.Spicy, "Whisper in my ear what you'll do if I lose the next round."),
        Card("d07", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Spicy, "Dance for me, slowly, until I say stop."),
        Card("d08", GameCardKind.Dare, GameCardFor.Sub, PromptRating.Spicy, "Crawl to me and ask nicely to be allowed to keep playing."),
        Card("d09", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Explicit, "Describe out loud what you'd do to me right now if nobody was watching."),
        Card("d10", GameCardKind.Dare, GameCardFor.Sub, PromptRating.Explicit, "Present yourself however I tell you to, and stay that way until your next turn."),
        Card("d11", GameCardKind.Dare, GameCardFor.Owner, PromptRating.Explicit, "Take your time and show me exactly how you'd claim me."),

        Card("f01", GameCardKind.Forfeit, GameCardFor.Anyone, PromptRating.Mild, "Call me by a pet name of my choice until the game ends."),
        Card("f02", GameCardKind.Forfeit, GameCardFor.Anyone, PromptRating.Mild, "Bow deeply and admit defeat in /say."),
        Card("f03", GameCardKind.Forfeit, GameCardFor.Sub, PromptRating.Mild, "Thank me for the lesson, on your knees."),
        Card("f04", GameCardKind.Forfeit, GameCardFor.Owner, PromptRating.Mild, "Let me choose your next emote and do it without complaint."),
        Card("f05", GameCardKind.Forfeit, GameCardFor.Sub, PromptRating.Spicy, "Sit at my feet and stay quiet until you win a round."),
        Card("f06", GameCardKind.Forfeit, GameCardFor.Owner, PromptRating.Spicy, "Grant me one wish, within reason, after the game."),
        Card("f07", GameCardKind.Forfeit, GameCardFor.Anyone, PromptRating.Spicy, "Answer my next question with complete honesty, whatever it is."),
        Card("f08", GameCardKind.Forfeit, GameCardFor.Sub, PromptRating.Spicy, "Ask me, nicely, what your punishment should be."),
        Card("f09", GameCardKind.Forfeit, GameCardFor.Sub, PromptRating.Explicit, "Describe how you'll make this loss up to me, in detail."),
        Card("f10", GameCardKind.Forfeit, GameCardFor.Owner, PromptRating.Explicit, "Let me take control of the next scene, whatever I decide it is."),
        Card("f11", GameCardKind.Forfeit, GameCardFor.Anyone, PromptRating.Explicit, "Tell me your filthiest thought about me right now."),
    ];

    public static IEnumerable<GameCard> Enabled(GamesConfig config) =>
        Starter.Where(c => !config.DisabledStarterCards.Contains(c.Id)).Concat(config.Cards.Where(c => Clean(c.Text).Length > 0));

    /// A random enabled card of that kind, for that role, at or below the rating; null when there's none.
    public static GameCard? Draw(GamesConfig config, GameCardKind kind, bool forOwner, PromptRating maxRating)
    {
        var who = forOwner ? GameCardFor.Owner : GameCardFor.Sub;
        var pool = Enabled(config).Where(c => c.Kind == kind && c.Rating <= maxRating && (c.For == GameCardFor.Anyone || c.For == who)).ToList();
        return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
    }

    /// One line, no quotes (they delimit the text in a tell), at most MaxTextLength.
    public static string Clean(string text)
    {
        var flat = string.Join(' ', text.Replace('"', '\'').Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length <= MaxTextLength ? flat : flat[..MaxTextLength].TrimEnd();
    }

    [Conditional("DEBUG")]
    public static void Validate()
    {
        foreach (var card in Starter)
            if (card.Text.Length > MaxTextLength || card.Text.Contains('"'))
                Plugin.Log.Error($"Game deck: starter card {card.Id} is too long or has a quote.");
        foreach (var dup in Starter.GroupBy(c => c.Id).Where(g => g.Count() > 1))
            Plugin.Log.Error($"Game deck: starter id {dup.Key} is used twice.");
    }
}
