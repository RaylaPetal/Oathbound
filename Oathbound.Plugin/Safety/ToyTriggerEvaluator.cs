using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Hooks;
using ECommons.Hooks.ActionEffectTypes;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Safety;

/// collar/toy-control "Local automatic toy triggers": entirely local, Sub-side automation - no Owner
/// involvement, no wire message, no network round-trip. Polled from the same per-frame `OnFrameworkUpdate`
/// dispatch every other per-frame checker in this plugin already uses. `HealthPercent` and
/// `RestrictionActive` are edge-triggered (fire on the transition into the condition, not on every tick it
/// stays true) so a Sub sitting below a health threshold, or holding an active restriction, for a long
/// stretch doesn't refire on every frame; `PlayerDamage`/`SpellCastOnYou`/`EmoteOnYou` are inherently
/// event-based already (one hit/emote, one candidate firing). Every rule additionally enforces its own
/// cooldown (see `TryFire`), with a 2-second floor beneath whatever the Sub configured, defensively - the
/// same clamp-not-reject posture every other numeric input in this plugin uses.
public sealed class ToyTriggerEvaluator : IDisposable
{
    private const int MinimumCooldownSeconds = 2;

    private readonly PluginConfig config;
    private readonly ToyControlCommand toyControl;
    private readonly SubRuntimeState runtimeState;
    private readonly RestrictionRuleManager restrictionRules;

    private readonly Dictionary<string, long> lastFiredTicks = new();
    private readonly Dictionary<string, bool> wasHealthBelowThreshold = new();
    private readonly Dictionary<string, bool> wasRestrictionActive = new();

    /// One entry per action-effect the hook callback observed aimed at the local player, since the last
    /// `OnFrameworkUpdate` tick drained it. A `ConcurrentQueue` rather than a plain list/flag - the hook
    /// callback and the framework-update tick are not guaranteed to be the same call stack, so this needs
    /// to be safe to enqueue into concurrently with the drain below. Deliberately not acted on from inside
    /// the hook callback itself - keeps every actual toy-triggering decision on the same framework-tick
    /// cadence as every other check here, and avoids doing IPC/Buttplug calls from inside a native-hook
    /// callback's call stack (see design.md Decision 4).
    private readonly ConcurrentQueue<ActionHit> actionHits = new();

    /// `SourceName`/`SourceWorld` are empty when the source isn't a player character (an NPC/enemy, or a
    /// source no longer in the object table) - such a hit can never match a player filter.
    private readonly record struct ActionHit(uint ActionId, uint SourceJobId, bool IsDamage, bool FromPlayer, string SourceName, string SourceWorld);

    private readonly EmoteWatcher emoteWatcher;

    public ToyTriggerEvaluator(PluginConfig config, ToyControlCommand toyControl, SubRuntimeState runtimeState, RestrictionRuleManager restrictionRules, EmoteWatcher emoteWatcher)
    {
        this.config = config;
        this.toyControl = toyControl;
        this.runtimeState = runtimeState;
        this.restrictionRules = restrictionRules;
        this.emoteWatcher = emoteWatcher;
        ActionEffect.ActionEffectEntryEvent += OnActionEffect;
    }

    /// collar/toy-control "Hit trigger fires"/"Spell cast on you": records every action effect (not just
    /// damage - `SpellCastOnYou` also wants heals/buffs/debuffs) aimed at the local player from any source
    /// other than the local player itself. Whether it came from a real player character is recorded rather
    /// than filtered here: `PlayerDamage` takes damage from anything, `SpellCastOnYou` only from players.
    /// `ActionEffectType.Nothing` entries (empty padding slots in a multi-target packet) are skipped.
    private void OnActionEffect(uint actionId, ushort animationId, ActionEffectType type, uint sourceId, ulong targetOid, uint damage)
    {
        if (type == ActionEffectType.Nothing) return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer is null || targetOid != localPlayer.GameObjectId) return;
        if (sourceId == localPlayer.EntityId) return;

        var source = Plugin.ObjectTable.FirstOrDefault(o => o.EntityId == sourceId);
        var fromPlayer = source is { ObjectKind: ObjectKind.Pc };
        var sourceJobId = source is ICharacter character ? character.ClassJob.RowId : 0u;
        var (name, world) = fromPlayer ? Identify(source!) : ("", "");
        actionHits.Enqueue(new ActionHit(actionId, sourceJobId, type == ActionEffectType.Damage, fromPlayer, name, world));
    }

    private static (string Name, string World) Identify(IGameObject obj) =>
        (obj.Name.TextValue, obj is IPlayerCharacter pc ? pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "" : "");

    public void OnFrameworkUpdate()
    {
        var hitsThisTick = new List<ActionHit>();
        while (actionHits.TryDequeue(out var hit))
            hitsThisTick.Add(hit);

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        // Emotes aimed at the local player, from the shared watcher (polled by Plugin just before this).
        var emotesThisTick = localPlayer is null ? [] : emoteWatcher.ThisTick.Where(e => e.TargetObjectId == localPlayer.GameObjectId).ToList();

        if (!config.ToyTriggersAcknowledged || runtimeState.ToyTriggersSuspended)
            return;

        foreach (var rule in config.ToyTriggerRules)
        {
            if (!rule.Enabled) continue;

            switch (rule.Kind)
            {
                case ToyTriggerKind.HealthPercent:
                    EvaluateHealthPercent(rule, localPlayer);
                    break;
                case ToyTriggerKind.PlayerDamage:
                    foreach (var h in hitsThisTick)
                    {
                        if (!h.IsDamage || !MatchesSourceFilter(rule, h.SourceName, h.SourceWorld)) continue;
                        TryFire(rule, h.SourceName.Length > 0 ? $"hit by {h.SourceName}" : "hit");
                        break;
                    }
                    break;
                case ToyTriggerKind.RestrictionActive:
                    EvaluateRestrictionActive(rule);
                    break;
                case ToyTriggerKind.SpellCastOnYou:
                    foreach (var h in hitsThisTick)
                    {
                        if (!h.FromPlayer || !MatchesSpellFilter(rule, h) || !MatchesSourceFilter(rule, h.SourceName, h.SourceWorld)) continue;
                        TryFire(rule, $"{ActionName(h.ActionId)} from {h.SourceName}");
                        break;
                    }
                    break;
                case ToyTriggerKind.EmoteOnYou:
                    foreach (var e in emotesThisTick)
                    {
                        if (rule.EmoteIds.Count > 0 && !rule.EmoteIds.Contains(e.EmoteId)) continue;
                        if (!MatchesSourceFilter(rule, e.SourceName, e.SourceWorld)) continue;
                        TryFire(rule, $"{EmoteName(e.EmoteId)} from {e.SourceName}");
                        break;
                    }
                    break;
            }
        }
    }

    /// collar/toy-control "Spell cast on you": `SpellJobIds`/`SpellActionIds` are independent AND filters -
    /// an empty list on either side means "any" for that side, so a rule with both empty matches every hit
    /// (any action, from any player).
    private static bool MatchesSpellFilter(ToyTriggerRule rule, ActionHit hit) =>
        (rule.SpellActionIds.Count == 0 || rule.SpellActionIds.Contains(hit.ActionId)) &&
        (rule.SpellJobIds.Count == 0 || rule.SpellJobIds.Contains(hit.SourceJobId));

    /// Empty `SourcePlayers` means anyone. Otherwise the source must match an entry by name, and by world
    /// too when the entry has one ("Name Surname@World"); a source with no player name (an NPC/enemy for
    /// `PlayerDamage`) never matches a non-empty filter.
    private static bool MatchesSourceFilter(ToyTriggerRule rule, string sourceName, string sourceWorld)
    {
        if (rule.SourcePlayers.Count == 0) return true;
        if (sourceName.Length == 0) return false;
        foreach (var entry in rule.SourcePlayers)
        {
            var at = entry.IndexOf('@');
            var name = (at < 0 ? entry : entry[..at]).Trim();
            var world = at < 0 ? "" : entry[(at + 1)..].Trim();
            if (string.Equals(name, sourceName, StringComparison.OrdinalIgnoreCase) &&
                (world.Length == 0 || string.Equals(world, sourceWorld, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static string ActionName(uint actionId) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(actionId)?.Name.ExtractText() is { Length: > 0 } name ? name : "an action";

    private static string EmoteName(uint emoteId) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>().GetRowOrDefault(emoteId)?.Name.ExtractText() is { Length: > 0 } name ? name : "an emote";

    /// collar/toy-control "Health-percentage trigger fires": edge-triggered on the transition from at-or-
    /// above the threshold to below it, not on every tick health stays below it.
    private void EvaluateHealthPercent(ToyTriggerRule rule, IPlayerCharacter? localPlayer)
    {
        if (localPlayer is null || localPlayer.MaxHp == 0) return;

        var percent = localPlayer.CurrentHp * 100.0 / localPlayer.MaxHp;
        var isBelow = percent <= rule.HealthPercentThreshold;
        var wasBelow = wasHealthBelowThreshold.TryGetValue(rule.Id, out var prev) && prev;
        wasHealthBelowThreshold[rule.Id] = isBelow;

        if (isBelow && !wasBelow)
            TryFire(rule, $"health below {rule.HealthPercentThreshold}%");
    }

    /// collar/toy-control "Restriction-active trigger fires": edge-triggered on the transition into active,
    /// reusing the existing RestrictionRuleManager state - no new tracking needed for "is it active".
    private void EvaluateRestrictionActive(ToyTriggerRule rule)
    {
        var isActive = restrictionRules.IsActive(rule.RestrictionKind);
        var wasActive = wasRestrictionActive.TryGetValue(rule.Id, out var prev) && prev;
        wasRestrictionActive[rule.Id] = isActive;

        if (isActive && !wasActive)
            TryFire(rule, $"{rule.RestrictionKind} became active");
    }

    /// collar/toy-control "Automatic triggers are rate-limited per rule": the per-rule cooldown gate every
    /// firing path above funnels through - a rule that fails to actually start a toy action (e.g. Intiface
    /// disconnected) does not consume its cooldown, so it can fire as soon as the condition is next true and
    /// a connection exists. `reason` only labels the live status display.
    private void TryFire(ToyTriggerRule rule, string reason)
    {
        var now = Environment.TickCount64;
        var cooldownMs = Math.Max(MinimumCooldownSeconds, rule.CooldownSeconds) * 1000L;
        if (lastFiredTicks.TryGetValue(rule.Id, out var last) && now - last < cooldownMs)
            return;

        var source = $"Trigger: {reason}";
        var fired = rule.PatternName is { Length: > 0 } patternName
            ? toyControl.ForceApplyPattern(patternName, source)
            : toyControl.ForceApplyVibrate(rule.IntensityPercent ?? 50, rule.DurationSeconds is { } d ? ToyDuration.Bounded(d) : ToyDuration.Unspecified, source);

        if (fired)
            lastFiredTicks[rule.Id] = now;
    }

    public void Dispose()
    {
        ActionEffect.ActionEffectEntryEvent -= OnActionEffect;
    }
}
