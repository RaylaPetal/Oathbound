using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// The Owner's estimate of each Sub's gagged/restrained/leashed state, built from the commands this client sent.
/// The only state that comes back is the Sub's leash-off notice. In-memory only; never sends anything.
public sealed class OwnerStatusEstimateTracker : IDisposable
{
    private readonly PluginConfig config;
    private readonly ChatSender sender;
    private readonly Dictionary<Guid, OwnerStatusEstimate> estimates = new();

    public OwnerStatusEstimateTracker(PluginConfig config, ChatSender sender)
    {
        this.config = config;
        this.sender = sender;
        sender.Sent += OnSent;
    }

    /// Null if nothing is estimated active. Drops ended pairings and expired timed restraints.
    public OwnerStatusEstimate? For(PairingState pairing)
    {
        if (!estimates.TryGetValue(pairing.Id, out var estimate))
            return null;
        if (!pairing.IsPaired || pairing.Direction != PairingDirection.OwnerSide)
        {
            estimates.Remove(pairing.Id);
            return null;
        }
        estimate.DropExpired();
        return estimate.Any ? estimate : null;
    }

    /// Also returns an estimate that only has last-sent module commands (no icon state), for the Active banners.
    public OwnerStatusEstimate? ForBanner(PairingState pairing)
    {
        if (!pairing.IsPaired || pairing.Direction != PairingDirection.OwnerSide)
            return null;
        if (!estimates.TryGetValue(pairing.Id, out var estimate))
            return null;
        estimate.DropExpired();
        return estimate;
    }

    /// One module's part of the estimate, from its Active banner.
    public void ClearModule(Guid pairingId, string module)
    {
        if (estimates.TryGetValue(pairingId, out var estimate))
            estimate.ClearModule(module);
    }

    public OwnerStatusEstimate? ForActivePairing =>
        config.GetActivePairing() is { Direction: PairingDirection.OwnerSide } pairing ? For(pairing) : null;

    public void Clear(Guid pairingId) => estimates.Remove(pairingId);

    /// The Sub's own presence rule leashed them; from here the leash travels with area changes like one the Owner sent.
    public void MarkLeashed(Guid pairingId)
    {
        if (!estimates.TryGetValue(pairingId, out var estimate))
            estimates[pairingId] = estimate = new OwnerStatusEstimate();
        estimate.Leashed = true;
        estimate.LeashedAtUtc = DateTime.UtcNow;
        estimate.PauseInDuties = false;
    }

    /// The Sub struggled free or used the key. Null `restraint` (an older Sub's notice) means everything is off.
    public void MarkUnrestrained(Guid pairingId, string? restraint = null)
    {
        if (!estimates.TryGetValue(pairingId, out var estimate))
            return;
        if (restraint is null)
            estimate.ClearRestraints();
        else
            estimate.RemoveRestraint(restraint);
    }

    /// Returns whether the estimate showed the Sub leashed until now.
    public bool MarkUnleashed(Guid pairingId)
    {
        if (!estimates.TryGetValue(pairingId, out var estimate) || !estimate.Leashed)
            return false;
        estimate.Leashed = false;
        return true;
    }

    private void OnSent(string text)
    {
        // A command goes to the active pairing, same as ChatComposer.Wrap.
        if (config.GetActivePairing() is not { Direction: PairingDirection.OwnerSide, IsPaired: true } pairing) return;
        if (StripEnvelope(text, pairing) is not { Length: > 0 } body) return;

        if (!estimates.TryGetValue(pairing.Id, out var estimate))
            estimates[pairing.Id] = estimate = new OwnerStatusEstimate();
        Apply(estimate, body);
    }

    /// Leaves the command body exactly as ChatComposer.Wrap received it.
    private string? StripEnvelope(string text, PairingState pairing)
    {
        var rest = text.TrimStart();
        if (!rest.StartsWith('/')) return null;
        var isTell = rest.StartsWith("/tell ", StringComparison.OrdinalIgnoreCase);
        rest = SkipToken(rest);
        if (isTell)
            rest = SkipTellTarget(rest, pairing);

        var trigger = (!string.IsNullOrWhiteSpace(pairing.PeerTriggerPhrase) ? pairing.PeerTriggerPhrase : config.TriggerPhrase).Trim();
        if (trigger.Length > 0 && rest.StartsWith(trigger, StringComparison.OrdinalIgnoreCase))
            rest = rest[trigger.Length..];
        return rest.Trim();
    }

    private static string SkipToken(string s)
    {
        var space = s.IndexOf(' ');
        return space < 0 ? "" : s[(space + 1)..].TrimStart();
    }

    /// The tell target contains a space, so skip past the peer's whole address.
    private static string SkipTellTarget(string s, PairingState pairing)
    {
        var address = $"{pairing.PeerName}@{pairing.PeerWorld}";
        if (s.StartsWith(address, StringComparison.OrdinalIgnoreCase))
            return s[address.Length..].TrimStart();
        var at = s.IndexOf('@');
        return at < 0 ? s : SkipToken(s[at..]);
    }

    private void Apply(OwnerStatusEstimate estimate, string body)
    {
        var (word, rest) = SplitFirst(body);
        switch (word.ToLowerInvariant())
        {
            case ControlWords.Leash when rest.StartsWith(ChatComposer.LeashTravelWord + " ", StringComparison.OrdinalIgnoreCase):
                // Leash travel isn't a new leash, and may be addressed to a pairing other than the active one.
                break;
            case ControlWords.Leash:
                // 3 when bare.
                DutyPauseOption.Strip(LengthOption.Strip(MoodleOption.Strip(rest, out _), out var length), out var pauseInDuties);
                estimate.Leashed = true;
                estimate.PauseInDuties = pauseInDuties;
                estimate.LeashedAtUtc = DateTime.UtcNow;
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
                ApplyRestraint(estimate, rest);
                break;
            case "title":
                ApplyTitle(estimate, rest);
                break;
            case "outfit":
                ApplyOutfit(estimate, rest);
                break;
            case "gesture":
                var animation = Unquote(LockTimerOption.StripSeconds(rest, out _).Trim());
                estimate.LastAnimation = animation.Equals(ChatComposer.StopGestureWord, StringComparison.OrdinalIgnoreCase) ? null : animation;
                break;
            case "moodle":
                ApplyMoodle(estimate, rest);
                break;
            case "toy":
                estimate.LastToy = rest.Equals("stop", StringComparison.OrdinalIgnoreCase) ? null : rest.Trim();
                break;
            case "customtrigger":
                ApplyCustomTrigger(estimate, rest);
                break;
        }
    }

    private void ApplyRestraint(OwnerStatusEstimate estimate, string rest)
    {
        rest = MoodleOption.Strip(LockTimerOption.Strip(rest, out var restraintLock), out _).Trim();
        var (verb, remainder) = SplitFirst(rest);
        List<RestraintRuleAssignment>? rules = null;
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
                    estimate.ShiftRestraintTimers(delta);
                return;
            case "lock":
                if (!RestraintCommand.TryParseLockCommand(remainder, out reference, out rules)) return;
                rules ??= KnownRulesFor(reference);
                break;
            case "catalog":
                if (!RestraintCommand.TryParseCatalogCommand(remainder, out _, out reference, out _, out rules)) return;
                break;
            case "wear":
                if (!RestraintCommand.TryParseWearCommand(remainder, out _, out _, out reference, out rules)) return;
                break;
            default:
                return;
        }
        estimate.MarkRestrained(reference, rules, restraintLock);
    }

    /// When a `lockfor:` command's lock ends on the Sub, counted from now like the Sub counts it.
    private static DateTime? EndOf(int? lockSeconds) =>
        RestraintLock.FromSeconds(lockSeconds).Duration is { } duration ? DateTime.UtcNow + duration : null;

    private static void ApplyTitle(OwnerStatusEstimate estimate, string rest)
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
        estimate.TitleEndsAtUtc = EndOf(seconds);
    }

    private static void ApplyOutfit(OwnerStatusEstimate estimate, string rest)
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
        estimate.OutfitEndsAtUtc = EndOf(seconds);
    }

    private static void ApplyMoodle(OwnerStatusEstimate estimate, string rest)
    {
        var (verb, remainder) = SplitFirst(LockTimerOption.StripSeconds(rest, out var seconds));
        if (verb.Equals("clear", StringComparison.OrdinalIgnoreCase))
            estimate.ClearModule("moodles");
        else if (verb.Equals("apply", StringComparison.OrdinalIgnoreCase) && Unquote(remainder) is { Length: > 0 } name)
            estimate.AddMoodle(name, EndOf(seconds));
        else if (verb.Equals(CustomMoodle.CustomWord, StringComparison.OrdinalIgnoreCase) && CustomMoodle.TryDecode(remainder, out var custom))
            estimate.AddMoodle(MoodlesTextFormat.StripMarkup(custom.Title), EndOf(seconds), custom.Id);
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
    private void ApplyCustomTrigger(OwnerStatusEstimate estimate, string rest)
    {
        rest = LockTimerOption.Strip(rest, out var restraintLock).TrimStart();
        var (verb, remainder) = SplitFirst(rest);
        if (!verb.Equals("cast", StringComparison.OrdinalIgnoreCase)) return;
        if (!CustomTriggerCommand.TryParseCastCommand(remainder, out _, out var actions)) return;

        foreach (var action in actions.Where(a => a.Kind == CustomTriggerActionKind.Restraint))
            estimate.MarkRestrained(action.RestraintDeviceName, action.RestraintRules ?? KnownRulesFor(action.RestraintDeviceName), restraintLock);
    }

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

    public void Dispose() => sender.Sent -= OnSent;
}

/// One restraint the Owner sent, named as their command named it. Re-sending it replaces its lock, as on the Sub.
public sealed class EstimatedRestraint
{
    public string Reference { get; internal set; } = "";
    public bool Gagged { get; internal set; }
    /// What its rules draw; None when the rules aren't known.
    public CuffSet Cuffs { get; internal set; }
    public bool Locked { get; internal set; }
    public DateTime? ExpiresAtUtc { get; internal set; }
    public bool HasKey { get; internal set; }
}

/// `CustomId` for a moodle the Owner wrote, which is removed by id.
public sealed record EstimatedMoodle(string Name, DateTime? EndsAtUtc, Guid? CustomId = null);

public sealed class OwnerStatusEstimate
{
    private readonly List<EstimatedRestraint> restraints = new();

    public IReadOnlyList<EstimatedRestraint> Restraints => restraints;
    public bool Gagged => restraints.Any(r => r.Gagged);
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

    internal void MarkRestrained(string reference, List<RestraintRuleAssignment>? rules, RestraintLock restraintLock)
    {
        RemoveRestraint(reference);
        restraints.Add(new EstimatedRestraint
        {
            Reference = reference.Trim(),
            Gagged = rules?.Any(r => r.Kind == RestraintRuleKind.Gagged) == true,
            Cuffs = CuffSets.Drawn(rules),
            Locked = true,
            ExpiresAtUtc = restraintLock.Duration is { } duration ? DateTime.UtcNow + duration : null,
            HasKey = restraintLock.Key is not null,
        });
    }

    internal void RemoveRestraint(string reference) =>
        restraints.RemoveAll(r => string.Equals(r.Reference, reference.Trim(), StringComparison.OrdinalIgnoreCase));


    /// Mirrors the Sub's `restraint timer`: every timed lock moves, capped at the longest lock; one moved past now is off.
    internal void ShiftRestraintTimers(int deltaSeconds)
    {
        var max = DateTime.UtcNow + RestraintLock.MaxDuration;
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
