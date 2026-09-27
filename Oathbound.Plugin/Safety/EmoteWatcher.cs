using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace Oathbound.Plugin.Safety;

/// One emote another player character just started: who, from where, and at whom. `SourceWorld` is empty
/// when the home world can't be resolved.
public readonly record struct EmoteEvent(uint EmoteId, ulong SourceObjectId, string SourceName, string SourceWorld, Vector3 SourcePosition, ulong TargetObjectId);

/// Shared, once-per-frame emote detection for the toy "Emote used on you" trigger and Reactions: every
/// nearby player character's own emote state carries the emote it's playing and who it's aimed at, so this
/// polls that (the same technique ReactToMe uses) instead of hooking the game's emote handler or parsing
/// localized chat text. An emote counts the moment a character's (emote, target) pair changes to a new
/// non-zero emote; the same person repeating the same emote back-to-back with nothing in between can read
/// as one continuous emote. The local player's own emotes are never reported.
public sealed class EmoteWatcher
{
    private readonly Dictionary<ulong, (ushort EmoteId, ulong TargetId)> lastState = new();
    private readonly List<EmoteEvent> thisTick = new();

    /// Emotes that started since the previous `Poll` - read by consumers after `Poll` on the same tick.
    public IReadOnlyList<EmoteEvent> ThisTick => thisTick;

    public unsafe void Poll()
    {
        thisTick.Clear();
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer is null)
        {
            lastState.Clear();
            return;
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

            // No event on the first sighting: a character already mid-emote when they come into range (or
            // when the plugin loads) hasn't just started it.
            var hadPrevious = lastState.TryGetValue(obj.GameObjectId, out var previous);
            lastState[obj.GameObjectId] = (emoteId, targetId);
            if (!hadPrevious || emoteId == 0 || previous == (emoteId, targetId))
                continue;

            var world = obj is IPlayerCharacter pc ? pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "" : "";
            thisTick.Add(new EmoteEvent(emoteId, obj.GameObjectId, obj.Name.TextValue, world, obj.Position, targetId));
        }

        foreach (var gone in lastState.Keys.Where(k => !seen.Contains(k)).ToList())
            lastState.Remove(gone);
    }
}
