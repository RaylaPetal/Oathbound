using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.Commands;

/// Layers our temporary Penumbra claims per collection/mod; releasing the top claim restores the next one.
/// If held settings are dropped or changed anyway, the top claim is put back next frame.
public sealed class TemporaryModSettingsCoordinator : IDisposable
{
    private sealed record Claim(string Owner, Dictionary<string, IReadOnlyList<string>> Selections);

    /// So a fight with something else rewriting the mod can't become a per-frame loop.
    private const long ReassertCooldownMs = 1000;

    private readonly Dictionary<(Guid Collection, string Mod), List<Claim>> claims = new();
    private readonly Dictionary<(Guid Collection, string Mod), long> lastReassert = new();
    private readonly HashSet<(Guid Collection, string Mod)> pendingReassert = new();
    private readonly PenumbraIpc penumbra;

    /// The change events our own writes fire aren't a loss.
    private bool writing;

    public TemporaryModSettingsCoordinator(PenumbraIpc penumbra)
    {
        this.penumbra = penumbra;
        penumbra.SettingChanged += OnSettingChanged;
    }

    public bool Acquire(string owner, Guid collection, string mod, IReadOnlyDictionary<string, IReadOnlyList<string>> selections)
    {
        var key = (collection, mod);
        if (!Set(collection, mod, selections)) return false;
        if (!claims.TryGetValue(key, out var layers)) claims[key] = layers = [];
        layers.RemoveAll(x => x.Owner == owner);
        layers.Add(new Claim(owner, selections.ToDictionary(x => x.Key, x => x.Value)));
        return true;
    }

    public bool Release(string owner, Guid collection, string mod)
    {
        var key = (collection, mod);
        if (!claims.TryGetValue(key, out var layers) || layers.RemoveAll(x => x.Owner == owner) == 0) return false;
        if (layers.Count == 0)
        {
            claims.Remove(key);
            lastReassert.Remove(key);
            return Remove(collection, mod);
        }
        return Set(collection, mod, layers[^1].Selections);
    }

    /// A locked setting can only be removed with our key, so release everything on unload.
    public void Dispose()
    {
        penumbra.SettingChanged -= OnSettingChanged;
        foreach (var (collection, mod) in claims.Keys.ToList())
            Remove(collection, mod);
        claims.Clear();
    }

    private void OnSettingChanged(Guid collection, string mod)
    {
        var key = (collection, mod);
        if (writing || !claims.ContainsKey(key) || !pendingReassert.Add(key))
            return;
        // Not inline: this fires from inside Penumbra's own change handling.
        Plugin.Framework.RunOnTick(() => Reassert(key));
    }

    private void Reassert((Guid Collection, string Mod) key)
    {
        pendingReassert.Remove(key);
        if (!claims.TryGetValue(key, out var layers) || layers.Count == 0)
            return;

        var top = layers[^1];
        if (penumbra.IsHeld(key.Collection, key.Mod, top.Selections))
            return;

        var now = Environment.TickCount64;
        if (lastReassert.TryGetValue(key, out var last) && now - last < ReassertCooldownMs)
        {
            // Too soon - retry after the cooldown instead of dropping it.
            if (pendingReassert.Add(key))
                Plugin.Framework.RunOnTick(() => Reassert(key), TimeSpan.FromMilliseconds(ReassertCooldownMs));
            return;
        }
        lastReassert[key] = now;
        var ok = Set(key.Collection, key.Mod, top.Selections);
        Plugin.Log.Information($"Temporary settings for mod \"{key.Mod}\" changed outside Oathbound while held by \"{top.Owner}\" - {(ok ? "re-applied" : "could not re-apply")}.");
    }

    private bool Set(Guid collection, string mod, IReadOnlyDictionary<string, IReadOnlyList<string>> selections)
    {
        writing = true;
        try { return penumbra.TrySetTemporarySettings(collection, mod, selections); }
        finally { writing = false; }
    }

    private bool Remove(Guid collection, string mod)
    {
        writing = true;
        try { return penumbra.TryRemoveTemporarySettings(collection, mod); }
        finally { writing = false; }
    }
}
