using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.DalamudServices;

namespace Oathbound.Plugin.Safety;

/// SourceWorld is empty when the home world can't be resolved.
public readonly record struct EmoteEvent(uint EmoteId, ulong SourceObjectId, string SourceName, string SourceWorld, Vector3 SourcePosition, ulong TargetObjectId);

#pragma warning disable CS0649 // assigned via reflection by Svc.Hook.InitializeFromAttributes, not by the compiler

/// Emotes from the game's emote-received function where its signature resolves, plus once-per-frame polling of each
/// player's emote state. Polling alone misses facial expressions (they never reach the emote state) and the same emote
/// used twice in a row; the hook catches both. Nothing reads localized chat. The local player's own emotes go only to
/// OwnThisTick, never ThisTick.
public sealed class EmoteWatcher : IDisposable
{
    private const string SigOnEmote = "E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 4C 89 74 24";

    private delegate void OnEmoteDelegate(ulong unk, ulong instigatorAddress, ushort emoteId, ulong targetId, ulong unk2);

    [Signature(SigOnEmote, DetourName = nameof(OnEmoteDetour), Fallibility = Fallibility.Auto)]
    private readonly Hook<OnEmoteDelegate>? onEmoteHook;

    /// How long a hooked emote keeps polling from reporting the same one again, which can lag it by a frame or two.
    private static readonly TimeSpan HookedDedupe = TimeSpan.FromSeconds(1);

    private readonly ConcurrentQueue<(nint Instigator, ushort EmoteId, ulong TargetId)> hooked = new();
    private readonly Dictionary<(ulong Source, ushort EmoteId, ulong TargetId), DateTime> recentlyHooked = new();
    private readonly Dictionary<ulong, (ushort EmoteId, ulong TargetId)> lastState = new();
    private readonly List<EmoteEvent> thisTick = new();

    public EmoteWatcher()
    {
        try
        {
            Svc.Hook.InitializeFromAttributes(this);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "EmoteWatcher: emote hook setup failed.");
        }
        if (onEmoteHook is not null)
            onEmoteHook.Enable();
        else
            Plugin.Log.Warning("EmoteWatcher: emote hook didn't resolve - facial expressions and repeated emotes won't be seen this session.");
    }

    public void Dispose() => onEmoteHook?.Dispose();

    /// Only queues what arrived; the addresses are matched against the object table on the framework thread, never read.
    private void OnEmoteDetour(ulong unk, ulong instigatorAddress, ushort emoteId, ulong targetId, ulong unk2)
    {
        try
        {
            if (emoteId != 0 && instigatorAddress != 0)
                hooked.Enqueue(((nint)instigatorAddress, emoteId, targetId));
        }
        catch
        {
            // Never let a detour throw into the game.
        }
        onEmoteHook!.Original(unk, instigatorAddress, emoteId, targetId, unk2);
    }

    /// Read after Poll on the same tick.
    public IReadOnlyList<EmoteEvent> ThisTick => thisTick;

    /// The local player's own emote started this tick, if any. Kept apart so nothing reacts to its own emotes.
    public (uint EmoteId, ulong TargetObjectId)? OwnThisTick { get; private set; }
    private (ushort EmoteId, ulong TargetId)? ownLast;

    public unsafe void Poll()
    {
        thisTick.Clear();
        OwnThisTick = null;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer is null)
        {
            lastState.Clear();
            hooked.Clear();
            recentlyHooked.Clear();
            ownLast = null;
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var key in recentlyHooked.Where(kv => now - kv.Value > HookedDedupe).Select(kv => kv.Key).ToList())
            recentlyHooked.Remove(key);
        while (hooked.TryDequeue(out var arrived))
        {
            if (arrived.Instigator == localPlayer.Address)
            {
                OwnThisTick = (arrived.EmoteId, arrived.TargetId);
                recentlyHooked[(localPlayer.GameObjectId, arrived.EmoteId, arrived.TargetId)] = now;
                continue;
            }
            var source = Plugin.ObjectTable.FirstOrDefault(o => o.Address == arrived.Instigator && o.ObjectKind == ObjectKind.Pc);
            if (source is null)
                continue;
            var sourceWorld = source is IPlayerCharacter sourcePc ? sourcePc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "" : "";
            thisTick.Add(new EmoteEvent(arrived.EmoteId, source.GameObjectId, source.Name.TextValue, sourceWorld, source.Position, arrived.TargetId));
            recentlyHooked[(source.GameObjectId, arrived.EmoteId, arrived.TargetId)] = now;
        }

        if (localPlayer.Address != nint.Zero)
        {
            var me = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)localPlayer.Address;
            var own = (me->EmoteController.EmoteId, (ulong)me->EmoteController.Target);
            if (OwnThisTick is null && ownLast is { } before && own.EmoteId != 0 && before != own
                && !recentlyHooked.ContainsKey((localPlayer.GameObjectId, own.EmoteId, own.Item2)))
                OwnThisTick = (own.EmoteId, own.Item2);
            ownLast = own;
        }

        var seen = new HashSet<ulong>();
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != ObjectKind.Pc || obj.GameObjectId == localPlayer.GameObjectId || obj.Address == nint.Zero)
                continue;

            var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)obj.Address;
            var emoteId = chara->EmoteController.EmoteId;
            ulong targetId = chara->EmoteController.Target;
            seen.Add(obj.GameObjectId);

            // No event on first sighting: a character already mid-emote hasn't just started it.
            var hadPrevious = lastState.TryGetValue(obj.GameObjectId, out var previous);
            lastState[obj.GameObjectId] = (emoteId, targetId);
            if (!hadPrevious || emoteId == 0 || previous == (emoteId, targetId) || recentlyHooked.ContainsKey((obj.GameObjectId, emoteId, targetId)))
                continue;

            var world = obj is IPlayerCharacter pc ? pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "" : "";
            thisTick.Add(new EmoteEvent(emoteId, obj.GameObjectId, obj.Name.TextValue, world, obj.Position, targetId));
        }

        foreach (var gone in lastState.Keys.Where(k => !seen.Contains(k)).ToList())
            lastState.Remove(gone);
    }
}
