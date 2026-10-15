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
        Card("t05", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Spicy, "Where on your body do you most love to be kissed?"),
        Card("t06", GameCardKind.Truth, GameCardFor.Sub, PromptRating.Spicy, "What's the most embarrassing thing you'd let me make you do where others can see?"),
        Card("t07", GameCardKind.Truth, GameCardFor.Owner, PromptRating.Spicy, "What's the first thing you'd do to me if we were alone right now?"),
        Card("t08", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Spicy, "Describe the best make-out session you've ever had."),
        Card("t12", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Spicy, "Which outfit of mine makes it hardest to keep your hands off me?"),
        Card("t13", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Spicy, "What's the most teasing thing anyone has ever done to you?"),
        Card("t09", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Explicit, "What's your biggest kink, and when did you first realise it?"),
        Card("t10", GameCardKind.Truth, GameCardFor.Sub, PromptRating.Explicit, "Describe exactly how you want to be used tonight."),
        Card("t11", GameCardKind.Truth, GameCardFor.Owner, PromptRating.Explicit, "Describe, in detail, how you'd take me right now."),
        Card("t14", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Explicit, "What's the filthiest thing you've ever done in bed?"),
        Card("t15", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Explicit, "Giving or receiving oral - which do you crave more, and why?"),
        Card("t16", GameCardKind.Truth, GameCardFor.Anyone, PromptRating.Explicit, "What's one thing you've fantasised about doing to me that you've never said out loud?"),
        Card("t17", GameCardKind.Truth, GameCardFor.Sub, PromptRating.Explicit, "What's the hardest limit you'd let me push, and how far?"),

        Card("d01", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Mild, "Strike your most flattering pose and hold it for a full minute."),
        Card("d02", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Mild, "Pay me three compliments, each one more embarrassing than the last."),
        Card("d03", GameCardKind.Dare, GameCardFor.Sub, PromptRating.Mild, "Kneel at my feet until your next turn."),
        Card("d04", GameCardKind.Dare, GameCardFor.Owner, PromptRating.Mild, "Pat my head and tell me I'm good, and mean it."),
        Card("d05", GameCardKind.Dare, GameCardFor.Sub, PromptRating.Spicy, "Kiss your way up from my boots to my lips, slowly."),
        Card("d06", GameCardKind.Dare, GameCardFor.Owner, PromptRating.Spicy, "Pin me against the nearest wall and kiss me until I'm breathless."),
        Card("d07", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Spicy, "Give me a slow lap dance until I say stop."),
        Card("d08", GameCardKind.Dare, GameCardFor.Sub, PromptRating.Spicy, "Crawl to me and beg to be allowed to keep playing."),
        Card("d12", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Spicy, "Make out with me for a full minute, hands anywhere above the waist."),
        Card("d13", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Spicy, "Kiss and bite my neck until I make a sound."),
        Card("d14", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Spicy, "Take off one piece of my clothing - with your teeth."),
        Card("d09", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Explicit, "Get on your knees and go down on me until I tell you to stop."),
        Card("d10", GameCardKind.Dare, GameCardFor.Sub, PromptRating.Explicit, "Bend over however I tell you to and stay that way while I touch you."),
        Card("d11", GameCardKind.Dare, GameCardFor.Owner, PromptRating.Explicit, "Take me right here, however you want, and make it last."),
        Card("d15", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Explicit, "Give me a slow blowjob, eyes on me the whole time."),
        Card("d16", GameCardKind.Dare, GameCardFor.Anyone, PromptRating.Explicit, "Touch yourself for me while I watch, and don't stop until I say."),
        Card("d17", GameCardKind.Dare, GameCardFor.Sub, PromptRating.Explicit, "Ride me slowly, and ask permission before you finish."),
        Card("d18", GameCardKind.Dare, GameCardFor.Owner, PromptRating.Explicit, "Use your hands on me until I beg you to stop - then don't."),

        Card("f01", GameCardKind.Forfeit, GameCardFor.Anyone, PromptRating.Mild, "Call me by a pet name of my choice until the game ends."),
        Card("f02", GameCardKind.Forfeit, GameCardFor.Anyone, PromptRating.Mild, "Bow deeply and admit defeat in /say."),
        Card("f03", GameCardKind.Forfeit, GameCardFor.Sub, PromptRating.Mild, "Thank me for the lesson, on your knees."),
        Card("f04", GameCardKind.Forfeit, GameCardFor.Owner, PromptRating.Mild, "Let me choose your next emote and do it without complaint."),
        Card("f05", GameCardKind.Forfeit, GameCardFor.Sub, PromptRating.Spicy, "Sit in my lap and let me touch you wherever I like for a minute."),
        Card("f06", GameCardKind.Forfeit, GameCardFor.Owner, PromptRating.Spicy, "Let me take the lead and kiss you however I want."),
        Card("f07", GameCardKind.Forfeit, GameCardFor.Anyone, PromptRating.Spicy, "Leave a mark on my neck somewhere only we'll know about."),
        Card("f08", GameCardKind.Forfeit, GameCardFor.Sub, PromptRating.Spicy, "Ask me, nicely, what your punishment should be."),
        Card("f12", GameCardKind.Forfeit, GameCardFor.Anyone, PromptRating.Spicy, "Undress down to your underwear and stay that way until you win a round."),
        Card("f09", GameCardKind.Forfeit, GameCardFor.Sub, PromptRating.Explicit, "Get on your knees and pleasure me with your mouth until I'm satisfied."),
        Card("f10", GameCardKind.Forfeit, GameCardFor.Owner, PromptRating.Explicit, "Let me take control in bed tonight, whatever I decide."),
        Card("f11", GameCardKind.Forfeit, GameCardFor.Anyone, PromptRating.Explicit, "Tell me your filthiest thought about me, then act it out."),
        Card("f13", GameCardKind.Forfeit, GameCardFor.Anyone, PromptRating.Explicit, "Go down on me, and don't stop until I say so."),
        Card("f14", GameCardKind.Forfeit, GameCardFor.Sub, PromptRating.Explicit, "Edge yourself for me and don't finish until I give permission."),
        Card("f15", GameCardKind.Forfeit, GameCardFor.Owner, PromptRating.Explicit, "Lie back and let me use you however I please."),
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
