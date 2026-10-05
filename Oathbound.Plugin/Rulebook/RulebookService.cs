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
    private static readonly TimeSpan ArriveAfter = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan DepartAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PresenceTickInterval = TimeSpan.FromMilliseconds(250);
    /// Leaving needs this much past the range, so standing on the edge doesn't flip arrive/leave back and forth.
    private const float DepartMarginFraction = 0.25f;
    private const float MinDepartMarginYalms = 1f;
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(RelayProtocolConstants.RulebookReportMinUploadIntervalSeconds + 5);

    private readonly PluginConfig config;
    private readonly RulebookMailboxService mailbox;
    private readonly ChatCommandListener listener;
    private readonly ChatComposer composer;
    private readonly ChatSender sender;
    private readonly RulebookGameEvents events;
    private readonly Safety.EmoteWatcher emotes;
    private readonly Commands.GestureCommand gesture;
    private readonly Commands.FollowCommand follow;
    private readonly Commands.OutfitCommand outfit;
    private readonly Ipc.GlamourerIpc glamourer;
    private readonly Safety.SlotLockManager slotLocks;
    /// The oath the Sub is performing; its own emote isn't counted on top, and it counts only once the hold completes.
    private (Guid PairingId, string OathId)? performing;
    private readonly Func<CancellationToken> backgroundToken;
    public ConsequenceQueue Queue { get; }

    private readonly Dictionary<(Guid, string), PresenceState> presence = new();
    private readonly HashSet<Guid> reportsInFlight = new();
    private readonly Dictionary<Guid, DateTime> reportRetryAfter = new();
    private readonly HashSet<Guid> wasRunning = new();
    private DateTime nextSecondTick = DateTime.MinValue;
    private DateTime nextPresenceTick = DateTime.MinValue;

    private sealed class PresenceState
    {
        public bool Arrived;
        public DateTime? InRangeSince;
        public DateTime? OutOfRangeSince;
    }

    public RulebookService(PluginConfig config, RulebookMailboxService mailbox, ChatCommandListener listener, ChatComposer composer, ChatSender sender, Safety.EmoteWatcher emotes,
        Commands.GestureCommand gesture, Commands.FollowCommand follow, Commands.OutfitCommand outfit, Ipc.GlamourerIpc glamourer, Safety.SlotLockManager slotLocks,
        Func<CancellationToken> backgroundToken)
    {
        this.config = config;
        this.gesture = gesture;
        this.follow = follow;
        this.outfit = outfit;
        this.glamourer = glamourer;
        this.slotLocks = slotLocks;
        this.mailbox = mailbox;
        this.listener = listener;
        this.composer = composer;
        this.sender = sender;
        this.backgroundToken = backgroundToken;
        this.emotes = emotes;
        events = new RulebookGameEvents();
        Queue = new ConsequenceQueue(RunCommand, OnCommandResult, RulebookGameEvents.ShouldHoldConsequences);

        // Accepting a version is the Sub's consent to every rule in it; switches from older builds no longer apply.
        foreach (var pairing in config.Pairings.Where(p => p.Rulebook.DisabledRuleIds.Count > 0))
            pairing.Rulebook.DisabledRuleIds.Clear();

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
        events.JobChanged += OnJobChanged;
        outfit.OwnerDesignApplied += OnOwnerDesignApplied;
        outfit.RevertedToBase += ForgetOwnerOutfits;
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
        outfit.OwnerDesignApplied -= OnOwnerDesignApplied;
        outfit.RevertedToBase -= ForgetOwnerOutfits;
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
        // Walking through a few yalms takes about a second, so presence can't wait for the once-a-second tick.
        if (now >= nextPresenceTick)
        {
            nextPresenceTick = now + PresenceTickInterval;
            foreach (var pairing in SubPairings())
                if (pairing.Rulebook.Accepted is not null && IsRunning(pairing))
                {
                    TickPresence(pairing, now);
                    TickSideOaths(pairing);
                }
        }

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
                    TickKeepItOn(pairing);
                    TickTimeRules(pairing);
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
        ForgetOwnerOutfits();
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
    /// `extra` runs alongside the commands when they're accepted (not cooling down): a presence rule's leash
    /// and message, which aren't commands any other rule may run.
    private void Fire(PairingState pairing, string ruleId, string label, IReadOnlyList<string> commands, string? cooldownKey, int cooldownSeconds, Action? extra = null)
    {
        if ((commands.Count == 0 && extra is null) || !IsRunning(pairing))
            return;
        var outcome = Queue.Enqueue(new ConsequenceQueue.Item(pairing.Id, ruleId, label, commands.ToList()), cooldownKey, cooldownSeconds);
        if (outcome is ConsequenceQueue.Outcome.Ran or ConsequenceQueue.Outcome.Held)
            extra?.Invoke();
        if (outcome == ConsequenceQueue.Outcome.CoolingDown)
            Log(pairing, RulebookEventKind.ConsequenceSkipped, ruleId, $"{label}: skipped - its cooldown ({Math.Max(cooldownSeconds, RulebookLimits.MinCooldownSeconds)}s) hasn't passed yet.");
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
        if (doc.ResetCount > state.AppliedResetCount)
        {
            StartOver(pairing, doc.ResetCount);
            previous = null;
        }
        state.Accepted = doc;
        state.Pending = null;
        state.AcceptedUnixSeconds = Now();
        state.RemovedOnceCards.Clear();
        state.ThresholdsMet.RemoveWhere(id => doc.Thresholds.All(t => t.Id != id));
        ReconcileOaths(pairing, previous, doc);
        Log(pairing, RulebookEventKind.VersionAccepted, null, $"Version {doc.Version} accepted.");
        config.SaveNow();
    }

    /// Clears everything the Sub's side built up, keeping only the relay plumbing (keys, sequences).
    private void StartOver(PairingState pairing, int resetCount)
    {
        var state = pairing.Rulebook;
        Queue.DropFor(pairing.Id);
        state.Accepted = null;
        state.LastDeclinedVersion = null;
        state.Oaths.Clear();
        state.LedgerScore = 0;
        state.ThresholdsMet.Clear();
        state.RemovedOnceCards.Clear();
        state.Activity.Clear();
        state.ShopLastBought.Clear();
        state.AppliedResetCount = resetCount;
        Log(pairing, RulebookEventKind.VersionAccepted, null, $"{pairing.PeerName} started the rulebook over: oaths, ledger and history cleared.");
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

    // ---- Oaths ----

    /// New oaths are offered; changed or removed offers are withdrawn (a changed one is offered again). Accepting the
    /// version is the Sub's consent to its terms, so open oaths take the new terms and removed ones are voided.
    private void ReconcileOaths(PairingState pairing, RulebookDocument? previous, RulebookDocument doc)
    {
        var states = pairing.Rulebook.Oaths;
        var now = Now();
        OathState Offer(Oath oath) => new() { Status = OathStatus.Offered, OfferedUnixSeconds = now, ChangedUnixSeconds = now, OfferedTermsJson = RulebookJson.Serialize(oath) };

        foreach (var (id, s) in states.Where(kv => kv.Value.Status == OathStatus.Open && kv.Value.Terms is not null).ToList())
        {
            if (doc.Oaths.FirstOrDefault(o => o.Id == id) is not { } oath)
            {
                ResolveOath(pairing, id, s, OathStatus.Voided, "removed by your Owner");
                continue;
            }
            var old = s.Terms!;
            if (RulebookJson.Serialize(old) == RulebookJson.Serialize(oath))
                continue;
            if (SameTermsIgnoringName(old, oath))
            {
                s.Terms = RulebookJson.Deserialize<Oath>(RulebookJson.Serialize(oath));
                s.OfferedTermsJson = RulebookJson.Serialize(oath);
                continue;
            }
            // Carried over without scoring again; the new terms run from now so a shorter oath can't pay out at once.
            var carried = SameAction(old, oath) ? Math.Min(s.PeriodCount, RitualNeeded(oath)) : 0;
            StartOath(s, oath, now);
            s.PeriodCount = carried;
            // Logged as an acceptance: a new event kind would make an older Owner's report parse fail.
            Log(pairing, RulebookEventKind.OathAccepted, id, $"Oath \"{oath.Name}\" updated by your Owner: {OathText.Describe(oath)}.");
        }

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
            // Open oaths were brought up to date above. Anything else is offered again once the Owner changes it.
            if (s.Status == OathStatus.Open)
                continue;
            var offered = s.OfferedTermsJson ?? (previous?.Oaths.FirstOrDefault(o => o.Id == oath.Id) is { } older ? RulebookJson.Serialize(older) : null);
            if (offered is null || Normalized(offered) == RulebookJson.Serialize(oath))
                continue;
            Log(pairing, RulebookEventKind.OathWithdrawn, oath.Id, s.Status == OathStatus.Offered
                ? $"Oath \"{oath.Name}\" changed by the Owner and offered again."
                : $"Oath \"{oath.Name}\" offered again with new terms.");
            states[oath.Id] = Offer(oath);
        }
    }

    /// Terms stored by an older build lack fields added since; read and written again they compare equal.
    private static string Normalized(string termsJson) =>
        RulebookJson.Deserialize<Oath>(termsJson) is { } terms ? RulebookJson.Serialize(terms) : termsJson;

    public void AcceptOath(PairingState pairing, string oathId)
    {
        var state = pairing.Rulebook;
        if (state.Accepted?.Oaths.FirstOrDefault(o => o.Id == oathId) is not { } terms ||
            !state.Oaths.TryGetValue(oathId, out var s) || s.Status != OathStatus.Offered)
            return;
        StartOath(s, terms, Now());
        Log(pairing, RulebookEventKind.OathAccepted, oathId, $"Oath \"{terms.Name}\" accepted: {OathText.Describe(terms)}.");
        config.SaveNow();
    }

    private void StartOath(OathState s, Oath terms, long now)
    {
        s.Status = OathStatus.Open;
        s.ChangedUnixSeconds = now;
        s.Terms = RulebookJson.Deserialize<Oath>(RulebookJson.Serialize(terms));
        s.OfferedTermsJson = RulebookJson.Serialize(terms);
        s.ScopeEndsUnixSeconds = terms.Scope == OathScope.ForATime ? now + terms.DurationMinutes * 60L : null;
        s.DutyEntered = false;
        s.DutyStartedUnixSeconds = null;
        s.CurfewArmed = !InCurfew(terms);
        s.PeriodStartUnixSeconds = now;
        s.PeriodCount = 0;
        s.MissedPeriods = 0;
        s.LastMatchUnixSeconds = 0;
        s.StrikesUsed = 0;
        s.StopRenewing = false;
        s.GraceEndsUnixSeconds = null;
        s.LeaveGrantedUntilUnixSeconds = 0;
    }

    /// Equal apart from the name, so renaming a running oath doesn't restart it.
    public static bool SameTermsIgnoringName(Oath a, Oath b) => Unnamed(a) == Unnamed(b);

    private static string Unnamed(Oath oath)
    {
        var copy = RulebookJson.Deserialize<Oath>(RulebookJson.Serialize(oath))!;
        copy.Name = "";
        return RulebookJson.Serialize(copy);
    }

    /// Still asks for the same action, so what was done in the current period keeps counting after an edit.
    private static bool SameAction(Oath a, Oath b) =>
        a.Condition == b.Condition && a.EmoteId == b.EmoteId && a.AnimationId == b.AnimationId &&
        string.Equals(a.Phrase.Trim(), b.Phrase.Trim(), StringComparison.OrdinalIgnoreCase);

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
            .Where(kv => kv.Value.Status == OathStatus.Open && kv.Value.Terms is not null)
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
        if (outcome is OathStatus.Kept or OathStatus.Broken)
        {
            if (!OathConditions.IsRitual(terms.Condition) && terms.Recurrence != OathRecurrence.Once)
            {
                if (outcome == OathStatus.Kept)
                    CountStreak(pairing, id, s, terms);
                else
                    s.Streak = 0;
            }
            Recur(pairing, id, s);
        }
        config.SaveNow();
    }

    /// A violation is a strike while the oath has strikes left, and breaks it after that.
    private void Violate(PairingState pairing, string id, OathState s, Oath terms, string why)
    {
        if (s.StrikesUsed >= terms.Strikes)
        {
            ResolveOath(pairing, id, s, OathStatus.Broken, why);
            return;
        }
        s.StrikesUsed++;
        var name = string.IsNullOrWhiteSpace(terms.Name) ? "oath" : terms.Name;
        var left = terms.Strikes - s.StrikesUsed;
        // A ritual-progress line: a new event kind would make an older Owner's report unreadable.
        Log(pairing, RulebookEventKind.RitualDone, id, $"\"{name}\": strike {s.StrikesUsed} of {terms.Strikes} ({why}).");
        if (terms.LedgerPerStrike != 0)
            ChangeLedger(pairing, terms.LedgerPerStrike, $"strike on \"{name}\"", notify: false);
        Notify("Strike", left == 0 ? $"\"{name}\": {why}. The next one breaks it." : $"\"{name}\": {why}. {left} {(left == 1 ? "strike" : "strikes")} left.", NotificationType.Warning);
        config.Save();
    }

    /// Repeats done in full (rituals) or runs kept (other recurring oaths); pays at every multiple of the streak.
    private void CountStreak(PairingState pairing, string id, OathState s, Oath terms)
    {
        s.Streak++;
        if (terms.StreakEvery <= 0 || s.Streak % terms.StreakEvery != 0)
            return;
        var name = string.IsNullOrWhiteSpace(terms.Name) ? "oath" : terms.Name;
        Log(pairing, RulebookEventKind.RitualDone, id, $"\"{name}\": {s.Streak} in a row - streak bonus.");
        Notify("Streak", $"\"{name}\": {s.Streak} in a row!", NotificationType.Success);
        if (terms.StreakLedger != 0)
            ChangeLedger(pairing, terms.StreakLedger, $"{s.Streak} in a row on \"{name}\"", notify: false);
        if (terms.StreakDrawReward)
            DrawForEvent(pairing, CardPile.Reward, $"{s.Streak} in a row on \"{name}\"");
    }

    /// After a kept or broken run, under the latest accepted terms, so the Owner's edits apply to the next one.
    private void Recur(PairingState pairing, string id, OathState s)
    {
        if (s.StopRenewing || pairing.Rulebook.Accepted?.Oaths.FirstOrDefault(o => o.Id == id) is not { } latest
            || latest.Recurrence is OathRecurrence.Once or OathRecurrence.Unknown)
            return;
        var name = string.IsNullOrWhiteSpace(latest.Name) ? "oath" : latest.Name;
        var now = Now();
        if (latest.Recurrence == OathRecurrence.Renew)
        {
            StartOath(s, latest, now);
            // Logged as an acceptance: the Sub swore to the renewals along with the oath.
            Log(pairing, RulebookEventKind.OathAccepted, id, $"Oath \"{name}\" renewed: {OathText.Describe(latest)}.");
            return;
        }
        s.Status = OathStatus.Offered;
        s.OfferedUnixSeconds = now;
        s.ChangedUnixSeconds = now;
        s.OfferedTermsJson = RulebookJson.Serialize(latest);
        Log(pairing, RulebookEventKind.OathWithdrawn, id, $"Oath \"{name}\" offered again.");
        Notify("Oath", $"\"{name}\" is offered to you again. Review it in Rulebook.");
    }

    /// Lets the current run be the last; it's still judged as usual.
    public void StopRenewing(PairingState pairing, string oathId)
    {
        if (!pairing.Rulebook.Oaths.TryGetValue(oathId, out var s) || s.Status != OathStatus.Open || s.StopRenewing)
            return;
        s.StopRenewing = true;
        Log(pairing, RulebookEventKind.RitualDone, oathId, $"Oath \"{OathName(pairing, oathId)}\" won't renew after this run.");
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
                    // A strike re-arms only once the Sub is seen outside the window again.
                    s.CurfewArmed = false;
                    Violate(pairing, id, s, terms, "logged in during curfew");
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
                CloseMissedPeriods(pairing, id, s, terms, now);
                var over = s.ScopeEndsUnixSeconds is { } end && now >= end;
                if (terms.Condition == OathCondition.CheckIn && s.PeriodCount == 0 && !over)
                    CountRitual(pairing, id, s, terms, "checked in");
            }
            if (terms.Scope == OathScope.ForATime && s.ScopeEndsUnixSeconds is { } ends && now >= ends)
            {
                if (s.MissedPeriods > 0)
                    ResolveOath(pairing, id, s, OathStatus.Broken, $"missed {s.MissedPeriods} {(s.MissedPeriods == 1 ? "time" : "times")}");
                else
                    ResolveOath(pairing, id, s, OathStatus.Kept, "the time ran out");
            }
        }
    }

    public static int RitualNeeded(Oath terms) => terms.Condition == OathCondition.CheckIn ? 1 : terms.TimesPerPeriod;

    /// Walks every period that has ended since the last tick, up to the oath's own end; a short period is
    /// recorded as missed and scored, and the oath keeps running. The final period ends together with the oath,
    /// so it's judged before the oath resolves.
    private void CloseMissedPeriods(PairingState pairing, string id, OathState s, Oath terms, long now)
    {
        var length = Math.Max(1, terms.PeriodDays) * 86400L;
        var needed = RitualNeeded(terms);
        var name = string.IsNullOrWhiteSpace(terms.Name) ? "oath" : terms.Name;
        while (now >= s.PeriodStartUnixSeconds + length &&
               (s.ScopeEndsUnixSeconds is not { } ends || s.PeriodStartUnixSeconds + length <= ends))
        {
            if (s.PeriodCount < needed)
            {
                var missed = terms.Condition switch
                {
                    OathCondition.GreetOwner => "didn't greet their Owner in time",
                    OathCondition.MessageOwner => "didn't message their Owner in time",
                    OathCondition.DutyQuota => "didn't complete enough duties in time",
                    _ => "didn't log in in time",
                };
                s.MissedPeriods++;
                s.Streak = 0;
                Log(pairing, RulebookEventKind.RitualDone, id, $"\"{name}\": {missed} ({s.PeriodCount}/{needed} this time).");
                if (terms.LedgerPerMissed != 0)
                    ChangeLedger(pairing, (needed - s.PeriodCount) * terms.LedgerPerMissed, $"missed \"{name}\"", notify: true);
                else
                    Notify("Oath", $"You missed \"{name}\" this time.", NotificationType.Warning);
            }
            s.PeriodStartUnixSeconds += length;
            s.PeriodCount = 0;
        }
    }

    private void CountRitual(PairingState pairing, string id, OathState s, Oath terms, string what)
    {
        var needed = RitualNeeded(terms);
        if (s.PeriodCount >= needed)
            return;
        s.PeriodCount++;
        var name = string.IsNullOrWhiteSpace(terms.Name) ? "oath" : terms.Name;
        Log(pairing, RulebookEventKind.RitualDone, id, $"\"{name}\": {what} ({s.PeriodCount}/{needed} this time).");
        if (terms.LedgerPerDone != 0)
            ChangeLedger(pairing, terms.LedgerPerDone, $"\"{name}\"", notify: false);
        var ledger = terms.LedgerPerDone == 0 ? "" : $" ({(terms.LedgerPerDone > 0 ? "+" : "")}{terms.LedgerPerDone} ledger)";
        if (s.PeriodCount == needed)
            Notify("Oath", $"\"{name}\" done for now{ledger}.", NotificationType.Success);
        else if (ledger.Length > 0)
            Notify("Oath", $"\"{name}\": {s.PeriodCount}/{needed} done{ledger}.");
        if (s.PeriodCount == needed)
            CountStreak(pairing, id, s, terms);
        config.Save();
    }

    private void OnOwnEmote(uint emoteId, ulong targetObjectId)
    {
        foreach (var pairing in SubPairings().Where(IsRunning))
        {
            if (FindOwner(pairing) is not { } owner || owner.GameObjectId != targetObjectId)
                continue;
            foreach (var (id, s, terms) in OpenOaths(pairing))
                if (terms.Condition == OathCondition.GreetOwner && terms.EmoteId == emoteId && performing != (pairing.Id, id))
                    CountRitual(pairing, id, s, terms, $"greeted {pairing.PeerName} with {OathText.EmoteName(emoteId)}");
        }
    }

    public bool IsPerforming(PairingState pairing, string oathId) => performing == (pairing.Id, oathId);

    public double? PerformSecondsLeft => performing is null ? null : gesture.HoldSecondsLeft;

    /// Null when Perform can be used right now, otherwise why not.
    public string? PerformBlocker(PairingState pairing, string oathId)
    {
        if (!IsRunning(pairing))
            return "Your rulebooks aren't running.";
        if (performing is not null)
            return "You're already performing.";
        if (!pairing.Rulebook.Oaths.TryGetValue(oathId, out var s) || s.Status != OathStatus.Open || s.Terms is not { Condition: OathCondition.GreetOwner } terms)
            return "This oath isn't open.";
        if (s.PeriodCount >= RitualNeeded(terms))
            return "Done for now.";
        if (string.IsNullOrEmpty(terms.AnimationId) && !OathText.EmoteName(terms.EmoteId).StartsWith('/'))
            return "This gesture can only be done by hand.";
        if (FindOwner(pairing) is not { } owner || Plugin.TargetManager.Target?.GameObjectId != owner.GameObjectId)
            return $"Target {pairing.PeerName} first.";
        return null;
    }

    /// The Sub's own button: plays the oath's gesture or animation at the Owner and holds them for the oath's time.
    /// Returns why it couldn't start, or null.
    public string? Perform(PairingState pairing, string oathId)
    {
        if (PerformBlocker(pairing, oathId) is { } blocked)
            return blocked;
        var terms = pairing.Rulebook.Oaths[oathId].Terms!;
        var pairingId = pairing.Id;
        performing = (pairingId, oathId);

        void Ended(bool completed)
        {
            performing = null;
            var name = string.IsNullOrWhiteSpace(terms.Name) ? "oath" : terms.Name;
            if (!completed)
            {
                Notify("Oath", $"\"{name}\" stopped before the hold ended, so it didn't count.", NotificationType.Warning);
                return;
            }
            if (config.FindPairingById(pairingId) is not { } current || !IsRunning(current) ||
                !current.Rulebook.Oaths.TryGetValue(oathId, out var st) || st.Status != OathStatus.Open || st.Terms is not { } now)
                return;
            CountRitual(current, oathId, st, now, $"greeted {current.PeerName} with {OathText.Greeting(now)}, held {OathText.Hold(now)}");
        }

        if (!string.IsNullOrEmpty(terms.AnimationId))
        {
            var result = gesture.PlayHeld(terms.AnimationId, pairingId, terms.HoldSeconds, Ended);
            if (result.Success)
                return null;
            performing = null;
            return result.Status == Commands.GestureCommand.ApplyStatus.Missing
                ? "That animation isn't in your animation list. Rescan your animations, or ask your Owner to pick it again."
                : "The animation couldn't play (Penumbra didn't take it).";
        }

        var command = OathText.EmoteName(terms.EmoteId).TrimStart('/');
        gesture.PlayHeld(new GestureTrigger { Kind = GestureTriggerKind.SlashCommand, SlashCommand = command }, pairingId, terms.HoldSeconds, Ended);
        return null;
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
        if (kind == Dalamud.Game.Text.XivChatType.TellIncoming)
        {
            OnIncomingTell(message);
            return;
        }
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
                        Violate(pairing, id, s, terms, $"sent {pairing.PeerName} a tell without calling them \"{phrase}\"");
                        break;
                    case OathCondition.ForbiddenWord when phrase.Length > 0 && text.Contains(phrase, StringComparison.OrdinalIgnoreCase):
                        Violate(pairing, id, s, terms, $"said \"{phrase}\"");
                        break;
                    case OathCondition.QuietInPublic when Array.IndexOf(PublicChannels, kind) >= 0:
                        Violate(pairing, id, s, terms, "spoke in public chat");
                        break;
                }
            }
        }
    }

    /// Only the Owner's own tell, by name and home world, grants leave.
    private void OnIncomingTell(Dalamud.Game.Chat.IChatMessage message)
    {
        var (name, world) = SenderOf(message.Sender);
        if (name is null)
            return;
        var text = message.Message.TextValue;
        foreach (var pairing in SubPairings().Where(IsRunning))
        {
            if (!string.Equals(name, pairing.PeerName, StringComparison.OrdinalIgnoreCase)
                || (world is not null && !string.Equals(world, pairing.PeerWorld, StringComparison.OrdinalIgnoreCase)))
                continue;
            foreach (var (_, s, terms) in OpenOaths(pairing))
            {
                var phrase = terms.Phrase.Trim();
                if (terms.Condition != OathCondition.AskBeforeLogoff || phrase.Length == 0 || !text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    continue;
                s.LeaveGrantedUntilUnixSeconds = Now() + terms.LeaveWindowMinutes * 60L;
                var until = DateTimeOffset.FromUnixTimeSeconds(s.LeaveGrantedUntilUnixSeconds).ToLocalTime();
                Notify("Leave granted", $"{pairing.PeerName} lets you log off until {until:HH:mm}.", NotificationType.Success);
                config.Save();
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
            {
                if (terms.Condition == OathCondition.SayGoodnight && s.LastMatchUnixSeconds < cutoff)
                    Violate(pairing, id, s, terms, $"logged off without telling {pairing.PeerName} goodnight");
                else if (terms.Condition == OathCondition.AskBeforeLogoff && Now() >= s.LeaveGrantedUntilUnixSeconds)
                    Violate(pairing, id, s, terms, $"logged off without {pairing.PeerName}'s leave");
            }
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
        CheckJobLocks();
    }

    private void OnDutyWiped()
    {
        foreach (var pairing in SubPairings().Where(IsRunning))
        {
            foreach (var (id, s, terms) in OpenOaths(pairing))
                if (terms.Condition == OathCondition.NoWipes && Watching(s, terms))
                    Violate(pairing, id, s, terms, "the party wiped");
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
                if (terms.Condition == OathCondition.DutyQuota && (terms.Places.Count == 0 || RulebookPlaces.MatchesAny(terms.Places, events.CurrentTerritory))
                    && !(s.ScopeEndsUnixSeconds is { } ends && now >= ends))
                    CountRitual(pairing, id, s, terms, $"completed {RulebookPlaces.TerritoryName(events.CurrentTerritory)}");
                if (terms.Scope == OathScope.NextDuty)
                    ResolveOath(pairing, id, s, OathStatus.Kept, "the duty was completed");
            }
        }
    }

    private void OnJobChanged(uint job)
    {
        if (events.InStartedDuty)
            CheckJobLocks();
    }

    /// Checked at duty start and on a job change inside the duty.
    private void CheckJobLocks()
    {
        var job = events.CurrentJob;
        foreach (var pairing in SubPairings().Where(IsRunning))
            foreach (var (id, s, terms) in OpenOaths(pairing))
                if (terms.Condition == OathCondition.JobLock && Watching(s, terms) && !Jobs.IsAllowed(terms.JobIds, job))
                    Violate(pairing, id, s, terms, $"entered a duty as {Jobs.Name(job)}");
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
                    Violate(pairing, id, s, terms, "your character died");
            if (pairing.Rulebook.Accepted?.DrawOn.Death == true)
                DrawForEvent(pairing, CardPile.Punishment, "your character dying");
        }
    }

    // ---- Places ----

    private void OnTerritoryEntered(uint from, uint to)
    {
        foreach (var pairing in SubPairings().Where(IsRunning))
        {
            if (from == 0)
                foreach (var rule in pairing.Rulebook.Accepted!.Times.Where(t => t.Trigger == TimeTrigger.OnLogin))
                    Fire(pairing, rule.Id, $"{RuleName(rule.Name, "Time rule")} (logged in)", rule.Consequence, rule.Id, rule.CooldownSeconds);

            foreach (var (id, s, terms) in OpenOaths(pairing))
            {
                if (!Watching(s, terms) || from == 0)
                    continue;
                if (terms.Condition == OathCondition.StayInPlaces && !RulebookPlaces.MatchesAny(terms.Places, to))
                    Violate(pairing, id, s, terms, $"entered {RulebookPlaces.TerritoryName(to)}");
                else if (terms.Condition == OathCondition.AvoidPlaces && RulebookPlaces.MatchesAny(terms.Places, to))
                    Violate(pairing, id, s, terms, $"entered {RulebookPlaces.TerritoryName(to)}");
            }

            foreach (var rule in pairing.Rulebook.Accepted!.Places)
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

    private static string RuleName(string name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name;

    // ---- Time rules ----

    /// Fires once per chosen day, when logged in within the window after its time; a missed day isn't made up.
    private void TickTimeRules(PairingState pairing)
    {
        if (pairing.Rulebook.Accepted is not { Times.Count: > 0 } doc || Plugin.ObjectTable.LocalPlayer is null)
            return;
        var local = DateTime.Now;
        var minute = local.Hour * 60 + local.Minute;
        var today = local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var lastFired = pairing.Rulebook.TimeRuleLastFired;
        foreach (var rule in doc.Times)
        {
            if (rule.Trigger != TimeTrigger.AtTime || !TimeRules.On(rule.Weekdays, local.DayOfWeek)
                || minute < rule.Minutes || minute >= rule.Minutes + RulebookLimits.TimeRuleWindowMinutes
                || (lastFired.TryGetValue(rule.Id, out var date) && date == today))
                continue;
            lastFired[rule.Id] = today;
            Fire(pairing, rule.Id, $"{RuleName(rule.Name, "Time rule")} ({OathText.Clock(rule.Minutes)})", rule.Consequence, rule.Id, rule.CooldownSeconds);
            config.Save();
        }
    }

    // ---- Shop ----

    /// Null when the item can be bought now, otherwise why not.
    public string? BuyBlocker(PairingState pairing, ShopItem item)
    {
        if (!IsRunning(pairing))
            return "Your rulebook isn't running.";
        var state = pairing.Rulebook;
        if (state.LedgerScore < item.Price)
            return $"{item.Price - state.LedgerScore} more points needed.";
        if (ShopCooldownLeft(pairing, item) is { } left)
            return $"Available again in {RestraintLock.Format(left)}.";
        if (item.DrawReward && !HasCards(pairing, CardPile.Reward))
            return "There are no reward cards left.";
        return null;
    }

    public TimeSpan? ShopCooldownLeft(PairingState pairing, ShopItem item)
    {
        if (item.CooldownSeconds <= 0 || !pairing.Rulebook.ShopLastBought.TryGetValue(item.Id, out var last))
            return null;
        var left = last + item.CooldownSeconds - Now();
        return left > 0 ? TimeSpan.FromSeconds(left) : null;
    }

    private static bool HasCards(PairingState pairing, CardPile pile) =>
        pairing.Rulebook.Accepted?.Deck.Any(c => c.Pile == pile && !pairing.Rulebook.RemovedOnceCards.Contains(c.Id)) == true;

    /// The Sub's own Buy click is the only caller; nothing that arrives by chat or runs from a rule reaches this.
    /// Every check happens before the points are taken.
    public string? Buy(PairingState pairing, string itemId)
    {
        if (pairing.Rulebook.Accepted?.Shop.FirstOrDefault(i => i.Id == itemId) is not { } item)
            return "That item isn't in the shop any more.";
        if (BuyBlocker(pairing, item) is { } blocked)
            return blocked;
        var name = RuleName(item.Name, "Shop item");
        pairing.Rulebook.ShopLastBought[item.Id] = Now();
        ChangeLedger(pairing, -item.Price, $"bought \"{name}\"", notify: false);
        if (item.DrawReward)
            Draw(pairing, CardPile.Reward, $"buying \"{name}\"");
        else
            Fire(pairing, item.Id, $"Bought \"{name}\"", item.Consequence, null, 0);
        Notify("Bought", $"\"{name}\" for {item.Price} points.", NotificationType.Success);
        config.SaveNow();
        return null;
    }

    // ---- Presence ----

    private void TickPresence(PairingState pairing, DateTime now)
    {
        var rules = pairing.Rulebook.Accepted!.Presence;
        if (rules.Count == 0)
            return;
        var distance = OwnerDistance(pairing);
        var inDuty = RulebookPlaces.IsDuty(Plugin.ClientState.TerritoryType);
        foreach (var rule in rules)
        {
            var key = (pairing.Id, rule.Id);
            if (!presence.TryGetValue(key, out var p))
                presence[key] = p = new PresenceState();
            if (rule.SkipInDuties && inDuty)
            {
                // Forgotten rather than paused, so being together once the duty ends counts as a fresh arrival.
                presence[key] = new PresenceState();
                continue;
            }
            var inRange = distance is { } d && d <= rule.RangeYalms;
            var margin = Math.Max(MinDepartMarginYalms, rule.RangeYalms * DepartMarginFraction);
            var gone = distance is not { } far || far > rule.RangeYalms + margin;
            var name = string.IsNullOrWhiteSpace(rule.Name) ? "Presence rule" : rule.Name;
            if (inRange)
            {
                p.OutOfRangeSince = null;
                p.InRangeSince ??= now;
                if (!p.Arrived && now - p.InRangeSince >= ArriveAfter)
                {
                    p.Arrived = true;
                    var label = $"{name} ({pairing.PeerName} arrived)";
                    Fire(pairing, rule.Id, label, rule.Arrive, rule.Id + ":arrive", rule.CooldownSeconds,
                        rule.LeashOnArrive || !string.IsNullOrWhiteSpace(rule.ArriveTell) ? () => PresenceExtras(pairing, rule, arrived: true, label) : null);
                }
            }
            else
            {
                p.InRangeSince = null;
                if (!p.Arrived)
                    continue;
                if (!gone)
                {
                    p.OutOfRangeSince = null;
                    continue;
                }
                p.OutOfRangeSince ??= now;
                if (now - p.OutOfRangeSince >= DepartAfter)
                {
                    p.Arrived = false;
                    p.OutOfRangeSince = null;
                    var label = $"{name} ({pairing.PeerName} left)";
                    Fire(pairing, rule.Id, label, rule.Depart, rule.Id + ":depart", rule.CooldownSeconds,
                        rule.UnleashOnDepart || !string.IsNullOrWhiteSpace(rule.DepartTell) ? () => PresenceExtras(pairing, rule, arrived: false, label) : null);
                }
            }
        }
    }

    /// Judged only while the Owner is loaded nearby; anywhere else there's nothing to stay beside.
    private void TickSideOaths(PairingState pairing)
    {
        var distance = OwnerDistance(pairing);
        var inDuty = RulebookPlaces.IsDuty(Plugin.ClientState.TerritoryType);
        var now = Now();
        foreach (var (id, s, terms) in OpenOaths(pairing))
        {
            if (terms.Condition != OathCondition.StayAtSide || !Watching(s, terms))
                continue;
            if (distance is not { } d || !events.Settled || (terms.SkipInDuties && inDuty) || d <= terms.RangeYalms)
            {
                s.GraceEndsUnixSeconds = null;
                continue;
            }
            var name = string.IsNullOrWhiteSpace(terms.Name) ? "oath" : terms.Name;
            if (s.GraceEndsUnixSeconds is not { } graceEnds)
            {
                s.GraceEndsUnixSeconds = now + terms.GraceSeconds;
                Notify("Oath", $"\"{name}\": get back within {terms.RangeYalms:0} yalms of {pairing.PeerName} within {OathText.Grace(terms)}.", NotificationType.Warning);
            }
            else if (now >= graceEnds)
            {
                s.GraceEndsUnixSeconds = null;
                Violate(pairing, id, s, terms, $"strayed from {pairing.PeerName}");
            }
        }
    }

    // ---- Keep it on ----

    /// Remembers the slots the Owner's outfit set, read back once Glamourer has applied it.
    private void OnOwnerDesignApplied(Guid pairingId, Guid designId, string designName)
    {
        if (config.FindPairingById(pairingId) is not { Direction: PairingDirection.SubSide } pairing)
            return;
        var slots = glamourer.GetDesignEquipSlots(designId);
        Plugin.Framework.RunOnTick(() =>
        {
            var snapshot = new OwnerOutfitSnapshot { DesignId = designId, DesignName = designName };
            foreach (var slot in slots)
                if (glamourer.GetEquipSlotValue(slot) is { } value)
                    snapshot.Slots[(int)slot] = value.ItemId;
            pairing.Rulebook.OwnerOutfit = snapshot;
            foreach (var (_, s, _) in OpenOaths(pairing))
                s.GraceEndsUnixSeconds = null;
            config.Save();
        }, OutfitSettleDelay);
    }

    private static readonly TimeSpan OutfitSettleDelay = TimeSpan.FromSeconds(2);

    /// Panic and revert all take the outfit off on purpose; until the Owner sends another there's nothing to keep on.
    private void ForgetOwnerOutfits()
    {
        foreach (var pairing in SubPairings())
        {
            pairing.Rulebook.OwnerOutfit = null;
            foreach (var (_, s, _) in OpenOaths(pairing))
                s.GraceEndsUnixSeconds = null;
        }
        config.Save();
    }

    /// Slots another lock holds (a restraint, the collar) are Oathbound's own doing, not the Sub's.
    private List<Glamourer.Api.Enums.ApiEquipSlot> OutfitSlotsOff(OwnerOutfitSnapshot snapshot)
    {
        var off = new List<Glamourer.Api.Enums.ApiEquipSlot>();
        foreach (var (slotNumber, itemId) in snapshot.Slots)
        {
            var slot = (Glamourer.Api.Enums.ApiEquipSlot)slotNumber;
            if (slotLocks.LockOwner(slot) is { } owner && owner != Commands.OutfitCommand.SlotLockOwner)
                continue;
            if (glamourer.GetEquipSlotValue(slot) is { } current && current.ItemId != itemId)
                off.Add(slot);
        }
        return off;
    }

    private void TickKeepItOn(PairingState pairing)
    {
        if (pairing.Rulebook.OwnerOutfit is not { } snapshot)
            return;
        List<Glamourer.Api.Enums.ApiEquipSlot>? off = null;
        var now = Now();
        foreach (var (id, s, terms) in OpenOaths(pairing))
        {
            if (terms.Condition != OathCondition.KeepItOn || !Watching(s, terms) || !events.Settled)
                continue;
            off ??= OutfitSlotsOff(snapshot);
            if (off.Count == 0)
            {
                s.GraceEndsUnixSeconds = null;
                continue;
            }
            var name = string.IsNullOrWhiteSpace(terms.Name) ? "oath" : terms.Name;
            if (s.GraceEndsUnixSeconds is not { } graceEnds)
            {
                s.GraceEndsUnixSeconds = now + terms.GraceSeconds;
                Notify("Oath", $"\"{name}\": {snapshot.DesignName} came off. Put it back on within {OathText.Grace(terms)} (Rulebook > Your oaths).", NotificationType.Warning);
                config.Save();
            }
            else if (now >= graceEnds)
            {
                s.GraceEndsUnixSeconds = null;
                Violate(pairing, id, s, terms, $"took off {snapshot.DesignName}");
            }
        }
    }

    /// Null when the outfit could be applied again, otherwise why not.
    public string? PutBackOn(PairingState pairing)
    {
        if (pairing.Rulebook.OwnerOutfit is not { } snapshot)
            return "There's no outfit from your Owner to put back on.";
        return outfit.Reapply(snapshot.DesignId) ? null : "Glamourer couldn't apply it.";
    }

    /// Each part needs the Sub's own permission for it, exactly as if the Owner had sent it; a missing one is logged, not run.
    private void PresenceExtras(PairingState pairing, PresenceRule rule, bool arrived, string label)
    {
        if (arrived && rule.LeashOnArrive)
        {
            if (!config.Permissions.Follow)
                Log(pairing, RulebookEventKind.ConsequenceSkipped, rule.Id, $"{label}: skipped the leash - Follow permission is off.");
            else if (follow.LeashedPairingId == pairing.Id)
            {
                // Already on: nothing to tell the Owner.
            }
            else if (follow.Engage(pairing, Math.Clamp(rule.LeashLengthYalms, LengthOption.MinYalms, LengthOption.MaxYalms)))
            {
                Log(pairing, RulebookEventKind.ConsequenceRan, rule.Id, $"{label}: leashed to {pairing.PeerName} ({rule.LeashLengthYalms} yalms).");
                sender.Send(composer.ComposeLeashOnNotice(pairing.PeerName!, pairing.PeerWorld!));
            }
            else
                Log(pairing, RulebookEventKind.ConsequenceSkipped, rule.Id, $"{label}: the leash couldn't start.");
        }
        if (!arrived && rule.UnleashOnDepart && follow.LeashedPairingId == pairing.Id)
        {
            if (!config.Permissions.Follow)
                Log(pairing, RulebookEventKind.ConsequenceSkipped, rule.Id, $"{label}: skipped the unleash - Follow permission is off.");
            else
            {
                follow.Release(LeashEnd.Rule);
                Log(pairing, RulebookEventKind.ConsequenceRan, rule.Id, $"{label}: unleashed.");
            }
        }
        var text = (arrived ? rule.ArriveTell : rule.DepartTell).Trim();
        if (text.Length > 0)
        {
            if (!(config.Permissions.CustomChatMessages && config.CustomChatAcknowledged))
                Log(pairing, RulebookEventKind.ConsequenceSkipped, rule.Id, $"{label}: skipped the message - custom chat messages are off.");
            else if (sender.Send(composer.ComposePresenceTell(pairing.PeerName!, pairing.PeerWorld!, text)))
                Log(pairing, RulebookEventKind.ConsequenceRan, rule.Id, $"{label}: told {pairing.PeerName} \"{text}\".");
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
        if (pairing.Rulebook.Accepted is not { } doc)
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
        var cards = doc.Deck.Where(c => c.Pile == pile && !state.RemovedOnceCards.Contains(c.Id)).ToList();
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
                if (!state.ThresholdsMet.Add(t.Id))
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

    /// Owner's Grant leave button: a plain tell with the oath's grant word, only on that click.
    public void SendGrantLeave(PairingState pairing, string word)
    {
        if (pairing is { Direction: PairingDirection.OwnerSide, IsPaired: true, PeerName: { } name, PeerWorld: { } world } && word.Trim().Length > 0)
            sender.Send(composer.ComposePresenceTell(name, world, word.Trim()));
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
