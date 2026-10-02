using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace Oathbound.Plugin.Safety;

/// SourceWorld is empty when the home world can't be resolved.
public readonly record struct EmoteEvent(uint EmoteId, ulong SourceObjectId, string SourceName, string SourceWorld, Vector3 SourcePosition, ulong TargetObjectId);

/// Once-per-frame emote detection by polling each nearby player's emote state, rather than hooking or parsing
/// localized chat. A new (emote, target) pair counts as a new emote. The local player's own emotes are never reported.
public sealed class EmoteWatcher
{
    private readonly Dictionary<ulong, (ushort EmoteId, ulong TargetId)> lastState = new();
    private readonly List<EmoteEvent> thisTick = new();

    /// Read after Poll on the same tick.
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

            // No event on first sighting: a character already mid-emote hasn't just started it.
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
