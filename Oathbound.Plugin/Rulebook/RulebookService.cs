using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.Rulebook;

/// Runs accepted rulebooks on the Sub's own client and drives the Owner's publish/report side. Everything that
/// fires goes through the ConsequenceQueue and then the normal command dispatch, so every permission still applies.
/// Framework thread only, except where noted.
public sealed class RulebookService : IRulebookCommandSink, IDisposable
{
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromDays(7);
    private const int MaxActivity = 100;
    private static readonly TimeSpan ArriveAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DepartAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(RelayProtocolConstants.RulebookReportMinUploadIntervalSeconds + 5);

    private readonly PluginConfig config;
    private readonly RulebookMailboxService mailbox;
    private readonly ChatCommandListener listener;
    private readonly ChatComposer composer;
    private readonly ChatSender sender;
    private readonly RulebookGameEvents events;
    private readonly Safety.EmoteWatcher emotes;
    private readonly Func<CancellationToken> backgroundToken;
    public ConsequenceQueue Queue { get; }

    private readonly Dictionary<(Guid, string), PresenceState> presence = new();
    private readonly HashSet<Guid> reportsInFlight = new();
    private readonly Dictionary<Guid, DateTime> reportRetryAfter = new();
    private readonly HashSet<Guid> wasRunning = new();
    private DateTime nextSecondTick = DateTime.MinValue;

    private sealed class PresenceState
    {
        public bool Arrived;
        public DateTime? InRangeSince;
        public DateTime? OutOfRangeSince;
    }

    public RulebookService(PluginConfig config, RulebookMailboxService mailbox, ChatCommandListener listener, ChatComposer composer, ChatSender sender, Safety.EmoteWatcher emotes, Func<CancellationToken> backgroundToken)
    {
        this.config = config;
        this.mailbox = mailbox;
        this.listener = listener;
        this.composer = composer;
        this.sender = sender;
        this.backgroundToken = backgroundToken;
        this.emotes = emotes;
        events = new RulebookGameEvents();
        Queue = new ConsequenceQueue(RunCommand, OnCommandResult, RulebookGameEvents.ShouldHoldConsequences);

        listener.RulebookSink = this;
        listener.RulebookNudgeReceived += OnNudge;
        mailbox.RulebookReceived += OnRulebookReceived;
        mailbox.RulebookRejected += OnRulebookRejected;
        mailbox.ReportReceived += OnReportReceived;
        events.DutyStarted += OnDutyStarted;
        events.DutyWiped += OnDutyWiped;
        events.DutyCompleted += OnDutyCompleted;
        events.DutyAbandoned += OnDutyAbandoned;
        events.LocalDeath += OnLocalDeath;
        events.TerritoryEntered += OnTerritoryEntered;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
        Plugin.ClientState.Logout += OnLogout;
        GestureCommand.EmotePlayed += PluginOutput.RecordEmote;
    }

    public void Dispose()
    {
        listener.RulebookSink = null;
        listener.RulebookNudgeReceived -= OnNudge;
        mailbox.RulebookReceived -= OnRulebookReceived;
        mailbox.RulebookRejected -= OnRulebookRejected;
        mailbox.ReportReceived -= OnReportReceived;
        events.Dispose();
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        Plugin.ClientState.Logout -= OnLogout;
        GestureCommand.EmotePlayed -= PluginOutput.RecordEmote;
    }

    // ---- Wiring ----

    public void OnPairStatus(PairingState pairing, PairEnvelope pair)
    {
        if (pair.RulebookMailbox is { } summary)
            Plugin.FireAndForget(mailbox.ApplyPairStatusAsync(pairing, summary, backgroundToken()));
    }

    public void CheckNow(PairingState pairing) => Plugin.FireAndForget(mailbox.CheckNowAsync(pairing, backgroundToken()));

    private void OnNudge(PairingState pairing) => CheckNow(pairing);

    public void OnFrameworkUpdate()
    {
        if (!Plugin.ClientState.IsLoggedIn)
            return;
        events.OnFrameworkUpdate();
        // An animation this plugin played (an Owner's command, a reaction, a rulebook consequence) isn't a greeting.
        if (emotes.OwnThisTick is { } ownEmote && !PluginOutput.EmoteJustPlayed)
            OnOwnEmote(ownEmote.EmoteId, ownEmote.TargetObjectId);
        Queue.Pump();

        var now = DateTime.UtcNow;
        if (now < nextSecondTick)
            return;
        nextSecondTick = now.AddSeconds(1);
        foreach (var pairing in SubPairings())
        {
            TrackRunningState(pairing);
            var state = pairing.Rulebook;
            if (state.Accepted is not null)
            {
                ExpireOffers(pairing);
                if (IsRunning(pairing))
                {
                    TickOaths(pairing);
                    TickPresence(pairing, now);
                }
            }
            // From the first version received, so the Owner also learns about a pending review, a decline,
            // the permission being off or a panic pause before anything was ever accepted.
            if (state.LastReceivedVersion > 0 || state.Accepted is not null)
                MaybeUploadReport(pairing, now);
        }
    }

    /// Panic: suspend, void open oaths, drop everything held. The ledger is left as is.
    public void OnPanic()
    {
        Queue.Clear();
        presence.Clear();
        if (!config.RulebookSuspended)
        {
            config.RulebookSuspended = true;
            foreach (var pairing in SubPairings().Where(p => p.Rulebook.Accepted is not null))
            {
                VoidOpenOaths(pairing, "panic");
                Log(pairing, RulebookEventKind.Suspended, null, "Rulebook suspended by panic.");
            }
            config.SaveNow();
        }
    }

    public void Resume()
    {
        if (!config.RulebookSuspended)
            return;
        config.RulebookSuspended = false;
        foreach (var pairing in SubPairings().Where(p => p.Rulebook.Accepted is not null))
            Log(pairing, RulebookEventKind.Resumed, null, "Rulebook resumed.");
        config.SaveNow();
    }

    // ---- State helpers ----

    private IEnumerable<PairingState> SubPairings() =>
        config.Pairings.Where(p => p.IsPaired && p.Direction == PairingDirection.SubSide);

    public bool IsRunning(PairingState pairing) =>
        pairing is { IsPaired: true, Direction: PairingDirection.SubSide }
        && pairing.Rulebook.Accepted is not null
        && config.Permissions.Rulebook && config.RulebookAcknowledged && !config.RulebookSuspended;

    private static bool RuleOn(PairingState pairing, string id) => !pairing.Rulebook.DisabledRuleIds.Contains(id);

    /// Turning the permission off voids open oaths and drops anything held, like panic minus the suspension.
    private void TrackRunningState(PairingState pairing)
    {
        var running = IsRunning(pairing);
        if (running)
        {
            wasRunning.Add(pairing.Id);
            return;
        }
        if (!wasRunning.Remove(pairing.Id))
            return;
        Queue.DropFor(pairing.Id);
        if (!config.Permissions.Rulebook)
            VoidOpenOaths(pairing, "the Rulebook permission was turned off");
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void Log(PairingState pairing, RulebookEventKind kind, string? ruleId, string text)
    {
        var activity = pairing.Rulebook.Activity;
        activity.Add(new RulebookEvent { At = Now(), Kind = kind, RuleId = ruleId, Text = text });
        if (activity.Count > MaxActivity)
            activity.RemoveRange(0, activity.Count - MaxActivity);
        config.Save();
    }

    private static void Notify(string title, string content, NotificationType type = NotificationType.Info) =>
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = title,
            Content = content,
            Type = type,
            InitialDuration = TimeSpan.FromSeconds(8),
        });

    // ---- Consequences ----

    private (bool, string) RunCommand(Guid pairingId, string command)
    {
        if (config.FindPairingById(pairingId) is not { IsPaired: true } pairing)
            return (false, "The pairing ended.");
        var result = listener.RunRulebookCommand(command, pairing);
        return (result.Success, result.Message);
    }

    private void OnCommandResult(Guid pairingId, ConsequenceQueue.Item item, string command, bool success, string message)
    {
        if (config.FindPairingById(pairingId) is not { IsPaired: true } pairing)
            return;
        if (success)
            Log(pairing, RulebookEventKind.ConsequenceRan, item.RuleId, $"{item.Label}: {ConsequenceText.Describe(command)}.");
        else
            Log(pairing, RulebookEventKind.ConsequenceSkipped, item.RuleId, $"{item.Label}: skipped \"{ConsequenceText.Describe(command)}\" - {message}");
    }

    /// `cooldownKey` null for a drawn card, which its draw already paid for.
    private void Fire(PairingState pairing, string ruleId, string label, IReadOnlyList<string> commands, string? cooldownKey, int cooldownSeconds)
    {
        if (commands.Count == 0 || !IsRunning(pairing))
            return;
        var outcome = Queue.Enqueue(new ConsequenceQueue.Item(pairing.Id, ruleId, label, commands.ToList()), cooldownKey, cooldownSeconds);
        if (outcome == ConsequenceQueue.Outcome.Capped)
            Log(pairing, RulebookEventKind.ConsequenceDropped, ruleId, $"{label}: dropped - more than {ConsequenceQueue.CapCount} consequences in 10 minutes.");
    }

    // ---- Rulebook versions (Sub) ----

    private void OnRulebookReceived(PairingState pairing, RulebookDocument doc)
    {
        Log(pairing, RulebookEventKind.VersionReceived, null, $"Version {doc.Version} received.");
        Notify("New rulebook", $"{pairing.PeerName} sent rulebook version {doc.Version}. Review it in Rulebook - nothing in it runs until you accept.");
    }

    private void OnRulebookRejected(PairingState pairing, int version, string reason)
    {
        pairing.Rulebook.LastDeclinedVersion = version;
        Log(pairing, RulebookEventKind.VersionDeclined, null, $"Version {version} was refused automatically: {reason}.");
        Notify("Rulebook refused", $"Rulebook version {version} from {pairing.PeerName} was refused: {reason}.", NotificationType.Warning);
    }

    public void AcceptPending(PairingState pairing)
    {
        var state = pairing.Rulebook;
        if (state.Pending is not { } doc)
            return;
        var previous = state.Accepted;
        state.Accepted = doc;
        state.Pending = null;
        state.AcceptedUnixSeconds = Now();
        state.RemovedOnceCards.Clear();
        var liveIds = doc.AllRules().Select(r => r.Id).ToHashSet();
        state.DisabledRuleIds.RemoveWhere(id => !liveIds.Contains(id));
        state.ThresholdsMet.RemoveWhere(id => doc.Thresholds.All(t => t.Id != id));
        ReconcileOaths(pairing, previous, doc);
        Log(pairing, RulebookEventKind.VersionAccepted, null, $"Version {doc.Version} accepted.");
        config.SaveNow();
    }

    public void DeclinePending(PairingState pairing)
    {
        var state = pairing.Rulebook;
        if (state.Pending is not { } doc)
            return;
        state.Pending = null;
        state.LastDeclinedVersion = doc.Version;
        Log(pairing, RulebookEventKind.VersionDeclined, null, $"Version {doc.Version} declined.");
        config.SaveNow();
    }

    public void SetRuleEnabled(PairingState pairing, string ruleId, bool enabled)
    {
        var state = pairing.Rulebook;
        var name = state.Accepted?.AllRules().FirstOrDefault(r => r.Id == ruleId).Name ?? "rule";
        if (enabled ? !state.DisabledRuleIds.Remove(ruleId) : !state.DisabledRuleIds.Add(ruleId))
            return;
        if (!enabled && state.Oaths.TryGetValue(ruleId, out var oath) && oath.Status == OathStatus.Open)
            ResolveOath(pairing, ruleId, oath, OathStatus.Voided, "its rule was switched off");
        Log(pairing, enabled ? RulebookEventKind.RuleSwitchedOn : RulebookEventKind.RuleSwitchedOff, ruleId,
            $"\"{name}\" switched {(enabled ? "on" : "off")}.");
        config.SaveNow();
    }

    // ---- Oaths ----

    /// New oaths are offered; changed or removed offers are withdrawn (a changed one is offered again); started oaths keep their terms.
    private void ReconcileOaths(PairingState pairing, RulebookDocument? previous, RulebookDocument doc)
    {
        var states = pairing.Rulebook.Oaths;
        var now = Now();
        OathState Offer(Oath oath) => new() { Status = OathStatus.Offered, OfferedUnixSeconds = now, ChangedUnixSeconds = now, OfferedTermsJson = RulebookJson.Serialize(oath) };

        foreach (var (id, s) in states.Where(kv => kv.Value.Status == OathStatus.Offered && doc.Oaths.All(o => o.Id != kv.Key)).ToList())
        {
            s.Status = OathStatus.Withdrawn;
            s.ChangedUnixSeconds = now;
            Log(pairing, RulebookEventKind.OathWithdrawn, id, $"Oath \"{previous?.Oaths.FirstOrDefault(o => o.Id == id)?.Name ?? "oath"}\" withdrawn by the Owner.");
        }

        foreach (var oath in doc.Oaths)
        {
            if (!states.TryGetValue(oath.Id, out var s))
            {
                states[oath.Id] = Offer(oath);
                continue;
            }
            // A running oath keeps its accepted terms. Anything else is offered again once the Owner changes it.
            if (s.Status == OathStatus.Open)
                continue;
            var offered = s.OfferedTermsJson ?? (previous?.Oaths.FirstOrDefault(o => o.Id == oath.Id) is { } older ? RulebookJson.Serialize(older) : null);
            if (offered is null || offered == RulebookJson.Serialize(oath))
                continue;
            Log(pairing, RulebookEventKind.OathWithdrawn, oath.Id, s.Status == OathStatus.Offered
                ? $"Oath \"{oath.Name}\" changed by the Owner and offered again."
                : $"Oath \"{oath.Name}\" offered again with new terms.");
            states[oath.Id] = Offer(oath);
        }
    }

    public void AcceptOath(PairingState pairing, string oathId)
    {
        var state = pairing.Rulebook;
        if (state.Accepted?.Oaths.FirstOrDefault(o => o.Id == oathId) is not { } terms ||
            !state.Oaths.TryGetValue(oathId, out var s) || s.Status != OathStatus.Offered)
            return;
        var now = Now();
        s.Status = OathStatus.Open;
        s.ChangedUnixSeconds = now;
        s.Terms = RulebookJson.Deserialize<Oath>(RulebookJson.Serialize(terms));
        s.ScopeEndsUnixSeconds = terms.Scope == OathScope.ForATime ? now + terms.DurationMinutes * 60L : null;
        s.DutyEntered = false;
        s.DutyStartedUnixSeconds = null;
        s.CurfewArmed = !InCurfew(terms);
        s.PeriodStartUnixSeconds = now;
        s.PeriodCount = 0;
        s.LastMatchUnixSeconds = 0;
        Log(pairing, RulebookEventKind.OathAccepted, oathId, $"Oath \"{terms.Name}\" accepted: {OathText.Describe(terms)}.");
        config.SaveNow();
    }

    public void DeclineOath(PairingState pairing, string oathId)
    {
        var state = pairing.Rulebook;
        if (!state.Oaths.TryGetValue(oathId, out var s) || s.Status != OathStatus.Offered)
            return;
        s.Status = OathStatus.Declined;
        s.ChangedUnixSeconds = Now();
        Log(pairing, RulebookEventKind.OathDeclined, oathId, $"Oath \"{OathName(pairing, oathId)}\" declined.");
        config.SaveNow();
    }

    private static string OathName(PairingState pairing, string oathId) =>
        (pairing.Rulebook.Oaths.TryGetValue(oathId, out var s) ? s.Terms?.Name : null)
        ?? pairing.Rulebook.Accepted?.Oaths.FirstOrDefault(o => o.Id == oathId)?.Name ?? "oath";

    private void ExpireOffers(PairingState pairing)
    {
        var cutoff = Now() - (long)OfferLifetime.TotalSeconds;
        foreach (var (id, s) in pairing.Rulebook.Oaths.Where(kv => kv.Value.Status == OathStatus.Offered && kv.Value.OfferedUnixSeconds < cutoff).ToList())
        {
            s.Status = OathStatus.Expired;
            s.ChangedUnixSeconds = Now();
            Log(pairing, RulebookEventKind.OathExpired, id, $"Oath \"{OathName(pairing, id)}\" expired without an answer.");
        }
    }

    private IEnumerable<(string Id, OathState State, Oath Terms)> OpenOaths(PairingState pairing) =>
        pairing.Rulebook.Oaths
            .Where(kv => kv.Value.Status == OathStatus.Open && kv.Value.Terms is not null && RuleOn(pairing, kv.Key))
            .Select(kv => (kv.Key, kv.Value, kv.Value.Terms!))
            .ToList();

    /// A NextDuty oath only watches once its duty has started; a timed oath watches from acceptance.
    private static bool Watching(OathState s, Oath terms) => terms.Scope == OathScope.ForATime || s.DutyEntered;

    private void ResolveOath(PairingState pairing, string id, OathState s, OathStatus outcome, string why)
    {
        if (s.Status != OathStatus.Open || s.Terms is not { } terms)
            return;
        s.Status = outcome;
        s.ChangedUnixSeconds = Now();
        var name = string.IsNullOrWhiteSpace(terms.Name) ? "oath" : terms.Name;
        switch (outcome)
        {
            case OathStatus.Kept:
                Log(pairing, RulebookEventKind.OathKept, id, $"Oath \"{name}\" kept ({why}).");
                Notify("Oath kept", $"You kept \"{name}\".", NotificationType.Success);
                Fire(pairing, id, $"Oath \"{name}\" kept", terms.Kept, null, 0);
                if (terms.KeptLedger != 0)
                    ChangeLedger(pairing, terms.KeptLedger, $"kept \"{name}\"", notify: false);
                if (pairing.Rulebook.Accepted?.DrawOn.OathKept == true)
                    DrawForEvent(pairing, CardPile.Reward, $"oath \"{name}\" kept");
                break;
            case OathStatus.Broken:
                Log(pairing, RulebookEventKind.OathBroken, id, $"Oath \"{name}\" broken ({why}).");
                Notify("Oath broken", $"You broke \"{name}\": {why}.", NotificationType.Warning);
                Fire(pairing, id, $"Oath \"{name}\" broken", terms.Broken, null, 0);
                if (terms.BrokenLedger != 0)
                    ChangeLedger(pairing, terms.BrokenLedger, $"broke \"{name}\"", notify: false);
                if (pairing.Rulebook.Accepted?.DrawOn.OathBroken == true)
                    DrawForEvent(pairing, CardPile.Punishment, $"oath \"{name}\" broken");
                break;
            default:
                Log(pairing, RulebookEventKind.OathVoided, id, $"Oath \"{name}\" voided ({why}).");
                break;
        }
        config.SaveNow();
    }

    private void VoidOpenOaths(PairingState pairing, string why)
    {
        foreach (var (id, s) in pairing.Rulebook.Oaths.Where(kv => kv.Value.Status == OathStatus.Open).ToList())
            ResolveOath(pairing, id, s, OathStatus.Voided, why);
    }

    private void TickOaths(PairingState pairing)
    {
        var now = Now();
        foreach (var (id, s, terms) in OpenOaths(pairing))
        {
            if (terms.Condition == OathCondition.Curfew)
            {
                var inside = InCurfew(terms);
                if (!inside)
                    s.CurfewArmed = true;
                else if (s.CurfewArmed && Watching(s, terms))
                {
                    ResolveOath(pairing, id, s, OathStatus.Broken, "logged in during curfew");
                    continue;
                }
            }
            if (terms.Condition == OathCondition.DutyTimeLimit && s.DutyStartedUnixSeconds is { } started && events.InStartedDuty
                && now - started > terms.TimeLimitMinutes * 60L)
            {
                ResolveOath(pairing, id, s, OathStatus.Broken, $"the duty took longer than {terms.TimeLimitMinutes} minutes");
                continue;
            }
            if (OathConditions.IsRitual(terms.Condition))
            {
                // Periods that ended while offline are judged first, then being logged in checks in for this one.
                if (CloseMissedPeriods(pairing, id, s, terms, now))
                    continue;
                if (terms.Condition == OathCondition.CheckIn)
                    s.PeriodCount = 1;
            }
            if (terms.Scope == OathScope.ForATime && s.ScopeEndsUnixSeconds is { } ends && now >= ends)
                ResolveOath(pairing, id, s, OathStatus.Kept, "the time ran out");
        }
    }

    /// Walks every period that has ended since the last tick; a period short of its count breaks the oath.
    /// Returns true when it broke. The final period ends together with the oath, so it's judged before "kept".
    private bool CloseMissedPeriods(PairingState pairing, string id, OathState s, Oath terms, long now)
    {
        var length = Math.Max(1, terms.PeriodDays) * 86400L;
        var needed = terms.Condition == OathCondition.CheckIn ? 1 : terms.TimesPerPeriod;
        while (now >= s.PeriodStartUnixSeconds + length)
        {
            if (s.PeriodCount < needed)
            {
                var missed = terms.Condition switch
                {
                    OathCondition.GreetOwner => "didn't greet their Owner in time",
                    OathCondition.MessageOwner => "didn't message their Owner in time",
                    _ => "didn't log in in time",
                };
                ResolveOath(pairing, id, s, OathStatus.Broken, missed);
                return true;
            }
            s.PeriodStartUnixSeconds += length;
            s.PeriodCount = 0;
        }
        return false;
    }

    private void CountRitual(PairingState pairing, string id, OathState s, Oath terms, string what)
    {
        if (s.PeriodCount >= terms.TimesPerPeriod)
            return;
        s.PeriodCount++;
        var name = string.IsNullOrWhiteSpace(terms.Name) ? "oath" : terms.Name;
        Log(pairing, RulebookEventKind.RitualDone, id, $"\"{name}\": {what} ({s.PeriodCount}/{terms.TimesPerPeriod} this time).");
        if (s.PeriodCount == terms.TimesPerPeriod)
            Notify("Oath", $"\"{name}\" done for now.", NotificationType.Success);
        config.Save();
    }

    private void OnOwnEmote(uint emoteId, ulong targetObjectId)
    {
        foreach (var pairing in SubPairings().Where(IsRunning))
        {
            if (FindOwner(pairing) is not { } owner || owner.GameObjectId != targetObjectId)
                continue;
            foreach (var (id, s, terms) in OpenOaths(pairing))
                if (terms.Condition == OathCondition.GreetOwner && terms.EmoteId == emoteId)
                    CountRitual(pairing, id, s, terms, $"greeted {pairing.PeerName} with {OathText.EmoteName(emoteId)}");
        }
    }

    private static readonly Dalamud.Game.Text.XivChatType[] PublicChannels =
        [Dalamud.Game.Text.XivChatType.Say, Dalamud.Game.Text.XivChatType.Shout, Dalamud.Game.Text.XivChatType.Yell];

    private static readonly Dalamud.Game.Text.XivChatType[] SpokenChannels =
    [
        Dalamud.Game.Text.XivChatType.Say, Dalamud.Game.Text.XivChatType.Shout, Dalamud.Game.Text.XivChatType.Yell,
        Dalamud.Game.Text.XivChatType.TellOutgoing, Dalamud.Game.Text.XivChatType.Party, Dalamud.Game.Text.XivChatType.CrossParty,
        Dalamud.Game.Text.XivChatType.Alliance, Dalamud.Game.Text.XivChatType.FreeCompany, Dalamud.Game.Text.XivChatType.CustomEmote,
        Dalamud.Game.Text.XivChatType.Ls1, Dalamud.Game.Text.XivChatType.Ls2, Dalamud.Game.Text.XivChatType.Ls3, Dalamud.Game.Text.XivChatType.Ls4,
        Dalamud.Game.Text.XivChatType.Ls5, Dalamud.Game.Text.XivChatType.Ls6, Dalamud.Game.Text.XivChatType.Ls7, Dalamud.Game.Text.XivChatType.Ls8,
        Dalamud.Game.Text.XivChatType.CrossLinkShell1, Dalamud.Game.Text.XivChatType.CrossLinkShell2, Dalamud.Game.Text.XivChatType.CrossLinkShell3,
        Dalamud.Game.Text.XivChatType.CrossLinkShell4, Dalamud.Game.Text.XivChatType.CrossLinkShell5, Dalamud.Game.Text.XivChatType.CrossLinkShell6,
        Dalamud.Game.Text.XivChatType.CrossLinkShell7, Dalamud.Game.Text.XivChatType.CrossLinkShell8,
    ];

    /// Only the Sub's own lines count: an outgoing tell (whose sender field names the recipient), or a line in a
    /// spoken channel sent by the local player. OriginalMessage, so a gag's rewrite doesn't hide what they typed.
    private void OnChatMessage(Dalamud.Game.Chat.IChatMessage message)
    {
        var kind = message.LogKind;
        if (Array.IndexOf(SpokenChannels, kind) < 0 || Plugin.ObjectTable.LocalPlayer is not { } me)
            return;
        var text = message.OriginalMessage.ExtractText();
        var outgoingTell = kind == Dalamud.Game.Text.XivChatType.TellOutgoing;
        var (name, world) = SenderOf(message.Sender);
        if (!outgoingTell && !string.Equals(name, me.Name.TextValue, StringComparison.OrdinalIgnoreCase))
            return;
        // Lines this plugin sent by itself (automatic tells, a reaction's reply, an Owner's Custom Trigger chat)
        // are neither the Sub keeping nor the Sub breaking an oath.
        if (PluginOutput.ConsumeEcho(text))
            return;

        foreach (var pairing in SubPairings().Where(IsRunning))
        {
            var toOwner = outgoingTell && string.Equals(name, pairing.PeerName, StringComparison.OrdinalIgnoreCase)
                && (world is null || string.Equals(world, pairing.PeerWorld, StringComparison.OrdinalIgnoreCase));
            foreach (var (id, s, terms) in OpenOaths(pairing))
            {
                var phrase = terms.Phrase.Trim();
                var hasPhrase = phrase.Length == 0 || text.Contains(phrase, StringComparison.OrdinalIgnoreCase);
                switch (terms.Condition)
                {
                    case OathCondition.MessageOwner when toOwner && hasPhrase:
                        CountRitual(pairing, id, s, terms, $"messaged {pairing.PeerName}");
                        break;
                    case OathCondition.SayGoodnight when toOwner && hasPhrase:
                        s.LastMatchUnixSeconds = Now();
                        config.Save();
                        break;
                    case OathCondition.AddressOwner when toOwner && !text.Contains(phrase, StringComparison.OrdinalIgnoreCase):
                        ResolveOath(pairing, id, s, OathStatus.Broken, $"sent {pairing.PeerName} a tell without calling them \"{phrase}\"");
                        break;
                    case OathCondition.ForbiddenWord when phrase.Length > 0 && text.Contains(phrase, StringComparison.OrdinalIgnoreCase):
                        ResolveOath(pairing, id, s, OathStatus.Broken, $"said \"{phrase}\"");
                        break;
                    case OathCondition.QuietInPublic when Array.IndexOf(PublicChannels, kind) >= 0:
                        ResolveOath(pairing, id, s, OathStatus.Broken, "spoke in public chat");
                        break;
                }
            }
        }
    }

    private static (string? Name, string? World) SenderOf(Dalamud.Game.Text.SeStringHandling.SeString sender)
    {
        var player = sender.Payloads.OfType<Dalamud.Game.Text.SeStringHandling.Payloads.PlayerPayload>().FirstOrDefault();
        if (player is not null)
            return (player.PlayerName, player.World.Value.Name.ExtractText());
        var text = sender.TextValue.Trim();
        var at = text.IndexOf('@');
        return at >= 0 ? (text[..at].Trim(), text[(at + 1)..].Trim()) : (text.Length > 0 ? text : null, null);
    }

    /// Only a normal logout is seen; closing the game outright can't be told apart from a crash, so it never breaks.
    private void OnLogout(int type, int code)
    {
        var cutoff = Now() - RulebookLimits.GoodnightWindowMinutes * 60L;
        foreach (var pairing in SubPairings().Where(IsRunning))
            foreach (var (id, s, terms) in OpenOaths(pairing))
                if (terms.Condition == OathCondition.SayGoodnight && s.LastMatchUnixSeconds < cutoff)
                    ResolveOath(pairing, id, s, OathStatus.Broken, $"logged off without telling {pairing.PeerName} goodnight");
    }

    private static bool InCurfew(Oath terms)
    {
        var local = DateTime.Now;
        var minute = local.Hour * 60 + local.Minute;
        var start = terms.CurfewStartMinutes;
        var end = terms.CurfewEndMinutes;
        return start < end ? minute >= start && minute < end : minute >= start || minute < end;
    }

    private void OnDutyStarted(uint territory)
    {
        foreach (var pairing in SubPairings().Where(IsRunning))
        {
            foreach (var (_, s, terms) in OpenOaths(pairing))
            {
                if (terms.Scope == OathScope.NextDuty && !s.DutyEntered)
                    s.DutyEntered = true;
                if (terms.Condition == OathCondition.DutyTimeLimit && Watching(s, terms))
                    s.DutyStartedUnixSeconds = Now();
            }
            config.Save();
        }
    }

    private void OnDutyWiped()
    {
        foreach (var pairing in SubPairings().Where(IsRunning))
        {
            foreach (var (id, s, terms) in OpenOaths(pairing))
                if (terms.Condition == OathCondition.NoWipes && Watching(s, terms))
                    ResolveOath(pairing, id, s, OathStatus.Broken, "the party wiped");
            if (pairing.Rulebook.Accepted?.DrawOn.Wipe == true)
                DrawForEvent(pairing, CardPile.Punishment, "a wipe");
        }
    }

    private void OnDutyCompleted()
    {
        var now = Now();
        foreach (var pairing in SubPairings().Where(IsRunning))
        {
            foreach (var (id, s, terms) in OpenOaths(pairing))
            {
                if (!Watching(s, terms))
                    continue;
                if (terms.Condition == OathCondition.DutyTimeLimit && s.DutyStartedUnixSeconds is { } started)
                {
                    s.DutyStartedUnixSeconds = null;
                    if (now - started > terms.TimeLimitMinutes * 60L)
                    {
                        ResolveOath(pairing, id, s, OathStatus.Broken, $"the duty took longer than {terms.TimeLimitMinutes} minutes");
                        continue;
                    }
                }
                if (terms.Scope == OathScope.NextDuty)
                    ResolveOath(pairing, id, s, OathStatus.Kept, "the duty was completed");
            }
        }
    }

    private void OnDutyAbandoned()
    {
        foreach (var pairing in SubPairings().Where(IsRunning))
            foreach (var (id, s, terms) in OpenOaths(pairing))
            {
                if (terms.Scope == OathScope.NextDuty && s.DutyEntered)
                    ResolveOath(pairing, id, s, OathStatus.Voided, "the duty was left before it was completed");
                else
                    s.DutyStartedUnixSeconds = null;
            }
    }

    private void OnLocalDeath()
    {
        foreach (var pairing in SubPairings().Where(IsRunning))
        {
            foreach (var (id, s, terms) in OpenOaths(pairing))
                if (terms.Condition == OathCondition.NoDeaths && Watching(s, terms))
                    ResolveOath(pairing, id, s, OathStatus.Broken, "your character died");
            if (pairing.Rulebook.Accepted?.DrawOn.Death == true)
                DrawForEvent(pairing, CardPile.Punishment, "your character dying");
        }
    }

    // ---- Places ----

    private void OnTerritoryEntered(uint from, uint to)
    {
        foreach (var pairing in SubPairings().Where(IsRunning))
        {
            foreach (var (id, s, terms) in OpenOaths(pairing))
            {
                if (!Watching(s, terms) || from == 0)
                    continue;
                if (terms.Condition == OathCondition.StayInPlaces && !RulebookPlaces.MatchesAny(terms.Places, to))
                    ResolveOath(pairing, id, s, OathStatus.Broken, $"entered {RulebookPlaces.TerritoryName(to)}");
                else if (terms.Condition == OathCondition.AvoidPlaces && RulebookPlaces.MatchesAny(terms.Places, to))
                    ResolveOath(pairing, id, s, OathStatus.Broken, $"entered {RulebookPlaces.TerritoryName(to)}");
            }

            foreach (var rule in pairing.Rulebook.Accepted!.Places.Where(r => RuleOn(pairing, r.Id)))
            {
                var wasIn = from != 0 && RulebookPlaces.MatchesAny(rule.Places, from);
                var isIn = RulebookPlaces.MatchesAny(rule.Places, to);
                var name = string.IsNullOrWhiteSpace(rule.Name) ? "Place rule" : rule.Name;
                if (!wasIn && isIn)
                    Fire(pairing, rule.Id, $"{name} (entered)", rule.Enter, rule.Id + ":enter", rule.CooldownSeconds);
                else if (wasIn && !isIn)
                    Fire(pairing, rule.Id, $"{name} (left)", rule.Leave, rule.Id + ":leave", rule.CooldownSeconds);
            }
        }
    }

    // ---- Presence ----

    private void TickPresence(PairingState pairing, DateTime now)
    {
        var rules = pairing.Rulebook.Accepted!.Presence.Where(r => RuleOn(pairing, r.Id)).ToList();
        if (rules.Count == 0)
            return;
        var distance = OwnerDistance(pairing);
        foreach (var rule in rules)
        {
            var key = (pairing.Id, rule.Id);
            if (!presence.TryGetValue(key, out var p))
                presence[key] = p = new PresenceState();
            var inRange = distance is { } d && d <= rule.RangeYalms;
            var name = string.IsNullOrWhiteSpace(rule.Name) ? "Presence rule" : rule.Name;
            if (inRange)
            {
                p.OutOfRangeSince = null;
                p.InRangeSince ??= now;
                if (!p.Arrived && now - p.InRangeSince >= ArriveAfter)
                {
                    p.Arrived = true;
                    Fire(pairing, rule.Id, $"{name} ({pairing.PeerName} arrived)", rule.Arrive, rule.Id + ":arrive", rule.CooldownSeconds);
                }
            }
            else
            {
                p.InRangeSince = null;
                if (!p.Arrived)
                    continue;
                p.OutOfRangeSince ??= now;
                if (now - p.OutOfRangeSince >= DepartAfter)
                {
                    p.Arrived = false;
                    p.OutOfRangeSince = null;
                    Fire(pairing, rule.Id, $"{name} ({pairing.PeerName} left)", rule.Depart, rule.Id + ":depart", rule.CooldownSeconds);
                }
            }
        }
    }

    /// Matched by name and home world, never by name alone.
    private static float? OwnerDistance(PairingState pairing) =>
        Plugin.ObjectTable.LocalPlayer is { } me && FindOwner(pairing) is { } owner ? Vector3.Distance(me.Position, owner.Position) : null;

    /// Matched by name and home world, never by name alone.
    private static IPlayerCharacter? FindOwner(PairingState pairing)
    {
        if (Plugin.ObjectTable.LocalPlayer is not { } me)
            return null;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter pc || pc.GameObjectId == me.GameObjectId)
                continue;
            if (string.Equals(pc.Name.TextValue, pairing.PeerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pc.HomeWorld.ValueNullable?.Name.ExtractText(), pairing.PeerWorld, StringComparison.OrdinalIgnoreCase))
                return pc;
        }
        return null;
    }

    // ---- Deck ----

    private void DrawForEvent(PairingState pairing, CardPile pile, string reason)
    {
        if (!RuleOn(pairing, DrawOnSettings.RuleId) || pairing.Rulebook.Accepted is not { } doc)
            return;
        // The draw itself is what the cooldown limits; the card's consequence then runs without a second check.
        if (!TryReserveCooldown(pairing, DrawOnSettings.RuleId, doc.DrawOn.CooldownSeconds))
            return;
        Draw(pairing, pile, reason);
    }

    private readonly Dictionary<(Guid, string), DateTime> drawCooldowns = new();

    private bool TryReserveCooldown(PairingState pairing, string key, int seconds)
    {
        var now = DateTime.UtcNow;
        if (drawCooldowns.TryGetValue((pairing.Id, key), out var last) && now - last < TimeSpan.FromSeconds(Math.Max(seconds, RulebookLimits.MinCooldownSeconds)))
            return false;
        drawCooldowns[(pairing.Id, key)] = now;
        return true;
    }

    /// Weighted by card weight over the pile's cards still in the deck. Null when that pile is empty.
    public DeckCard? Draw(PairingState pairing, CardPile pile, string reason)
    {
        if (!IsRunning(pairing) || pairing.Rulebook.Accepted is not { } doc)
            return null;
        var state = pairing.Rulebook;
        var cards = doc.Deck.Where(c => c.Pile == pile && !state.RemovedOnceCards.Contains(c.Id) && RuleOn(pairing, c.Id)).ToList();
        if (cards.Count == 0)
        {
            Log(pairing, RulebookEventKind.DeckEmpty, null, $"Tried to draw a {CardPiles.Word(pile)} card for {reason}, but there are none.");
            return null;
        }
        var roll = Random.Shared.Next(cards.Sum(c => c.Weight));
        var card = cards[^1];
        foreach (var c in cards)
        {
            if (roll < c.Weight) { card = c; break; }
            roll -= c.Weight;
        }
        if (card.Once)
            state.RemovedOnceCards.Add(card.Id);
        Log(pairing, RulebookEventKind.CardDrawn, card.Id, $"Drew \"{card.Name}\" for {reason}: {ConsequenceText.Describe(card.Consequence)}.");
        Notify("Card drawn", $"\"{card.Name}\": {ConsequenceText.Describe(card.Consequence)}");
        Fire(pairing, card.Id, $"Card \"{card.Name}\"", card.Consequence, null, 0);
        config.SaveNow();
        return card;
    }

    // ---- Ledger ----

    private void ChangeLedger(PairingState pairing, int delta, string reason, bool notify)
    {
        var state = pairing.Rulebook;
        var before = state.LedgerScore;
        state.LedgerScore = Math.Clamp(before + delta, RulebookLimits.MinLedger, RulebookLimits.MaxLedger);
        var text = $"{(delta > 0 ? "+" : "")}{delta}" + (reason.Length > 0 ? $": {reason}" : "");
        Log(pairing, RulebookEventKind.LedgerChanged, null, $"Ledger {text} (now {state.LedgerScore}).");
        if (notify)
            Notify("Ledger", $"{text} (now {state.LedgerScore})");
        EvaluateThresholds(pairing);
        config.SaveNow();
    }

    /// A threshold fires on the change that makes it met, and again only after it stopped being met.
    private void EvaluateThresholds(PairingState pairing)
    {
        var state = pairing.Rulebook;
        if (state.Accepted is not { } doc)
            return;
        // Bounded: a reset can make another threshold met, but never loop forever.
        for (var pass = 0; pass < 3; pass++)
        {
            var reset = false;
            foreach (var t in doc.Thresholds)
            {
                var met = t.Direction == ThresholdDirection.AtOrAbove ? state.LedgerScore >= t.Score : state.LedgerScore <= t.Score;
                if (!met)
                {
                    state.ThresholdsMet.Remove(t.Id);
                    continue;
                }
                if (!state.ThresholdsMet.Add(t.Id) || !RuleOn(pairing, t.Id))
                    continue;
                var name = string.IsNullOrWhiteSpace(t.Name) ? $"Ledger {(t.Direction == ThresholdDirection.AtOrAbove ? "at or above" : "at or below")} {t.Score}" : t.Name;
                Log(pairing, RulebookEventKind.ThresholdCrossed, t.Id, $"Threshold \"{name}\" reached at {state.LedgerScore}.");
                Fire(pairing, t.Id, $"Threshold \"{name}\"", t.Consequence, t.Id, t.CooldownSeconds);
                if (t.DrawCard)
                    Draw(pairing, t.Pile, $"threshold \"{name}\"");
                if (t.ResetTo is { } to)
                {
                    state.LedgerScore = to;
                    state.ThresholdsMet.Remove(t.Id);
                    Log(pairing, RulebookEventKind.LedgerChanged, t.Id, $"Ledger reset to {to}.");
                    reset = true;
                    break;
                }
            }
            if (!reset)
                return;
        }
    }

    // ---- Owner commands arriving on the Sub ----

    public LocalTestResult DeckDraw(string rest, PairingState? sourcePairing)
    {
        if (!CardPiles.TryParseDraw(rest, out var pile))
            return LocalTestResult.Fail("Expected \"deck draw reward\" or \"deck draw punishment\".");
        if (sourcePairing is null || !IsRunning(sourcePairing))
            return LocalTestResult.Fail("No running rulebook for this pairing.");
        return Draw(sourcePairing, pile, $"{sourcePairing.PeerName}'s draw") is { } card
            ? LocalTestResult.Ok($"Drew \"{card.Name}\".")
            : LocalTestResult.Fail($"No {CardPiles.Word(pile)} cards to draw.");
    }

    public LocalTestResult Ledger(string rest, PairingState? sourcePairing)
    {
        if (!LedgerCommand.TryParse(rest, out var delta, out var reason))
            return LocalTestResult.Fail("Expected \"ledger +n\" or \"ledger -n\" (n 1-99).");
        if (sourcePairing is null || !IsRunning(sourcePairing))
            return LocalTestResult.Fail("No running rulebook for this pairing.");
        ChangeLedger(sourcePairing, delta, reason, notify: true);
        return LocalTestResult.Ok($"Ledger now {sourcePairing.Rulebook.LedgerScore}.");
    }

    // ---- Owner side actions ----

    public System.Threading.Tasks.Task<string?> PublishAsync(PairingState pairing) => mailbox.PublishRulebookAsync(pairing, CancellationToken.None);

    /// The `collarrulebook` nudge, only after a successful publish the Owner clicked.
    public void SendNudge(PairingState pairing)
    {
        if (pairing is { Direction: PairingDirection.OwnerSide, IsPaired: true, PeerName: { } name, PeerWorld: { } world })
            sender.Send(composer.ComposeRulebookNudge(name, world));
    }

    /// Owner: send `deck draw <pile>` or `ledger ±n reason` to the active Sub like any other command.
    public void SendOwnerCommand(string command)
    {
        foreach (var message in composer.ComposeAll(command))
            sender.Send(message);
    }

    private void OnReportReceived(PairingState pairing, RulebookReport report)
    {
        Plugin.Log.Debug($"Rulebook report from {pairing.PeerName}: version {report.AcceptedVersion}, ledger {report.LedgerScore}.");
    }

    // ---- Reports (Sub) ----

    public RulebookReport BuildReport(PairingState pairing)
    {
        var state = pairing.Rulebook;
        return new RulebookReport
        {
            GeneratedAt = Now(),
            AcceptedVersion = state.Accepted?.Version ?? 0,
            PendingVersion = state.Pending?.Version,
            DeclinedVersion = state.LastDeclinedVersion,
            PermissionOn = config.Permissions.Rulebook && config.RulebookAcknowledged,
            Suspended = config.RulebookSuspended,
            LedgerScore = state.LedgerScore,
            DisabledRuleIds = state.DisabledRuleIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Oaths = state.Oaths.Select(kv => new ReportedOath
            {
                Id = kv.Key,
                Name = OathName(pairing, kv.Key),
                Status = kv.Value.Status,
                ChangedAt = kv.Value.ChangedUnixSeconds,
                ScopeEndsAt = kv.Value.ScopeEndsUnixSeconds,
            }).OrderBy(o => o.Id, StringComparer.Ordinal).ToList(),
            Events = state.Activity.ToList(),
        };
    }

    private static string ReportDigest(RulebookReport report)
    {
        var generated = report.GeneratedAt;
        report.GeneratedAt = 0;
        var digest = RelayCrypto.Sha256Hex(RulebookJson.Serialize(report));
        report.GeneratedAt = generated;
        return digest;
    }

    private void MaybeUploadReport(PairingState pairing, DateTime now)
    {
        var state = pairing.Rulebook;
        if (reportsInFlight.Contains(pairing.Id))
            return;
        if (reportRetryAfter.TryGetValue(pairing.Id, out var retry) && now < retry)
            return;
        if (state.LastReportUploadUnixSeconds > 0 && now - DateTimeOffset.FromUnixTimeSeconds(state.LastReportUploadUnixSeconds).UtcDateTime < ReportInterval)
            return;
        var report = BuildReport(pairing);
        var digest = ReportDigest(report);
        if (digest == state.LastUploadedReportDigest)
            return;

        reportsInFlight.Add(pairing.Id);
        Plugin.FireAndForget(UploadAsync());

        async System.Threading.Tasks.Task UploadAsync()
        {
            var ok = false;
            try
            {
                ok = await mailbox.UploadReportAsync(pairing, report, backgroundToken()).ConfigureAwait(false);
            }
            finally
            {
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    reportsInFlight.Remove(pairing.Id);
                    if (ok)
                    {
                        pairing.Rulebook.LastUploadedReportDigest = digest;
                        reportRetryAfter.Remove(pairing.Id);
                        config.Save();
                    }
                    else
                    {
                        reportRetryAfter[pairing.Id] = DateTime.UtcNow.AddMinutes(5);
                    }
                }).ConfigureAwait(false);
            }
        }
    }
}
