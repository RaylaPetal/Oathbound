using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// collar/status-indicators: the Owner's estimate of each commanded Sub's gagged/restrained/leashed state,
/// built only from the commands this client itself sent - same shape as OwnerToyStatusTracker, for the same
/// reason: nothing about the Sub's state ever travels back over the wire. So this can't see the Sub's panic,
/// the Sub's own alias releases, or a command the Sub's client refused; the UI says so and offers Clear.
/// In-memory only - it starts empty on every load, and never sends anything.
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

    /// The estimate for one Owner-side pairing, or null if nothing is estimated active. Drops estimates for
    /// pairings that ended, and clears a timed restraint whose end time has passed.
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

    /// The Owner's manual "Clear estimate" (spec: "The Owner can see and clear a stale estimate").
    public void Clear(Guid pairingId) => estimates.Remove(pairingId);

    private void OnSent(string text)
    {
        // Same attribution as ChatComposer.Wrap and OwnerToyStatusTracker: a command goes to the active pairing.
        if (config.GetActivePairing() is not { Direction: PairingDirection.OwnerSide, IsPaired: true } pairing) return;
        if (StripEnvelope(text, pairing) is not { Length: > 0 } body) return;

        if (!estimates.TryGetValue(pairing.Id, out var estimate))
            estimates[pairing.Id] = estimate = new OwnerStatusEstimate();
        Apply(estimate, body);
    }

    /// Removes the channel prefix (`/tell Name@World `, `/p `, ...) and the trigger phrase, leaving the
    /// command body exactly as ChatComposer.Wrap received it.
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

    /// A tell target is `Name Surname@World` - it contains a space, so skip past the peer's own address.
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
            case ControlWords.Leash:
                estimate.Leashed = true;
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

    /// Each part of a (possibly split) bundle arrives as its own message; every restraint action in it marks
    /// the Sub restrained. Bundles carry no leash action (CustomTriggerActionKind has none).
    private void ApplyCustomTrigger(OwnerStatusEstimate estimate, string rest)
    {
        rest = LockTimerOption.Strip(rest, out var restraintLock).TrimStart();
        var (verb, remainder) = SplitFirst(rest);
        if (!verb.Equals("cast", StringComparison.OrdinalIgnoreCase)) return;
        if (!CustomTriggerCommand.TryParseCastCommand(remainder, out _, out var actions)) return;

        foreach (var action in actions.Where(a => a.Kind == CustomTriggerActionKind.Restraint))
            estimate.MarkRestrained(action.RestraintRules ?? KnownRulesFor(action.RestraintDeviceName), restraintLock);
    }

    /// A `restraint lock <name>` without inline rules uses the Sub's own device rules; the Owner only knows
    /// them if a saved restraint quick command of that name carries them. Unknown means "restrained, not
    /// known to be gagged".
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

/// One Owner-side pairing's estimate. `RestrainedUntilUtc` is set by a timed (`lockfor:`) restraint command;
/// a later Permanent one clears it - the same "latest lock wins" rule the Sub applies.
public sealed class OwnerStatusEstimate
{
    public bool Gagged { get; internal set; }
    public bool Restrained { get; internal set; }
    public bool Leashed { get; internal set; }
    public DateTime? RestrainedUntilUtc { get; internal set; }

    public bool Any => Gagged || Restrained || Leashed;

    internal void MarkRestrained(List<RestraintRuleAssignment>? rules, RestraintLock restraintLock)
    {
        Restrained = true;
        if (rules?.Any(r => r.Kind == RestraintRuleKind.Gagged) == true)
            Gagged = true;
        RestrainedUntilUtc = restraintLock.Duration is { } duration ? DateTime.UtcNow + duration : null;
    }

    internal void ClearRestraints()
    {
        Restrained = false;
        Gagged = false;
        RestrainedUntilUtc = null;
    }

    internal void ClearAll()
    {
        ClearRestraints();
        Leashed = false;
    }
}
