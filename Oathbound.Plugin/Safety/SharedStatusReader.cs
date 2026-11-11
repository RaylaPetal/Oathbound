using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.Safety;

/// Reads the `oathbound` tag of nearby players about once a second. Reading sends nothing; the tags arrive through
/// whatever sync service the player uses. Framework thread.
public sealed class SharedStatusReader
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly SimpleHeelsIpc heels;
    private readonly Dictionary<ulong, SharedStatus> statuses = new();
    /// Per object id, so a nearby player's name is hashed once rather than every frame.
    private readonly Dictionary<ulong, (string Hash, string NameWorld)> hashes = new();
    private readonly HashSet<ulong> seen = new();
    /// Each leashed player's Owner, resolved once per read rather than per nameplate per frame.
    private readonly Dictionary<ulong, ulong> owners = new();
    private DateTime nextUtc;

    public SharedStatusReader(SimpleHeelsIpc heels) => this.heels = heels;

    public SharedStatus? For(ulong objectId) => statuses.GetValueOrDefault(objectId);

    /// The nearby Owner holding this player's shared leash, or 0.
    public ulong OwnerOf(ulong objectId) => owners.GetValueOrDefault(objectId);

    public IEnumerable<(ulong ObjectId, SharedStatus Status)> All()
    {
        foreach (var (id, status) in statuses)
            yield return (id, status);
    }

    /// The nearby player whose name@world hashes to `hash`, or null.
    public IPlayerCharacter? FindByHash(string hash)
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is IPlayerCharacter player && HashOf(player) == hash)
                return player;
        }
        return null;
    }

    public string? HashOf(IPlayerCharacter player)
    {
        var world = player.HomeWorld.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrEmpty(world))
            return null;
        var nameWorld = $"{player.Name.TextValue}@{world}";
        if (hashes.TryGetValue(player.GameObjectId, out var cached) && cached.NameWorld == nameWorld)
            return cached.Hash;
        var hash = SharedStatus.OwnerHashOf(player.Name.TextValue, world);
        hashes[player.GameObjectId] = (hash, nameWorld);
        return hash;
    }

    public void OnFrameworkUpdate()
    {
        var now = DateTime.UtcNow;
        if (now < nextUtc)
            return;
        nextUtc = now + Interval;
        if (!heels.IsAvailable || Plugin.ObjectTable.LocalPlayer is not { } me)
        {
            statuses.Clear();
            hashes.Clear();
            owners.Clear();
            return;
        }

        seen.Clear();
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player || player.Address == me.Address)
                continue;
            seen.Add(player.GameObjectId);
            if (SharedStatus.TryParse(heels.Get(player.ObjectIndex, SharedStatus.TagKey)) is { } status)
                statuses[player.GameObjectId] = status;
            else
                statuses.Remove(player.GameObjectId);
        }
        foreach (var id in new List<ulong>(statuses.Keys))
            if (!seen.Contains(id))
                statuses.Remove(id);
        foreach (var id in new List<ulong>(hashes.Keys))
            if (!seen.Contains(id) && id != me.GameObjectId)
                hashes.Remove(id);

        owners.Clear();
        foreach (var (id, status) in statuses)
        {
            if (status.OwnerHash is { } hash && FindByHash(hash) is { } owner)
                owners[id] = owner.GameObjectId;
        }
    }
}
