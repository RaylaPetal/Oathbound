using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Games;

public sealed class GameInvite
{
    public string Id { get; init; } = "";
    public Guid PairingId { get; init; }
    public string PeerName { get; init; } = "";
    public string PeerWorld { get; init; } = "";
    public bool Outgoing { get; init; }
    public GameKind Kind { get; init; }
    public RollChannel Channel { get; init; }
    public LossRule Loss { get; init; }
    public PromptRating Rating { get; init; }
    public int DeathrollStart { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
}

public sealed record GameLogEntry(DateTime AtUtc, string Text);

public sealed class GameSession
{
    public required string Id { get; init; }
    public required Guid PairingId { get; init; }
    public required string PeerName { get; init; }
    public required string PeerWorld { get; init; }
    public required string MyName { get; init; }
    public required Seat Me { get; init; }
    public required GameKind Kind { get; init; }
    public required RollChannel Channel { get; init; }
    public required LossRule Loss { get; init; }
    public required PromptRating Rating { get; init; }
    public GameRules Rules { get; set; } = null!;

    public Seat Peer => Me.Other();
    public int[] Losses { get; } = new int[2];
    public List<string>[] Stripped { get; } = [new(), new()];
    public List<GameLogEntry> Log { get; } = new();
    /// The other player lost and this player picks their forfeit.
    public int ForfeitsToGive { get; set; }
    /// This player lost and waits for a forfeit.
    public int ForfeitsOwed { get; set; }
    public Seat? LastLoser { get; set; }
    public bool OutOfStep { get; set; }
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

    internal (int Value, int Max, DateTime AtUtc)? PendingOwnRoll;
    internal readonly List<(int Value, int Max, DateTime AtUtc)> PeerRollsSeen = new();
    internal (int Value, int Max, DateTime AtUtc)? PeerNoticeWaiting;

    public string NameOf(Seat seat) => seat == Me ? MyName : FirstName(PeerName);

    public static string FirstName(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
}

/// Runs a game between the two people of a pairing. Each client keeps its own copy of the session and moves it on only
/// from roll lines it read itself and `collargame` tells from the other player, so neither trusts the other's word for
/// a roll. Losses act only on the local player.
public sealed class GamesService
{
    public const string Keyword = "collargame";

    private static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);
    /// How long the watcher waits to read a roll the roller says they made.
    private static readonly TimeSpan SeeRollWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepPeerRoll = TimeSpan.FromSeconds(30);
    /// The game drops chat lines sent faster than this.
    private static readonly TimeSpan SendSpacing = TimeSpan.FromSeconds(1.1);
    private const int MaxLog = 40;
    private const string SessionIdAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    private readonly PluginConfig config;
    private readonly ChatComposer composer;
    private readonly ChatSender sender;
    private readonly StripService strip;
    private readonly Queue<string> outbox = new();
    private DateTime lastSentUtc = DateTime.MinValue;

    public GamesService(PluginConfig config, ChatComposer composer, ChatSender sender, StripService strip)
    {
        this.config = config;
        this.composer = composer;
        this.sender = sender;
        this.strip = strip;
    }

    public GameInvite? Invite { get; private set; }
    public GameSession? Session { get; private set; }

    public bool CanStrip => strip.IsAvailable;

    // ---- Invites

    /// Null when sent, otherwise why it can't be.
    public string? SendInvite(GameKind kind, RollChannel channel, LossRule loss, PromptRating rating, int deathrollStart)
    {
        if (Session is not null)
            return "A game is already running.";
        if (Invite is not null)
            return "An invite is already waiting for an answer.";
        if (config.ActivePairing is not { PeerName: { Length: > 0 } name, PeerWorld: { Length: > 0 } world } pairing)
            return "Pick a pairing first.";
        if (GameWords.UsesDice(kind) && channel == RollChannel.Party)
        {
            if (!RollReader.PartyRollsVerified)
                return "Party rolls aren't available yet. Use nearby rolls.";
            if (!InPartyWith(name, world))
                return $"You and {name} need to be in a party together to roll in party chat.";
        }
        if (!GameWords.DeathrollStarts.Contains(deathrollStart))
            deathrollStart = GameWords.DefaultDeathrollStart;

        var invite = new GameInvite
        {
            Id = NewSessionId(), PairingId = pairing.Id, PeerName = name, PeerWorld = world, Outgoing = true,
            Kind = kind, Channel = channel, Loss = loss, Rating = rating, DeathrollStart = deathrollStart,
            ExpiresAtUtc = DateTime.UtcNow + InviteLifetime,
        };
        var body = $"invite {invite.Id} {GameWords.Kinds[kind]} {GameWords.Channels[channel]} {GameWords.Losses[loss]} {GameWords.Ratings[rating]}";
        if (kind == GameKind.Deathroll)
            body += " " + deathrollStart.ToString(CultureInfo.InvariantCulture);
        Invite = invite;
        Send(name, world, body);
        return null;
    }

    public void WithdrawInvite()
    {
        if (Invite is not { Outgoing: true } invite)
            return;
        Send(invite.PeerName, invite.PeerWorld, $"withdraw {invite.Id}");
        Invite = null;
    }

    public void AcceptInvite()
    {
        if (Invite is not { Outgoing: false } invite || Session is not null)
            return;
        Invite = null;
        Send(invite.PeerName, invite.PeerWorld, $"accept {invite.Id}");
        Start(invite, Seat.Guest);
    }

    public void DeclineInvite()
    {
        if (Invite is not { Outgoing: false } invite)
            return;
        Invite = null;
        Send(invite.PeerName, invite.PeerWorld, $"decline {invite.Id}");
    }

    private void Start(GameInvite invite, Seat me)
    {
        var myName = Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "You";
        var session = new GameSession
        {
            Id = invite.Id, PairingId = invite.PairingId, PeerName = invite.PeerName, PeerWorld = invite.PeerWorld,
            MyName = GameSession.FirstName(myName), Me = me, Kind = invite.Kind, Channel = invite.Channel, Loss = invite.Loss,
            Rating = invite.Rating,
        };
        session.Rules = GameRules.Create(invite.Kind, session.NameOf, invite.DeathrollStart);
        Session = session;
        AddLog(session, $"{GameWords.Name(invite.Kind)} started. Loss: {GameWords.Name(invite.Loss).ToLowerInvariant()}. {session.NameOf(Seat.Inviter)} goes first.");
        OathboundChat.Print($"{GameWords.Name(invite.Kind)} with {invite.PeerName} started.");
    }

    // ---- This player's moves

    /// The roll command for this player's turn, or null when it isn't theirs to roll.
    public string? RollCommand()
    {
        if (Session is not { } s || !GameWords.UsesDice(s.Kind) || s.PendingOwnRoll is not null || s.Rules.ExpectedMax(s.Me) is not { } max)
            return null;
        var number = max.ToString(CultureInfo.InvariantCulture);
        return s.Channel == RollChannel.Party ? $"/dice party {number}" : $"/random {number}";
    }

    public void Roll()
    {
        if (RollCommand() is { } command)
            outbox.Enqueue(command);
    }

    public void Call(bool higher) => Move(s => s.Rules.Call(s.Me, higher), id => $"call {id} {(higher ? "higher" : "lower")}");
    public void Stand() => Move(s => s.Rules.Stand(s.Me), id => $"stand {id}");
    public void Pick(bool dare) => Move(s => s.Rules.Pick(s.Me, dare), id => $"pick {id} {(dare ? "dare" : "truth")}");
    public void Answer(bool done) => Move(s => s.Rules.Answer(s.Me, done), id => $"{(done ? "done" : "refuse")} {id}");

    public void SendPrompt(string text)
    {
        var clean = GameDecks.Clean(text);
        if (clean.Length == 0)
            return;
        Move(s => s.Rules.Prompt(s.Me, clean), id => $"prompt {id} \"{clean}\"");
    }

    /// `body` gets the session id. Applied locally only once it's queued, so both sides apply it.
    private void Move(Func<GameSession, GameOutcome> action, Func<string, string> body)
    {
        if (Session is not { } s)
            return;
        var outcome = action(s);
        if (!outcome.Accepted)
            return;
        Send(s.PeerName, s.PeerWorld, body(s.Id));
        Apply(s, outcome);
    }

    public void SendForfeit(string text)
    {
        if (Session is not { ForfeitsToGive: > 0 } s)
            return;
        var clean = GameDecks.Clean(text);
        if (clean.Length == 0)
            return;
        s.ForfeitsToGive--;
        Send(s.PeerName, s.PeerWorld, $"forfeit {s.Id} \"{clean}\"");
        AddLog(s, $"Forfeit for {s.NameOf(s.Peer)}: {clean}");
    }

    public GameCard? DrawForfeit() =>
        Session is { } s ? GameDecks.Draw(config.Games, GameCardKind.Forfeit, PeerIsOwner(s), s.Rating) : null;

    /// A truth or dare for the other player, who is answering.
    public GameCard? DrawPrompt() =>
        Session is { Rules: TruthOrDareRules tod } s ? GameDecks.Draw(config.Games, tod.Dare ? GameCardKind.Dare : GameCardKind.Truth, PeerIsOwner(s), s.Rating) : null;

    /// Whether the other player is the Owner of this pairing.
    private bool PeerIsOwner(GameSession s) => config.FindPairingById(s.PairingId) is { Direction: PairingDirection.SubSide };

    public bool IsOwnerOfPeer(GameSession s) => config.FindPairingById(s.PairingId) is { Direction: PairingDirection.OwnerSide };

    public void Quit() => End("quit", "You quit the game.", notifyPeer: true);

    /// Panic always ends the game; the panic revert itself puts the pieces back.
    public void EndForPanic()
    {
        Invite = null;
        End("panic", "The game ended: panic.", notifyPeer: true);
    }

    public void OnLogout()
    {
        Invite = null;
        if (Session is not { } s)
            return;
        // Sent now: nothing drains the queue once logged out.
        sender.Send(composer.ComposeGame(s.PeerName, s.PeerWorld, $"quit {s.Id} logout"));
        End("logout", "The game ended: you logged out.", notifyPeer: false);
    }

    public void OnLogin()
    {
        if (Session is null)
            strip.RestoreStale();
    }

    // ---- Chat input

    /// A `collargame` tell. True when it was one, handled or not, so it's hidden like the other notices.
    public bool HandleTell(string? name, string? world, string text)
    {
        if (!text.StartsWith(Keyword + " ", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name is null || world is null || config.FindPairing(name, world) is not { IsPaired: true } pairing)
            return true;
        var (verb, rest) = Split(text[Keyword.Length..]);
        var (id, args) = Split(rest);
        if (id.Length == 0)
            return true;

        try
        {
            switch (verb.ToLowerInvariant())
            {
                case "invite":
                    OnInvite(pairing, name, world, id, args);
                    break;
                case "accept" when Invite is { Outgoing: true } mine && mine.Id == id && SamePeer(mine.PeerName, mine.PeerWorld, name, world) && Session is null:
                    Invite = null;
                    Start(mine, Seat.Inviter);
                    break;
                case "decline" when Invite is { Outgoing: true } mine && mine.Id == id && SamePeer(mine.PeerName, mine.PeerWorld, name, world):
                    Invite = null;
                    OathboundChat.Print($"{name} declined the game.");
                    break;
                case "withdraw" when Invite is { Outgoing: false } theirs && theirs.Id == id && SamePeer(theirs.PeerName, theirs.PeerWorld, name, world):
                    Invite = null;
                    break;
                default:
                    if (Session is { } s && s.Id == id && SamePeer(s.PeerName, s.PeerWorld, name, world))
                        OnSessionTell(s, verb.ToLowerInvariant(), args);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Game message \"{verb}\" failed.");
        }
        return true;
    }

    private void OnInvite(PairingState pairing, string name, string world, string id, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4
            || !GameWords.TryParse(GameWords.Kinds, parts[0], out GameKind kind)
            || !GameWords.TryParse(GameWords.Channels, parts[1], out RollChannel channel)
            || !GameWords.TryParse(GameWords.Losses, parts[2], out LossRule loss)
            || !GameWords.TryParse(GameWords.Ratings, parts[3], out PromptRating rating))
            return;
        var start = parts.Length > 4 && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && GameWords.DeathrollStarts.Contains(n)
            ? n
            : GameWords.DefaultDeathrollStart;
        if (Session is not null || Invite is not null)
        {
            Plugin.Log.Information($"Game invite from {name} ignored: a game or invite is already open.");
            return;
        }
        Invite = new GameInvite
        {
            Id = id, PairingId = pairing.Id, PeerName = name, PeerWorld = world, Outgoing = false,
            Kind = kind, Channel = channel, Loss = loss, Rating = rating, DeathrollStart = start,
            ExpiresAtUtc = DateTime.UtcNow + InviteLifetime,
        };
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Game invite",
            Content = $"{name} invites you to {GameWords.Name(kind)} ({GameWords.Name(loss).ToLowerInvariant()}). Answer in Games.",
            Type = NotificationType.Info,
        });
        OathboundChat.Print($"{name} invites you to {GameWords.Name(kind)}. Open Games to accept or decline.");
    }

    private void OnSessionTell(GameSession s, string verb, string args)
    {
        s.LastActivityUtc = DateTime.UtcNow;
        var (word, _) = Split(args);
        switch (verb)
        {
            case "call":
                ApplyPeer(s, s.Rules.Call(s.Peer, word.Equals("higher", StringComparison.OrdinalIgnoreCase)), "a call");
                break;
            case "stand":
                ApplyPeer(s, s.Rules.Stand(s.Peer), "a stand");
                break;
            case "pick":
                ApplyPeer(s, s.Rules.Pick(s.Peer, word.Equals("dare", StringComparison.OrdinalIgnoreCase)), "a pick");
                break;
            case "prompt" when Quoted(args) is { } prompt:
                ApplyPeer(s, s.Rules.Prompt(s.Peer, prompt), "a prompt");
                if (s.Rules is TruthOrDareRules { PromptText: not null } tod && tod.Answerer == s.Me)
                    OathboundChat.Print($"{s.PeerName}'s {(tod.Dare ? "dare" : "truth")} for you: {prompt}");
                break;
            case "done":
            case "refuse":
                ApplyPeer(s, s.Rules.Answer(s.Peer, verb == "done"), "an answer");
                break;
            case "forfeit" when Quoted(args) is { } forfeit:
                s.ForfeitsOwed = Math.Max(0, s.ForfeitsOwed - 1);
                AddLog(s, $"Forfeit for {s.NameOf(s.Me)}: {forfeit}");
                OathboundChat.Print($"Your forfeit from {s.PeerName}: {forfeit}");
                Plugin.NotificationManager.AddNotification(new Notification { Title = "Forfeit", Content = forfeit, Type = NotificationType.Warning });
                break;
            case "rolled" when TryNumbers(args, out var value, out var max):
                OnPeerNotice(s, value, max);
                break;
            case "seen" when int.TryParse(word, out var seen) && s.PendingOwnRoll is { } pending && pending.Value == seen:
                s.PendingOwnRoll = null;
                CountRoll(s, s.Me, pending.Value, pending.Max);
                break;
            case "missed" when int.TryParse(word, out var missed) && s.PendingOwnRoll is { } pending && pending.Value == missed:
                s.PendingOwnRoll = null;
                AddLog(s, $"{s.NameOf(s.Peer)} didn't see your roll of {missed} - roll again.");
                break;
            case "stripped":
                OnPeerStripped(s, word);
                break;
            case "quit":
                End(word, $"{s.PeerName} ended the game ({ReasonText(word)}).", notifyPeer: false);
                break;
        }
    }

    /// Roll lines from chat, from anyone. Only the two players' rolls in this session's channel matter.
    public void OnRollLine(RollLine roll)
    {
        if (Session is not { } s || !GameWords.UsesDice(s.Kind) || roll.Channel != s.Channel)
            return;
        Seat seat;
        if (roll.Self)
            seat = s.Me;
        else if (roll.Name is not null && SamePeer(s.PeerName, s.PeerWorld, roll.Name, roll.World ?? s.PeerWorld))
            seat = s.Peer;
        else
            return;

        var who = s.NameOf(seat);
        if (s.Rules.ExpectedMax(seat) is not { } expected)
        {
            AddLog(s, $"{who}'s roll of {roll.Value} didn't count: {s.Rules.WhyNoRoll(seat)}.");
            return;
        }
        if (roll.Max != expected)
        {
            AddLog(s, $"{who}'s roll of {roll.Value} didn't count: it needed a maximum of {expected}.");
            return;
        }

        if (seat == s.Me)
        {
            if (s.PendingOwnRoll is not null)
            {
                AddLog(s, $"Your roll of {roll.Value} didn't count: {s.NameOf(s.Peer)} hasn't confirmed your last one yet.");
                return;
            }
            s.PendingOwnRoll = (roll.Value, roll.Max, DateTime.UtcNow);
            Send(s.PeerName, s.PeerWorld, string.Create(CultureInfo.InvariantCulture, $"rolled {s.Id} {roll.Value} {roll.Max}"));
            return;
        }

        if (s.PeerNoticeWaiting is { } notice && notice.Value == roll.Value && notice.Max == roll.Max)
        {
            s.PeerNoticeWaiting = null;
            ConfirmPeerRoll(s, roll.Value, roll.Max);
            return;
        }
        s.PeerRollsSeen.Add((roll.Value, roll.Max, DateTime.UtcNow));
    }

    private void OnPeerNotice(GameSession s, int value, int max)
    {
        var index = s.PeerRollsSeen.FindIndex(r => r.Value == value && r.Max == max);
        if (index >= 0)
        {
            s.PeerRollsSeen.RemoveAt(index);
            ConfirmPeerRoll(s, value, max);
            return;
        }
        s.PeerNoticeWaiting = (value, max, DateTime.UtcNow);
    }

    private void ConfirmPeerRoll(GameSession s, int value, int max)
    {
        Send(s.PeerName, s.PeerWorld, string.Create(CultureInfo.InvariantCulture, $"seen {s.Id} {value}"));
        CountRoll(s, s.Peer, value, max);
    }

    private void CountRoll(GameSession s, Seat seat, int value, int max)
    {
        s.LastActivityUtc = DateTime.UtcNow;
        if (s.Rules.ExpectedMax(seat) != max)
        {
            MarkOutOfStep(s, $"{s.NameOf(seat)}'s roll of {value} no longer fits the game here");
            return;
        }
        Apply(s, s.Rules.Roll(seat, value));
    }

    private void ApplyPeer(GameSession s, GameOutcome outcome, string what)
    {
        if (!outcome.Accepted)
        {
            MarkOutOfStep(s, $"{s.PeerName} sent {what}, but {outcome.Note}");
            return;
        }
        Apply(s, outcome);
    }

    private void Apply(GameSession s, GameOutcome outcome)
    {
        s.LastActivityUtc = DateTime.UtcNow;
        if (outcome.Note is { } note)
            AddLog(s, note);
        if (outcome.Loser is { } loser)
            Lose(s, loser);
    }

    private void Lose(GameSession s, Seat loser)
    {
        s.Losses[(int)loser]++;
        s.LastLoser = loser;
        AddLog(s, $"{s.NameOf(loser)} lost the round.");
        if (loser == s.Peer)
        {
            if (s.Loss != LossRule.Strip)
                s.ForfeitsToGive++;
            return;
        }

        OathboundChat.Print($"You lost a round of {GameWords.Name(s.Kind)}.");
        if (s.Loss != LossRule.Forfeit)
        {
            var slot = strip.StripNext(s.Id);
            Send(s.PeerName, s.PeerWorld, $"stripped {s.Id} {(slot is { } taken ? StripService.SlotName(taken).Replace(' ', '-') : "none")}");
            if (slot is { } piece)
            {
                s.Stripped[(int)s.Me].Add(StripService.SlotName(piece));
                AddLog(s, $"Your {StripService.SlotName(piece)} came off.");
            }
            else
            {
                AddLog(s, strip.IsAvailable ? "Nothing left to take off - a forfeit instead." : "Glamourer isn't available - a forfeit instead.");
                if (s.Loss == LossRule.Strip)
                    s.ForfeitsOwed++;
            }
        }
        if (s.Loss != LossRule.Strip)
            s.ForfeitsOwed++;
    }

    private void OnPeerStripped(GameSession s, string word)
    {
        if (word.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            AddLog(s, $"{s.NameOf(s.Peer)} has nothing left to take off.");
            if (s.Loss == LossRule.Strip)
                s.ForfeitsToGive++;
            return;
        }
        var piece = word.Replace('-', ' ');
        s.Stripped[(int)s.Peer].Add(piece);
        AddLog(s, $"{s.NameOf(s.Peer)}'s {piece} came off.");
    }

    private void MarkOutOfStep(GameSession s, string why)
    {
        s.OutOfStep = true;
        AddLog(s, $"Out of step: {why}. Quit to end the game and get everything back.");
    }

    // ---- Every frame

    public void OnFrameworkUpdate()
    {
        var now = DateTime.UtcNow;
        DrainOutbox(now);

        if (Invite is { } invite && (now >= invite.ExpiresAtUtc || config.FindPairingById(invite.PairingId) is not { IsPaired: true }))
        {
            Invite = null;
            if (now >= invite.ExpiresAtUtc)
                OathboundChat.Print(invite.Outgoing ? $"{invite.PeerName} didn't answer your game invite." : $"The game invite from {invite.PeerName} expired.");
        }

        if (Session is not { } s)
            return;
        if (config.FindPairingById(s.PairingId) is not { IsPaired: true })
        {
            End("unpair", "The game ended: the pairing ended.", notifyPeer: false);
            return;
        }
        if (now - s.LastActivityUtc >= IdleLimit)
        {
            End("idle", "The game ended after 15 minutes without a move.", notifyPeer: true);
            return;
        }
        if (s.PeerNoticeWaiting is { } notice && now - notice.AtUtc >= SeeRollWait)
        {
            s.PeerNoticeWaiting = null;
            Send(s.PeerName, s.PeerWorld, string.Create(CultureInfo.InvariantCulture, $"missed {s.Id} {notice.Value}"));
            AddLog(s, $"You didn't see {s.NameOf(s.Peer)}'s roll of {notice.Value} - they roll again.");
        }
        if (s.PendingOwnRoll is { } pending && now - pending.AtUtc >= AnswerWait)
        {
            s.PendingOwnRoll = null;
            AddLog(s, $"No answer from {s.NameOf(s.Peer)} about your roll of {pending.Value} - roll again.");
        }
        s.PeerRollsSeen.RemoveAll(r => now - r.AtUtc >= KeepPeerRoll);
    }

    private void End(string reason, string message, bool notifyPeer)
    {
        if (Session is not { } s)
            return;
        Session = null;
        strip.RestoreAll(s.Id);
        if (notifyPeer)
            Send(s.PeerName, s.PeerWorld, $"quit {s.Id} {reason}");
        OathboundChat.Print(message);
        Plugin.Log.Information($"Game {s.Id} ended: {reason}.");
    }

    private static string ReasonText(string reason) => reason switch
    {
        "quit" => "they quit",
        "idle" => "no move for 15 minutes",
        "panic" => "panic",
        "logout" => "they logged out",
        "unpair" => "the pairing ended",
        _ => reason,
    };

    // ---- Helpers

    private void Send(string name, string world, string body) => outbox.Enqueue(composer.ComposeGame(name, world, body));

    private void DrainOutbox(DateTime now)
    {
        if (outbox.Count == 0 || now - lastSentUtc < SendSpacing)
            return;
        sender.Send(outbox.Dequeue());
        lastSentUtc = now;
    }

    private static void AddLog(GameSession s, string text)
    {
        s.Log.Add(new GameLogEntry(DateTime.UtcNow, text));
        if (s.Log.Count > MaxLog)
            s.Log.RemoveAt(0);
    }

    private static bool InPartyWith(string name, string world) =>
        Plugin.PartyList.Any(m => string.Equals(m.Name.TextValue, name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.World.Value.Name.ExtractText(), world, StringComparison.OrdinalIgnoreCase));

    private static bool SamePeer(string name, string world, string otherName, string otherWorld) =>
        string.Equals(name, otherName, StringComparison.OrdinalIgnoreCase) && string.Equals(world, otherWorld, StringComparison.OrdinalIgnoreCase);

    private static (string First, string Remainder) Split(string text)
    {
        var trimmed = text.Trim();
        var space = trimmed.IndexOf(' ');
        return space < 0 ? (trimmed, "") : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }

    private static string? Quoted(string args)
    {
        var start = args.IndexOf('"');
        var end = args.LastIndexOf('"');
        if (start < 0 || end <= start)
            return null;
        var text = GameDecks.Clean(args[(start + 1)..end]);
        return text.Length > 0 ? text : null;
    }

    private static bool TryNumbers(string args, out int value, out int max)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        max = 0;
        value = 0;
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out max);
    }

    private static string NewSessionId() =>
        string.Create(6, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = SessionIdAlphabet[RandomNumberGenerator.GetInt32(SessionIdAlphabet.Length)];
        });
}
