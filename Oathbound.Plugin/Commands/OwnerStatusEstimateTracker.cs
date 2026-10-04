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
        if (estimate.RestrainedUntilUtc is { } until && DateTime.UtcNow >= until)
            estimate.ClearRestraints();
        return estimate.Any ? estimate : null;
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
                LengthOption.Strip(MoodleOption.Strip(rest, out _), out var length);
                estimate.Leashed = true;
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
            case "customtrigger":
                ApplyCustomTrigger(estimate, rest);
                break;
        }
    }

    private void ApplyRestraint(OwnerStatusEstimate estimate, string rest)
    {
        rest = MoodleOption.Strip(LockTimerOption.Strip(rest, out var restraintLock), out _).Trim();
        if (rest.Equals("unlock", StringComparison.OrdinalIgnoreCase))
        {
            estimate.ClearRestraints();
            return;
        }

        var (verb, remainder) = SplitFirst(rest);
        List<RestraintRuleAssignment>? rules = null;
        switch (verb.ToLowerInvariant())
        {
            case "lock":
                if (!RestraintCommand.TryParseLockCommand(remainder, out var name, out rules)) return;
                rules ??= KnownRulesFor(name);
                break;
            case "catalog":
                if (!RestraintCommand.TryParseCatalogCommand(remainder, out _, out _, out rules)) return;
                break;
            case "wear":
                if (!RestraintCommand.TryParseWearCommand(remainder, out _, out _, out _, out rules)) return;
                break;
            default:
                return;
        }
        estimate.MarkRestrained(rules, restraintLock);
    }

    /// Each part of a split bundle arrives separately. Bundles carry no leash action.
    private void ApplyCustomTrigger(OwnerStatusEstimate estimate, string rest)
    {
        rest = LockTimerOption.Strip(rest, out var restraintLock).TrimStart();
        var (verb, remainder) = SplitFirst(rest);
        if (!verb.Equals("cast", StringComparison.OrdinalIgnoreCase)) return;
        if (!CustomTriggerCommand.TryParseCastCommand(remainder, out _, out var actions)) return;

        foreach (var action in actions.Where(a => a.Kind == CustomTriggerActionKind.Restraint))
            estimate.MarkRestrained(action.RestraintRules ?? KnownRulesFor(action.RestraintDeviceName), restraintLock);
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

/// A later Permanent lock clears RestrainedUntilUtc - latest lock wins, as on the Sub.
public sealed class OwnerStatusEstimate
{
    public bool Gagged { get; internal set; }
    public bool Restrained { get; internal set; }
    public bool Leashed { get; internal set; }
    public float LeashLength { get; internal set; } = LengthOption.DefaultYalms;
    public DateTime? RestrainedUntilUtc { get; internal set; }
    /// What the sent restraints' rules draw; None when the rules aren't known.
    public CuffSet Cuffs { get; internal set; }

    public bool Any => Gagged || Restrained || Leashed;

    internal void MarkRestrained(List<RestraintRuleAssignment>? rules, RestraintLock restraintLock)
    {
        Restrained = true;
        if (rules?.Any(r => r.Kind == RestraintRuleKind.Gagged) == true)
            Gagged = true;
        Cuffs |= CuffSets.Drawn(rules);
        RestrainedUntilUtc = restraintLock.Duration is { } duration ? DateTime.UtcNow + duration : null;
    }

    internal void ClearRestraints()
    {
        Restrained = false;
        Gagged = false;
        Cuffs = CuffSet.None;
        RestrainedUntilUtc = null;
    }

    internal void ClearAll()
    {
        ClearRestraints();
        Leashed = false;
    }
}
