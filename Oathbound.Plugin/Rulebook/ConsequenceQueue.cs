using System;
using System.Collections.Generic;
using System.Linq;

namespace Oathbound.Plugin.Rulebook;

/// The only place rulebook consequences run, so the cooldowns, the per-pairing cap, the combat/cutscene hold
/// and panic's flush can't be skipped. In memory only: anything still held when the plugin unloads is dropped.
public sealed class ConsequenceQueue
{

    /// A consequence that fired for one pairing; Label is what the activity log calls it.
    public sealed record Item(Guid PairingId, string RuleId, string Label, IReadOnlyList<string> Commands);

    public enum Outcome
    {
        Ran,
        Held,
        CoolingDown,
    }

    /// Runs one command for a pairing and reports (success, message). Wired to ChatCommandListener.RunRulebookCommand.
    private readonly Func<Guid, string, (bool Success, string Message)> run;
    private readonly Action<Guid, Item, string, bool, string> onCommandResult;
    private readonly Func<bool> shouldHold;

    private readonly List<Item> held = new();
    private readonly Dictionary<(Guid, string), DateTime> lastFired = new();

    public ConsequenceQueue(Func<Guid, string, (bool, string)> run, Action<Guid, Item, string, bool, string> onCommandResult, Func<bool> shouldHold)
    {
        this.run = run;
        this.onCommandResult = onCommandResult;
        this.shouldHold = shouldHold;
    }

    /// `cooldownKey` null skips the per-rule cooldown (a drawn card, which its draw already paid for).
    public Outcome Enqueue(Item item, string? cooldownKey, int cooldownSeconds)
    {
        var now = DateTime.UtcNow;
        if (cooldownKey is not null)
        {
            if (lastFired.TryGetValue((item.PairingId, cooldownKey), out var last) && now - last < TimeSpan.FromSeconds(Math.Max(cooldownSeconds, RulebookLimits.MinCooldownSeconds)))
                return Outcome.CoolingDown;
        }

        if (cooldownKey is not null)
            lastFired[(item.PairingId, cooldownKey)] = now;

        // Toys and moodles change nothing the party sees the character do, so they never wait.
        var immediate = item.Commands.Where(IsUnheld).ToList();
        var rest = item.Commands.Where(c => !IsUnheld(c)).ToList();
        foreach (var command in immediate)
            Run(item, command);
        if (rest.Count == 0)
            return Outcome.Ran;
        var remainder = item with { Commands = rest };
        if (shouldHold() || held.Count > 0)
        {
            held.Add(remainder);
            return Outcome.Held;
        }
        foreach (var command in rest)
            Run(remainder, command);
        return Outcome.Ran;
    }

    /// Framework thread, every frame. Runs held consequences in order once nothing holds them.
    public void Pump()
    {
        if (held.Count == 0 || shouldHold())
            return;
        var batch = held.ToList();
        held.Clear();
        foreach (var item in batch)
            foreach (var command in item.Commands)
                Run(item, command);
    }

    public int HeldCount(Guid pairingId) => held.Count(i => i.PairingId == pairingId);

    /// Panic: nothing held runs later.
    public void Clear() => held.Clear();

    public void DropFor(Guid pairingId) => held.RemoveAll(i => i.PairingId == pairingId);

    private void Run(Item item, string command)
    {
        var (success, message) = run(item.PairingId, command);
        onCommandResult(item.PairingId, item, command, success, message);
    }

    private static bool IsUnheld(string command)
    {
        var word = command.TrimStart().Split(' ', 2)[0];
        return word.Equals("toy", StringComparison.OrdinalIgnoreCase) || word.Equals("moodle", StringComparison.OrdinalIgnoreCase);
    }
}
