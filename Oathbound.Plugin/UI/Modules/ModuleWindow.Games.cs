using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Glamourer.Api.Enums;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Games;

namespace Oathbound.Plugin.UI;

/// Games with the other person of the active pairing: the invite, the running game, and each player's own settings.
public sealed partial class ModuleWindow
{
    private static readonly GameKind[] GameKinds = [GameKind.Deathroll, GameKind.HighRoll, GameKind.HigherLower, GameKind.TwentyOne, GameKind.TruthOrDare];
    private static readonly LossRule[] LossRules = [LossRule.Strip, LossRule.Forfeit, LossRule.Both];
    private static readonly PromptRating[] PromptRatings = [PromptRating.Mild, PromptRating.Spicy, PromptRating.Explicit];
    private static readonly ApiEquipSlot[] StrippableSlots =
    [
        ApiEquipSlot.Head, ApiEquipSlot.Body, ApiEquipSlot.Hands, ApiEquipSlot.Legs, ApiEquipSlot.Feet,
        ApiEquipSlot.Ears, ApiEquipSlot.Neck, ApiEquipSlot.Wrists, ApiEquipSlot.RFinger, ApiEquipSlot.LFinger,
    ];

    private GameKind newGameKind;
    private string? gameInviteError;
    private string gamePromptText = "";
    private string gameForfeitText = "";
    private GameCardKind newCardKind;
    private GameCardFor newCardFor;
    private PromptRating newCardRating;
    private string newCardText = "";

    private void DrawGamesModule()
    {
        IconGlyph.Text(FontAwesomeIcon.Dice, "Games");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Play with the other person of your active pairing. Either of you can lose, and a loss only ever changes the loser's own character. Panic ends the game and gives everything back.");

        var games = plugin.Games;
        if (games.Session is { } session)
        {
            using (Section.Begin("gamesSession", $"{GameWords.Name(session.Kind)} with {session.PeerName}"))
                DrawGameSession(games, session);
        }
        else
        {
            using (Section.Begin("gamesInvite", "Play a game"))
                DrawGameInvite(games);
        }

        using (Section.Begin("gamesStrip", "Your strip order"))
            DrawStripOrder();
        using (Section.Begin("gamesDecks", "Your cards"))
            DrawGameCards();
    }

    private void DrawGameInvite(GamesService games)
    {
        var config = plugin.Configuration.Games;
        if (games.Invite is { Outgoing: false } incoming)
        {
            IconGlyph.WrappedColored(Theme.Warning, $"{incoming.PeerName} invites you to {GameWords.Name(incoming.Kind)}.");
            IconGlyph.WrappedDisabled(DescribeTerms(incoming.Kind, incoming.Channel, incoming.Loss, incoming.Rating, incoming.DeathrollStart));
            IconGlyph.WrappedDisabled("Accepting agrees to this loss rule for this game only. A strip loss takes a piece off your own character, and it all comes back when the game ends or you panic.");
            if (incoming.Loss != LossRule.Forfeit && !games.CanStrip)
                IconGlyph.WrappedColored(Theme.Warning, "Glamourer isn't available, so your strip losses become forfeits.");
            if (ImGui.Button("Accept"))
                games.AcceptInvite();
            ImGui.SameLine();
            if (ImGui.Button("Decline"))
                games.DeclineInvite();
            ImGui.SameLine();
            IconGlyph.WrappedDisabled($"Expires in {Math.Max(0, (int)(incoming.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds)}s.");
            return;
        }
        if (games.Invite is { Outgoing: true } outgoing)
        {
            IconGlyph.WrappedDisabled($"Waiting for {outgoing.PeerName} to answer your {GameWords.Name(outgoing.Kind)} invite ({Math.Max(0, (int)(outgoing.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds)}s).");
            if (ImGui.Button("Withdraw"))
                games.WithdrawInvite();
            return;
        }

        if (plugin.Configuration.ActivePairing is not { } pairing)
        {
            IconGlyph.WrappedColored(Theme.Warning, "Pick a pairing in the header first.");
            return;
        }

        var kind = (int)newGameKind;
        Layout.ItemWidth(180);
        if (ImGui.Combo("Game##gameKind", ref kind, GameKinds.Select(GameWords.Name).ToArray(), GameKinds.Length))
            newGameKind = GameKinds[kind];
        IconGlyph.WrappedDisabled(GameRulesText(newGameKind));

        var save = false;
        if (GameWords.UsesDice(newGameKind))
        {
            var channel = (int)config.LastChannel;
            Layout.ItemWidth(180);
            if (ImGui.Combo("Rolls##gameChannel", ref channel, ["Nearby (/random)", "Party (/dice party)"], 2))
            {
                config.LastChannel = (RollChannel)channel;
                save = true;
            }
            IconGlyph.HelpMarker(RollReader.PartyRollsVerified
                ? "Nearby rolls are seen by everyone around you. Party rolls stay in party chat, but you both need to be in one party."
                : "Nearby rolls are seen by everyone around you. Party rolls aren't available yet.");
        }
        if (newGameKind == GameKind.Deathroll)
        {
            var start = Array.IndexOf(GameWords.DeathrollStarts, config.LastDeathrollStart);
            if (start < 0)
                start = Array.IndexOf(GameWords.DeathrollStarts, GameWords.DefaultDeathrollStart);
            Layout.ItemWidth(180);
            if (ImGui.Combo("Start at##deathrollStart", ref start, GameWords.DeathrollStarts.Select(n => n.ToString()).ToArray(), GameWords.DeathrollStarts.Length))
            {
                config.LastDeathrollStart = GameWords.DeathrollStarts[start];
                save = true;
            }
        }
        var loss = (int)config.LastLossRule;
        Layout.ItemWidth(180);
        if (ImGui.Combo("Losing a round##gameLoss", ref loss, LossRules.Select(DescribeLoss).ToArray(), LossRules.Length))
        {
            config.LastLossRule = (LossRule)loss;
            save = true;
        }
        IconGlyph.HelpMarker("Strip takes one piece off the loser, in their own strip order. A forfeit is a roleplay card the winner picks. Out of clothes, a strip loss becomes a forfeit.");
        var rating = (int)config.LastRating;
        Layout.ItemWidth(180);
        if (ImGui.Combo("Cards up to##gameRating", ref rating, PromptRatings.Select(GameWords.Name).ToArray(), PromptRatings.Length))
        {
            config.LastRating = (PromptRating)rating;
            save = true;
        }
        IconGlyph.HelpMarker("The most explicit card either of you can draw in this game.");
        if (save)
            plugin.Configuration.Save();

        if (ImGui.Button($"Invite {pairing.PeerName}"))
            gameInviteError = games.SendInvite(newGameKind, GameWords.UsesDice(newGameKind) ? config.LastChannel : RollChannel.Nearby, config.LastLossRule, config.LastRating, config.LastDeathrollStart);
        if (gameInviteError is not null)
            IconGlyph.WrappedColored(Theme.Warning, gameInviteError);
    }

    private void DrawGameSession(GamesService games, GameSession s)
    {
        IconGlyph.WrappedDisabled(DescribeTerms(s.Kind, s.Channel, s.Loss, s.Rating, null));
        ImGui.TextUnformatted($"Rounds lost - {s.NameOf(s.Me)}: {s.Losses[(int)s.Me]}, {s.NameOf(s.Peer)}: {s.Losses[(int)s.Peer]}");
        if (s.Loss != LossRule.Forfeit)
        {
            IconGlyph.WrappedDisabled($"{s.NameOf(s.Me)} took off: {Pieces(s, s.Me)}. {s.NameOf(s.Peer)} took off: {Pieces(s, s.Peer)}.");
        }

        ImGui.Spacing();
        IconGlyph.WrappedColored(s.Rules.IsTurnOf(s.Me) ? Theme.AccentHover : Theme.TextMuted, s.Rules.Status());
        if (s.OutOfStep)
            IconGlyph.WrappedColored(Theme.Warning, $"Your game and {s.PeerName}'s have drifted apart. Quit to end it and get everything back.");

        DrawGameActions(games, s);
        DrawForfeits(games, s);
        DrawGameFollowUp(games, s);

        ImGui.Spacing();
        using (var log = ImRaii.Child("gameLog", new System.Numerics.Vector2(-1, Layout.Scaled(140)), true))
        {
            if (log)
            {
                foreach (var entry in s.Log)
                    IconGlyph.WrappedDisabled(entry.Text);
                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                    ImGui.SetScrollHereY(1f);
            }
        }
        if (ImGui.Button("Quit game"))
            games.Quit();
        IconGlyph.HelpMarker("Ends the game for both of you. Everything taken off comes back.");
    }

    private void DrawGameActions(GamesService games, GameSession s)
    {
        if (GameWords.UsesDice(s.Kind))
        {
            if (s.PendingOwnRoll is not null)
                IconGlyph.WrappedDisabled($"Waiting for {s.PeerName}'s client to confirm your roll...");
            var command = games.RollCommand();
            using (ImRaii.Disabled(command is null))
            {
                if (ImGui.Button(command is null ? "Roll" : $"Roll ({command})"))
                    games.Roll();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Sends the roll for your turn. Typing the same command yourself counts too.");
        }

        switch (s.Rules)
        {
            case HigherLowerRules hilo when hilo.CanCall(s.Me):
                ImGui.SameLine();
                if (ImGui.Button("Higher"))
                    games.Call(true);
                ImGui.SameLine();
                if (ImGui.Button("Lower"))
                    games.Call(false);
                break;
            case TwentyOneRules twentyOne:
                IconGlyph.WrappedDisabled($"{s.NameOf(s.Me)}: {twentyOne.Total(s.Me)}{(twentyOne.HasStood(s.Me) ? " (stood)" : "")}. {s.NameOf(s.Peer)}: {twentyOne.Total(s.Peer)}{(twentyOne.HasStood(s.Peer) ? " (stood)" : "")}.");
                using (ImRaii.Disabled(!twentyOne.CanStand(s.Me)))
                {
                    if (ImGui.Button("Stand"))
                        games.Stand();
                }
                break;
            case TruthOrDareRules tod:
                DrawTruthOrDare(games, s, tod);
                break;
        }
    }

    private void DrawTruthOrDare(GamesService games, GameSession s, TruthOrDareRules tod)
    {
        if (tod.CanPick(s.Me))
        {
            if (ImGui.Button("Truth"))
                games.Pick(false);
            ImGui.SameLine();
            if (ImGui.Button("Dare"))
                games.Pick(true);
        }
        else if (tod.CanPrompt(s.Me))
        {
            Layout.ItemWidth(320);
            ImGui.InputTextWithHint("##gamePrompt", tod.Dare ? "Write a dare" : "Write a question", ref gamePromptText, GameDecks.MaxTextLength);
            ImGui.SameLine();
            if (ImGui.Button("Draw##gamePromptDraw"))
                gamePromptText = games.DrawPrompt()?.Text ?? gamePromptText;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Fills in a random {(tod.Dare ? "dare" : "truth")} from your cards, up to this game's rating.");
            ImGui.SameLine();
            using (ImRaii.Disabled(GameDecks.Clean(gamePromptText).Length == 0))
            {
                if (ImGui.Button("Send##gamePromptSend"))
                {
                    games.SendPrompt(gamePromptText);
                    gamePromptText = "";
                }
            }
        }
        else if (tod.CanAnswer(s.Me))
        {
            if (ImGui.Button("Done"))
                games.Answer(true);
            ImGui.SameLine();
            if (ImGui.Button("Refuse"))
                games.Answer(false);
            IconGlyph.HelpMarker("Refusing loses the round.");
        }
    }

    private void DrawForfeits(GamesService games, GameSession s)
    {
        if (s.ForfeitsOwed > 0)
            IconGlyph.WrappedColored(Theme.Warning, $"{s.PeerName} is picking your forfeit.");
        if (s.ForfeitsToGive <= 0)
            return;
        ImGui.Spacing();
        IconGlyph.WrappedColored(Theme.AccentHover, $"Pick a forfeit for {s.PeerName}{(s.ForfeitsToGive > 1 ? $" ({s.ForfeitsToGive} owed)" : "")}.");
        Layout.ItemWidth(320);
        ImGui.InputTextWithHint("##gameForfeit", "Write a forfeit", ref gameForfeitText, GameDecks.MaxTextLength);
        ImGui.SameLine();
        if (ImGui.Button("Draw##gameForfeitDraw"))
            gameForfeitText = games.DrawForfeit()?.Text ?? gameForfeitText;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fills in a random forfeit from your cards, up to this game's rating.");
        ImGui.SameLine();
        using (ImRaii.Disabled(GameDecks.Clean(gameForfeitText).Length == 0))
        {
            if (ImGui.Button("Send##gameForfeitSend"))
            {
                games.SendForfeit(gameForfeitText);
                gameForfeitText = "";
            }
        }
    }

    /// The Owner's favorites after their Sub loses, applied like from any other module: only within permissions.
    private void DrawGameFollowUp(GamesService games, GameSession s)
    {
        if (s.LastLoser != s.Peer || !games.IsOwnerOfPeer(s))
            return;
        var favorites = QuickAccessMenu.CategorizedFavorites(plugin.Configuration.QuickCommands);
        if (favorites.Count == 0 || !ImGui.CollapsingHeader("Follow up with a command##gameFollowUp"))
            return;
        var canSend = plugin.Configuration.ActivePairing?.Id == s.PairingId;
        if (!canSend)
            IconGlyph.WrappedDisabled("Switch the header to this pairing to send.");
        foreach (var (label, commands) in favorites)
        {
            ImGui.TextDisabled(label);
            foreach (var cmd in commands)
            {
                var messages = plugin.ChatComposer.ComposeAll(OwnerMoodleOverride.ForFavoriteSend(plugin.Configuration, cmd));
                using (ImRaii.Disabled(!canSend || !Commands.ChatComposer.AllFit(messages)))
                {
                    if (ImGui.Button($"{cmd.Label}##gameFollow{label}{cmd.Command}"))
                        plugin.ChatSender.SendAll(messages);
                }
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }
    }

    private void DrawStripOrder()
    {
        var games = plugin.Configuration.Games;
        IconGlyph.WrappedDisabled("When you lose a strip round, the first piece here that you're wearing comes off. Pieces held by restraints, locks or the collar are skipped.");
        if (!plugin.Games.CanStrip)
            IconGlyph.WrappedColored(Theme.Warning, "Glamourer isn't available, so your strip losses become forfeits.");
        var changed = false;
        for (var i = 0; i < games.StripOrder.Count; i++)
        {
            var slot = games.StripOrder[i];
            using var id = ImRaii.PushId($"strip{i}");
            using (ImRaii.Disabled(i == 0))
            {
                if (ImGui.ArrowButton("##up", ImGuiDir.Up))
                {
                    (games.StripOrder[i - 1], games.StripOrder[i]) = (slot, games.StripOrder[i - 1]);
                    changed = true;
                }
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(i == games.StripOrder.Count - 1))
            {
                if (ImGui.ArrowButton("##down", ImGuiDir.Down))
                {
                    (games.StripOrder[i + 1], games.StripOrder[i]) = (slot, games.StripOrder[i + 1]);
                    changed = true;
                }
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}. {StripService.SlotName(slot)}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                games.StripOrder.RemoveAt(i);
                changed = true;
                break;
            }
        }
        var missing = StrippableSlots.Where(s => !games.StripOrder.Contains(s)).ToArray();
        if (missing.Length > 0)
        {
            Layout.ItemWidth(160);
            using var combo = ImRaii.Combo("##stripAdd", "Add a piece");
            if (combo)
            {
                foreach (var slot in missing)
                {
                    if (ImGui.Selectable(StripService.SlotName(slot)))
                    {
                        games.StripOrder.Add(slot);
                        changed = true;
                    }
                }
            }
        }
        if (changed)
            plugin.Configuration.Save();
    }

    private void DrawGameCards()
    {
        var games = plugin.Configuration.Games;
        IconGlyph.WrappedDisabled("Truths, dares and forfeits you can draw in a game. Only yours: a card's text is sent only when you draw and send it.");
        var changed = false;

        if (ImGui.CollapsingHeader($"Your own ({games.Cards.Count})##gameOwnCards"))
        {
            Layout.ItemWidth(100);
            var kind = (int)newCardKind;
            if (ImGui.Combo("##newCardKind", ref kind, ["Truth", "Dare", "Forfeit"], 3))
                newCardKind = (GameCardKind)kind;
            ImGui.SameLine();
            Layout.ItemWidth(100);
            var who = (int)newCardFor;
            if (ImGui.Combo("##newCardFor", ref who, ["For anyone", "For an Owner", "For a Sub"], 3))
                newCardFor = (GameCardFor)who;
            ImGui.SameLine();
            Layout.ItemWidth(100);
            var rating = (int)newCardRating;
            if (ImGui.Combo("##newCardRating", ref rating, PromptRatings.Select(GameWords.Name).ToArray(), PromptRatings.Length))
                newCardRating = (PromptRating)rating;
            Layout.ItemWidth(320);
            ImGui.InputTextWithHint("##newCardText", "Card text", ref newCardText, GameDecks.MaxTextLength);
            ImGui.SameLine();
            using (ImRaii.Disabled(GameDecks.Clean(newCardText).Length == 0))
            {
                if (ImGui.Button("Add##newCard"))
                {
                    games.Cards.Add(new GameCard { Kind = newCardKind, For = newCardFor, Rating = newCardRating, Text = GameDecks.Clean(newCardText) });
                    newCardText = "";
                    changed = true;
                }
            }

            foreach (var card in games.Cards.ToList())
            {
                using var id = ImRaii.PushId(card.Id);
                ImGui.TextDisabled($"{card.Kind}, {GameWords.Name(card.Rating).ToLowerInvariant()}{ForText(card.For)}");
                ImGui.SameLine();
                var text = card.Text;
                Layout.ItemWidth(300);
                if (ImGui.InputText("##cardText", ref text, GameDecks.MaxTextLength))
                {
                    card.Text = GameDecks.Clean(text);
                    changed = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Delete"))
                {
                    games.Cards.Remove(card);
                    changed = true;
                }
            }
        }

        if (ImGui.CollapsingHeader($"Starter cards ({GameDecks.Starter.Count})##gameStarterCards"))
        {
            IconGlyph.WrappedDisabled("Untick a card to never draw it.");
            foreach (var card in GameDecks.Starter)
            {
                var on = !games.DisabledStarterCards.Contains(card.Id);
                if (ImGui.Checkbox($"##{card.Id}", ref on))
                {
                    if (on)
                        games.DisabledStarterCards.Remove(card.Id);
                    else
                        games.DisabledStarterCards.Add(card.Id);
                    changed = true;
                }
                ImGui.SameLine();
                ImGui.TextDisabled($"{card.Kind}, {GameWords.Name(card.Rating).ToLowerInvariant()}{ForText(card.For)}:");
                ImGui.SameLine();
                ImGui.TextWrapped(card.Text);
            }
        }
        if (changed)
            plugin.Configuration.Save();
    }

    private static string ForText(GameCardFor who) => who switch
    {
        GameCardFor.Owner => ", for an Owner",
        GameCardFor.Sub => ", for a Sub",
        _ => "",
    };

    private static string Pieces(GameSession s, Seat seat) =>
        s.Stripped[(int)seat].Count == 0 ? "nothing yet" : string.Join(", ", s.Stripped[(int)seat]);

    private static string DescribeLoss(LossRule rule) => rule switch
    {
        LossRule.Strip => "Strip a piece",
        LossRule.Forfeit => "Take a forfeit",
        _ => "Strip a piece and take a forfeit",
    };

    private static string DescribeTerms(GameKind kind, RollChannel channel, LossRule loss, PromptRating rating, int? deathrollStart)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (GameWords.UsesDice(kind))
            parts.Add(channel == RollChannel.Party ? "rolls in party chat" : "rolls nearby");
        if (kind == GameKind.Deathroll && deathrollStart is { } start)
            parts.Add($"starting at {start}");
        parts.Add($"losing a round: {DescribeLoss(loss).ToLowerInvariant()}");
        parts.Add($"cards up to {GameWords.Name(rating).ToLowerInvariant()}");
        return string.Join(", ", parts) + ".";
    }

    private static string GameRulesText(GameKind kind) => kind switch
    {
        GameKind.Deathroll => "Take turns rolling, each up to the last roll. Whoever rolls 1 loses the round.",
        GameKind.HighRoll => "Both roll up to 999. The lower roll loses; a tie rolls again.",
        GameKind.HigherLower => "Call whether the next roll (up to 100) beats the number. Wrong loses; right passes the turn and builds the streak.",
        GameKind.TwentyOne => "Hit to add a roll of up to 10, or stand. Over 21 loses at once; otherwise the lower total loses.",
        _ => "Pick truth or dare; the other writes or draws one. Refusing loses the round.",
    };
}
