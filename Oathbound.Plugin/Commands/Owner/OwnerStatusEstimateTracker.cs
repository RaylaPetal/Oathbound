using System;
using System.Collections.Generic;
using System.Linq;
using Glamourer.Api.Enums;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.Commands;

/// The Owner's estimate of each Sub's gagged/restrained/leashed state, built from the commands this client sent that
/// the Sub's client didn't refuse (see OwnerReceiptTracker), plus the leash, struggle and key notices that come back.
/// A short log of recent changes over a base snapshot lets a late refusal take its command out again. In-memory only;
/// never sends anything.
public sealed class OwnerStatusEstimateTracker : IDisposable
{
    /// Longer than any receipt can be late; older changes are folded into the base snapshot.
    private static readonly TimeSpan LogWindow = TimeSpan.FromMinutes(3);

    private sealed record LogEvent(DateTime AtUtc, long Seq, Action<OwnerStatusEstimate, DateTime> Apply);

    private sealed class PairLog
    {
        public OwnerStatusEstimate Base = new();
        public OwnerStatusEstimate Current = new();
        public readonly List<LogEvent> Events = new();
    }

    private readonly PluginConfig config;
    private readonly OwnerReceiptTracker receipts;
    private readonly Dictionary<Guid, PairLog> logs = new();

    public OwnerStatusEstimateTracker(PluginConfig config, OwnerReceiptTracker receipts)
    {
        this.config = config;
        this.receipts = receipts;
        receipts.CommandOutcome += OnOutcome;
    }

    /// Null if nothing is estimated active. Drops ended pairings and expired timed restraints.
    public OwnerStatusEstimate? For(PairingState pairing)
    {
        if (!logs.TryGetValue(pairing.Id, out var log))
            return null;
        if (!pairing.IsPaired || pairing.Direction != PairingDirection.OwnerSide)
        {
            logs.Remove(pairing.Id);
            return null;
        }
        var estimate = log.Current;
        estimate.DropExpired();
        return estimate.Any ? estimate : null;
    }

    /// Also returns an estimate that only has last-sent module commands or confirmation marks, for the Active banners.
    public OwnerStatusEstimate? ForBanner(PairingState pairing)
    {
        if (!pairing.IsPaired || pairing.Direction != PairingDirection.OwnerSide)
            return null;
        if (!logs.TryGetValue(pairing.Id, out var log))
            return null;
        log.Current.DropExpired();
        return log.Current;
    }

    /// One module's part of the estimate, from its Active banner.
    public void ClearModule(Guid pairingId, string module)
    {
        if (!logs.TryGetValue(pairingId, out var log))
            return;
        Record(log, 0, (e, _) => e.ClearModule(module));
        log.Current.Marks.Remove(module);
    }

    public OwnerStatusEstimate? ForActivePairing =>
        config.GetActivePairing() is { Direction: PairingDirection.OwnerSide } pairing ? For(pairing) : null;

    public void Clear(Guid pairingId) => logs.Remove(pairingId);

    /// The Sub's own presence rule leashed them; from here the leash travels with area changes like one the Owner sent.
    public void MarkLeashed(Guid pairingId) =>
        Record(LogFor(pairingId), 0, (e, at) =>
        {
            e.Leashed = true;
            e.LeashedAtUtc = at;
            e.PauseInDuties = false;
        });

    /// The Sub struggled free or used the key. Null `restraint` (an older Sub's notice) means everything is off.
    public void MarkUnrestrained(Guid pairingId, string? restraint = null)
    {
        if (!logs.TryGetValue(pairingId, out var log))
            return;
        Record(log, 0, (e, _) =>
        {
            if (restraint is null)
                e.ClearRestraints();
            else
                e.RemoveRestraint(restraint);
        });
    }

    /// Returns whether the estimate showed the Sub leashed until now.
    public bool MarkUnleashed(Guid pairingId)
    {
        if (!logs.TryGetValue(pairingId, out var log) || !log.Current.Leashed)
            return false;
        Record(log, 0, (e, _) => e.Leashed = false);
        return true;
    }

    private PairLog LogFor(Guid pairingId)
    {
        if (!logs.TryGetValue(pairingId, out var log))
            logs[pairingId] = log = new PairLog();
        return log;
    }

    private static void Record(PairLog log, long seq, Action<OwnerStatusEstimate, DateTime> apply)
    {
        var now = DateTime.UtcNow;
        log.Events.Add(new LogEvent(now, seq, apply));
        apply(log.Current, now);
        var cutoff = now - LogWindow;
        while (log.Events.Count > 0 && log.Events[0].AtUtc < cutoff)
        {
            log.Events[0].Apply(log.Base, log.Events[0].AtUtc);
            log.Events.RemoveAt(0);
        }
    }

    /// Takes a refused command's change back out by replaying everything else over the base snapshot.
    private static void Remove(PairLog log, long seq)
    {
        if (log.Events.RemoveAll(e => e.Seq == seq) == 0)
            return;
        var rebuilt = log.Base.Clone();
        foreach (var e in log.Events)
            e.Apply(rebuilt, e.AtUtc);
        rebuilt.CopyMarksFrom(log.Current);
        rebuilt.DropExpired();
        log.Current = rebuilt;
    }

    private void OnOutcome(CommandOutcome outcome)
    {
        if (config.FindPairingById(outcome.PairingId) is not { Direction: PairingDirection.OwnerSide, IsPaired: true } pairing) return;
        var body = outcome.Text.Trim();
        if (body.Length == 0) return;
        var log = LogFor(pairing.Id);
        var modules = ModulesOf(body);

        switch (outcome.Kind)
        {
            case CommandOutcomeKind.Waiting:
                log.Current.SetMark(modules, new CommandMark(outcome.Seq, CommandConfirmation.Waiting));
                return;
            case CommandOutcomeKind.Applied or CommandOutcomeKind.NotConfirmed or CommandOutcomeKind.Legacy when !outcome.Late:
                Record(log, outcome.Seq, (e, at) => Apply(e, body, at));
                log.Current.SetMark(modules, new CommandMark(outcome.Seq, outcome.Kind switch
                {
                    CommandOutcomeKind.Applied => CommandConfirmation.Confirmed,
                    CommandOutcomeKind.NotConfirmed => CommandConfirmation.NotConfirmed,
                    _ => CommandConfirmation.Legacy,
                }));
                CommandSent?.Invoke(body);
                return;
            case CommandOutcomeKind.Applied:
                log.Current.SetMark(modules, new CommandMark(outcome.Seq, CommandConfirmation.Confirmed), sameSeqOnly: true);
                return;
            case CommandOutcomeKind.Refused:
                if (outcome.Late)
                    Remove(log, outcome.Seq);
                log.Current.SetMark(modules, new CommandMark(outcome.Seq, CommandConfirmation.Refused, outcome.Code));
                return;
        }
    }

    /// The Active banner modules a command speaks for; empty for a bare alias or a bundle, whose effect only the Sub knows.
    private static string[] ModulesOf(string body)
    {
        var (word, rest) = SplitFirst(body);
        return word.ToLowerInvariant() switch
        {
            ControlWords.Leash when rest.StartsWith(CommandComposer.LeashTravelWord + " ", StringComparison.OrdinalIgnoreCase) => [],
            ControlWords.Leash or ControlWords.Unleash => ["follow"],
            "revert" => ["title", "outfit", "animation", "moodles", "restraints", "toycontrol", "follow"],
            "restraint" => ["restraints"],
            "title" => ["title"],
            "outfit" => ["outfit"],
            "gesture" => ["animation"],
            "moodle" => ["moodles"],
            "toy" => ["toycontrol"],
            "cage" => ["cage"],
            _ => [],
        };
    }

    /// A command the Sub's client applied (or didn't answer, or can't confirm), as sent.
    public event Action<string>? CommandSent;

    private void Apply(OwnerStatusEstimate estimate, string body, DateTime at)
    {
        var (word, rest) = SplitFirst(body);
        switch (word.ToLowerInvariant())
        {
            case ControlWords.Leash when rest.StartsWith(CommandComposer.LeashTravelWord + " ", StringComparison.OrdinalIgnoreCase):
                // Leash travel isn't a new leash, and may be addressed to a pairing other than the active one.
                break;
            case ControlWords.Leash:
                // 3 when bare.
                DutyPauseOption.Strip(LengthOption.Strip(MoodleOption.Strip(rest, out _), out var length), out var pauseInDuties);
                estimate.Leashed = true;
                estimate.PauseInDuties = pauseInDuties;
                estimate.LeashedAtUtc = at;
                estimate.LeashLength = length ?? LengthOption.DefaultYalms;
                break;
            case ControlWords.Unleash:
                estimate.Leashed = false;
                break;
            case "revert":
                if (rest.Equals("all", StringComparison.OrdinalIgnoreCase))
                    estimate.ClearAll();
                break;
            case "restraint":
                ApplyRestraint(estimate, rest, at);
                break;
            case "title":
                ApplyTitle(estimate, rest, at);
                break;
            case "outfit":
                ApplyOutfit(estimate, rest, at);
                break;
            case "gesture":
                var animation = Unquote(LockTimerOption.StripSeconds(rest, out _).Trim());
                estimate.LastAnimation = animation.Equals(CommandComposer.StopGestureWord, StringComparison.OrdinalIgnoreCase) ? null : animation;
                break;
            case "moodle":
                ApplyMoodle(estimate, rest, at);
                break;
            case "toy":
                estimate.LastToy = rest.Equals("stop", StringComparison.OrdinalIgnoreCase) ? null : rest.Trim();
                break;
            case "customtrigger":
                ApplyCustomTrigger(estimate, rest, at);
                break;
        }
    }

    private void ApplyRestraint(OwnerStatusEstimate estimate, string rest, DateTime at)
    {
        rest = MoodleOption.Strip(LockTimerOption.Strip(rest, out var restraintLock), out _).Trim();
        var (verb, remainder) = SplitFirst(rest);
        List<RestraintRuleAssignment>? rules = null;
        ApiEquipSlot? slot = null;
        string reference;
        switch (verb.ToLowerInvariant())
        {
            case "unlock":
                if (remainder.Length == 0)
                    estimate.ClearRestraints();
                else
                    estimate.RemoveRestraint(remainder);
                return;
            case "timer":
                if (RestraintCommand.TryParseTimerAdjust(remainder, out var delta))
                    estimate.ShiftRestraintTimers(delta, at);
                return;
            case "lock":
                if (!RestraintCommand.TryParseLockCommand(remainder, out reference, out rules)) return;
                rules ??= KnownRulesFor(reference);
                slot = KnownSlotFor(reference);
                break;
            case "catalog":
                if (!RestraintCommand.TryParseCatalogCommand(remainder, out _, out reference, out var catalogItemId, out rules)) return;
                slot = catalogItemId == 0 ? null : GlamourerIpc.GetItemSlot((uint)catalogItemId);
                break;
            case "wear":
                if (!RestraintCommand.TryParseWearCommand(remainder, out var wearSlot, out var wearItemId, out reference, out rules)) return;
                slot = wearSlot ?? (wearItemId is { } id ? GlamourerIpc.GetItemSlot((uint)id) : null);
                break;
            default:
                return;
        }
        estimate.MarkRestrained(reference, rules, restraintLock, at, slot);
    }

    /// When a `lockfor:` command's lock ends on the Sub, counted from now like the Sub counts it.
    private static DateTime? EndOf(int? lockSeconds, DateTime at) =>
        RestraintLock.FromSeconds(lockSeconds).Duration is { } duration ? at + duration : null;

    private static void ApplyTitle(OwnerStatusEstimate estimate, string rest, DateTime at)
    {
        var (verb, remainder) = SplitFirst(LockTimerOption.StripSeconds(rest, out var seconds));
        string? text = null;
        if (verb.Equals("clear", StringComparison.OrdinalIgnoreCase))
            estimate.LastTitle = null;
        else if (verb.Equals("create", StringComparison.OrdinalIgnoreCase) && remainder.Length > 0)
            text = Unquote(remainder);
        else if (verb.Equals("style", StringComparison.OrdinalIgnoreCase) && TitleCommand.TryParseStyleCommand(remainder, out var styled, out _, out _, out _))
            text = styled;
        if (text is null)
            return;
        estimate.LastTitle = text;
        estimate.TitleEndsAtUtc = EndOf(seconds, at);
    }

    private static void ApplyOutfit(OwnerStatusEstimate estimate, string rest, DateTime at)
    {
        var (verb, remainder) = SplitFirst(MoodleOption.Strip(LockTimerOption.StripSeconds(rest, out var seconds), out _));
        if (verb.Equals("unlock", StringComparison.OrdinalIgnoreCase))
        {
            estimate.LastOutfit = null;
            return;
        }
        if (remainder.Length == 0 || !(verb.Equals("lock", StringComparison.OrdinalIgnoreCase) || verb.Equals("wear", StringComparison.OrdinalIgnoreCase)))
            return;
        estimate.LastOutfit = Unquote(remainder);
        estimate.LastOutfitLocked = verb.Equals("lock", StringComparison.OrdinalIgnoreCase) || seconds is not null;
        estimate.OutfitEndsAtUtc = EndOf(seconds, at);
    }

    private static void ApplyMoodle(OwnerStatusEstimate estimate, string rest, DateTime at)
    {
        var (verb, remainder) = SplitFirst(LockTimerOption.StripSeconds(rest, out var seconds));
        if (verb.Equals("clear", StringComparison.OrdinalIgnoreCase))
            estimate.ClearModule("moodles");
        else if (verb.Equals("apply", StringComparison.OrdinalIgnoreCase) && Unquote(remainder) is { Length: > 0 } name)
            estimate.AddMoodle(name, EndOf(seconds, at));
        else if (verb.Equals(CustomMoodle.CustomWord, StringComparison.OrdinalIgnoreCase) && CustomMoodle.TryDecode(remainder, out var custom))
            estimate.AddMoodle(MoodlesTextFormat.StripMarkup(custom.Title), EndOf(seconds, at), custom.Id);
        else if (verb.Equals(CustomMoodle.RemoveWord, StringComparison.OrdinalIgnoreCase))
        {
            if (CustomMoodle.TryDecodeId(remainder, out var id))
                estimate.RemoveMoodle(id);
            else if (Unquote(remainder) is { Length: > 0 } removed)
                estimate.RemoveMoodle(removed);
        }
    }

    private static string Unquote(string text)
    {
        var t = text.Trim();
        if (CommandSelector.TryRead(t, out var selector, out _))
            return selector;
        return t.Trim('"');
    }

    /// Each part of a split bundle arrives separately. Bundles carry no leash action.
    private void ApplyCustomTrigger(OwnerStatusEstimate estimate, string rest, DateTime at)
    {
        rest = LockTimerOption.Strip(rest, out var restraintLock).TrimStart();
        var (verb, remainder) = SplitFirst(rest);
        if (!verb.Equals("cast", StringComparison.OrdinalIgnoreCase)) return;
        if (!CustomTriggerCommand.TryParseCastCommand(remainder, out _, out var actions)) return;

        foreach (var action in actions.Where(a => a.Kind == CustomTriggerActionKind.Restraint))
            estimate.MarkRestrained(action.RestraintDeviceName, action.RestraintRules ?? KnownRulesFor(action.RestraintDeviceName), restraintLock, at, KnownSlotFor(action.RestraintDeviceName));
    }

    /// Only known when a saved quick command of that name carries its item.
    private ApiEquipSlot? KnownSlotFor(string? deviceName) =>
        deviceName is null ? null : config.QuickCommands.Restraints
            .FirstOrDefault(c => c.RestraintItemId is > 0 && string.Equals(c.Label, deviceName, StringComparison.OrdinalIgnoreCase))
            ?.RestraintItemId is { } itemId ? GlamourerIpc.GetItemSlot((uint)itemId) : null;

    /// The Owner only knows the rules if a saved quick command of that name carries them.
    private List<RestraintRuleAssignment>? KnownRulesFor(string? deviceName) =>
        deviceName is null ? null : config.QuickCommands.Restraints
            .FirstOrDefault(c => c.RestraintRules is { Count: > 0 } && string.Equals(c.Label, deviceName, StringComparison.OrdinalIgnoreCase))
            ?.RestraintRules;

    private static (string Word, string Remainder) SplitFirst(string s)
    {
        var trimmed = s.Trim();
        var space = trimmed.IndexOf(' ');
        return space < 0 ? (trimmed, "") : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }

    public void Dispose() => receipts.CommandOutcome -= OnOutcome;
}

/// One restraint the Owner sent, named as their command named it. Re-sending it replaces its lock, as on the Sub.
public sealed class EstimatedRestraint
{
    public string Reference { get; internal set; } = "";
    /// Null when the Owner's client doesn't know which gear slot it uses.
    public ApiEquipSlot? Slot { get; internal set; }
    public bool Gagged { get; internal set; }
    public bool Blindfolded { get; internal set; }
    /// What its rules draw; None when the rules aren't known.
    public CuffSet Cuffs { get; internal set; }
    public bool Locked { get; internal set; }
    public DateTime? ExpiresAtUtc { get; internal set; }
    public bool HasKey { get; internal set; }
}

/// `CustomId` for a moodle the Owner wrote, which is removed by id.
public sealed record EstimatedMoodle(string Name, DateTime? EndsAtUtc, Guid? CustomId = null);

public enum CommandConfirmation
{
    /// Delivered; the Sub's receipt hasn't arrived yet. Not in the estimate yet.
    Waiting,
    /// The Sub's client said it applied.
    Confirmed,
    /// No receipt in time; in the estimate until a late receipt says otherwise.
    NotConfirmed,
    /// The Sub's version doesn't confirm commands; in the estimate as before receipts.
    Legacy,
    /// The Sub's client refused it; the estimate keeps what it showed before.
    Refused,
}

/// The latest command for one Active banner module and where its receipt stands.
public sealed record CommandMark(long Seq, CommandConfirmation State, RefusalCode Code = RefusalCode.Other);

public sealed class OwnerStatusEstimate
{
    private readonly List<EstimatedRestraint> restraints = new();

    /// Per Active banner module ("title", "follow", ...): the latest command's confirmation.
    public Dictionary<string, CommandMark> Marks { get; } = new();

    public CommandMark? MarkFor(string module) => Marks.GetValueOrDefault(module);

    /// An older command's answer never replaces a newer command's mark. `sameSeqOnly` updates only that command's own.
    internal void SetMark(IEnumerable<string> modules, CommandMark mark, bool sameSeqOnly = false)
    {
        foreach (var module in modules)
        {
            var current = Marks.GetValueOrDefault(module);
            if (current is null ? !sameSeqOnly : sameSeqOnly ? current.Seq == mark.Seq : current.Seq <= mark.Seq)
                Marks[module] = mark;
        }
    }

    internal void CopyMarksFrom(OwnerStatusEstimate other)
    {
        Marks.Clear();
        foreach (var (module, mark) in other.Marks)
            Marks[module] = mark;
    }

    internal OwnerStatusEstimate Clone()
    {
        var copy = new OwnerStatusEstimate
        {
            Leashed = Leashed,
            LastTitle = LastTitle,
            TitleEndsAtUtc = TitleEndsAtUtc,
            LastOutfit = LastOutfit,
            LastOutfitLocked = LastOutfitLocked,
            OutfitEndsAtUtc = OutfitEndsAtUtc,
            LastAnimation = LastAnimation,
            LastToy = LastToy,
            LeashLength = LeashLength,
            LeashedAtUtc = LeashedAtUtc,
            PauseInDuties = PauseInDuties,
        };
        copy.moodles.AddRange(moodles);
        copy.restraints.AddRange(restraints.Select(r => new EstimatedRestraint
        {
            Reference = r.Reference,
            Slot = r.Slot,
            Gagged = r.Gagged,
            Blindfolded = r.Blindfolded,
            Cuffs = r.Cuffs,
            Locked = r.Locked,
            ExpiresAtUtc = r.ExpiresAtUtc,
            HasKey = r.HasKey,
        }));
        copy.CopyMarksFrom(this);
        return copy;
    }

    public IReadOnlyList<EstimatedRestraint> Restraints => restraints;
    public bool Gagged => restraints.Any(r => r.Gagged);
    public bool Blindfolded => restraints.Any(r => r.Blindfolded);
    public bool Restrained => restraints.Count > 0;
    public bool Leashed { get; internal set; }

    /// What this client last sent per module, for the Active banners. Not part of the icon states.
    public string? LastTitle { get; internal set; }
    /// When a timed title, outfit or moodle comes off on the Sub; null for one that stays until cleared.
    public DateTime? TitleEndsAtUtc { get; internal set; }
    public string? LastOutfit { get; internal set; }
    public bool LastOutfitLocked { get; internal set; }
    public DateTime? OutfitEndsAtUtc { get; internal set; }
    public string? LastAnimation { get; internal set; }
    public string? LastToy { get; internal set; }
    private readonly List<EstimatedMoodle> moodles = new();
    public IReadOnlyList<EstimatedMoodle> Moodles => moodles;

    internal void AddMoodle(string name, DateTime? endsAtUtc, Guid? customId = null)
    {
        RemoveMoodle(name);
        moodles.Add(new EstimatedMoodle(name, endsAtUtc, customId));
    }

    internal void RemoveMoodle(string name) => moodles.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    internal void RemoveMoodle(Guid customId) => moodles.RemoveAll(m => m.CustomId == customId);

    /// Timed titles, outfits, moodles and restraints come off on the Sub by themselves, so they leave the estimate too.
    internal void DropExpired()
    {
        var now = DateTime.UtcNow;
        restraints.RemoveAll(r => r.ExpiresAtUtc is { } end && now >= end);
        moodles.RemoveAll(m => m.EndsAtUtc is { } end && now >= end);
        if (TitleEndsAtUtc is { } titleEnd && now >= titleEnd)
        {
            LastTitle = null;
            TitleEndsAtUtc = null;
        }
        if (OutfitEndsAtUtc is { } outfitEnd && now >= outfitEnd)
        {
            LastOutfit = null;
            OutfitEndsAtUtc = null;
        }
    }

    internal void ClearModule(string module)
    {
        switch (module)
        {
            case "title": LastTitle = null; break;
            case "outfit": LastOutfit = null; break;
            case "animation": LastAnimation = null; break;
            case "moodles": moodles.Clear(); break;
            case "toycontrol": LastToy = null; break;
            case "restraints": ClearRestraints(); break;
            case "follow": Leashed = false; break;
        }
    }
    public float LeashLength { get; internal set; } = LengthOption.DefaultYalms;
    /// A leash only starts with both in the same place, so one started after the Owner moved needs no travel.
    public DateTime LeashedAtUtc { get; internal set; }
    /// The leash was sent with `duty:pause`, so the Sub's client pauses it in duties.
    public bool PauseInDuties { get; internal set; }
    /// What the sent restraints' rules draw; None when the rules aren't known.
    public CuffSet Cuffs => restraints.Aggregate(CuffSet.None, (set, r) => set | r.Cuffs);

    public bool Any => Gagged || Restrained || Leashed;

    /// The Sub's client refuses a second restraint on a taken slot, so one sent for a known taken slot isn't added.
    internal void MarkRestrained(string reference, List<RestraintRuleAssignment>? rules, RestraintLock restraintLock, DateTime at, ApiEquipSlot? slot = null)
    {
        if (slot is not null && restraints.Any(r => r.Slot == slot && !string.Equals(r.Reference, reference.Trim(), StringComparison.OrdinalIgnoreCase)))
            return;
        RemoveRestraint(reference);
        restraints.Add(new EstimatedRestraint
        {
            Reference = reference.Trim(),
            Slot = slot,
            Gagged = rules?.Any(r => r.Kind == RestraintRuleKind.Gagged) == true,
            Blindfolded = rules?.Any(r => r.Kind == RestraintRuleKind.Blindfolded) == true,
            Cuffs = CuffSets.Drawn(rules),
            Locked = true,
            ExpiresAtUtc = restraintLock.Duration is { } duration ? at + duration : null,
            HasKey = restraintLock.Key is not null,
        });
    }

    internal void RemoveRestraint(string reference) =>
        restraints.RemoveAll(r => string.Equals(r.Reference, reference.Trim(), StringComparison.OrdinalIgnoreCase));


    /// Mirrors the Sub's `restraint timer`: every timed lock moves, capped at the longest lock; one moved past now is off.
    internal void ShiftRestraintTimers(int deltaSeconds, DateTime at)
    {
        var max = at + RestraintLock.MaxDuration;
        foreach (var r in restraints.Where(r => r.ExpiresAtUtc is not null))
        {
            var shifted = r.ExpiresAtUtc!.Value.AddSeconds(deltaSeconds);
            r.ExpiresAtUtc = shifted > max ? max : shifted;
        }
        DropExpired();
    }

    internal void ClearRestraints() => restraints.Clear();

    internal void ClearAll()
    {
        ClearRestraints();
        Leashed = false;
        LastTitle = null;
        LastOutfit = null;
        LastAnimation = null;
        LastToy = null;
        moodles.Clear();
    }
}
