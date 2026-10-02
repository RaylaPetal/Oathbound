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

/// Local, Sub-side toy automation; nothing is sent. Health and restriction triggers are edge-triggered;
/// the hit/spell/emote ones are event-based. Each rule has a cooldown with a 2 s floor.
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

    /// The hook callback and the framework tick can run concurrently. Hits are only acted on from the tick,
    /// never from inside the native hook.
    private readonly ConcurrentQueue<ActionHit> actionHits = new();

    /// Empty for non-player sources, which never match a player filter.
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

    /// Records every effect aimed at the local player, damaging or not, from any source but ourselves.
    /// Padding (ActionEffectType.Nothing) is skipped.
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
        // From the shared watcher, polled by Plugin just before this.
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

    /// Independent AND filters; an empty list means "any".
    private static bool MatchesSpellFilter(ToyTriggerRule rule, ActionHit hit) =>
        (rule.SpellActionIds.Count == 0 || rule.SpellActionIds.Contains(hit.ActionId)) &&
        (rule.SpellJobIds.Count == 0 || rule.SpellJobIds.Contains(hit.SourceJobId));

    /// Empty means anyone. Name match, plus world when the entry has one.
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

    /// Fires on the transition below the threshold, not every tick.
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

    /// Fires on the transition into active.
    private void EvaluateRestrictionActive(ToyTriggerRule rule)
    {
        var isActive = restrictionRules.IsActive(rule.RestrictionKind);
        var wasActive = wasRestrictionActive.TryGetValue(rule.Id, out var prev) && prev;
        wasRestrictionActive[rule.Id] = isActive;

        if (isActive && !wasActive)
            TryFire(rule, $"{rule.RestrictionKind} became active");
    }

    /// A rule that fails to start a toy action doesn't consume its cooldown. `reason` only labels the status display.
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
