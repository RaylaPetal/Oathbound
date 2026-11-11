using System;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.Safety;

/// Keeps the local player's `oathbound` SimpleHeels tag matching their live state, so Oathbound players they're synced
/// with can see their leash and status icons. Deliberately has no off switch: it shares whenever SimpleHeels is installed.
/// Framework thread.
public sealed class SharedStatusPublisher : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly PluginConfig config;
    private readonly SimpleHeelsIpc heels;
    private readonly StatusIndicatorState status;
    private readonly FollowCommand follow;
    private DateTime nextUtc;
    /// What's on the tag now; null = no tag. Reset to force a rewrite.
    private string? written;
    private bool dirty = true;

    public SharedStatusPublisher(PluginConfig config, SimpleHeelsIpc heels, StatusIndicatorState status, FollowCommand follow)
    {
        this.config = config;
        this.heels = heels;
        this.status = status;
        this.follow = follow;
        heels.BecameAvailable += OnBecameAvailable;
    }

    private void OnBecameAvailable() => dirty = true;

    public void OnFrameworkUpdate()
    {
        var now = DateTime.UtcNow;
        if (now < nextUtc || !Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer is not { } me)
            return;
        nextUtc = now + Interval;

        var current = status.Get(me);
        int? length = null;
        string? owner = null;
        if (current.Leashed)
        {
            length = Math.Clamp((int)MathF.Round(follow.EffectiveLength), 1, 100);
            if (follow.LeashedPairingId is { } id && config.FindPairingById(id) is { PeerName: { } name, PeerWorld: { } world })
                owner = SharedStatus.OwnerHashOf(name, world);
        }
        Write(new SharedStatus(current.Gagged, current.Restrained, length, owner).Format());
    }

    private void Write(string? value)
    {
        if (!dirty && value == written)
            return;
        if (heels.SetLocal(SharedStatus.TagKey, value))
        {
            written = value;
            dirty = false;
        }
    }

    /// Panic, unpairing and unloading: off at once rather than on the next tick.
    public void Clear()
    {
        heels.SetLocal(SharedStatus.TagKey, null);
        written = null;
        dirty = false;
    }

    public void Dispose()
    {
        heels.BecameAvailable -= OnBecameAvailable;
        Clear();
    }
}
