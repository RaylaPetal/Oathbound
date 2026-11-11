using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Oathbound.Plugin.Ipc;

/// SimpleHeels' tag IPC (API 2.x). A tag on object 0, the local player, is carried by sync services to the players
/// they sync with, and shows up on that player's object in their SimpleHeels. Any failure reads as unavailable.
public sealed class SimpleHeelsIpc : IDisposable
{
    private const int SupportedMajorVersion = 2;
    public const int LocalPlayer = 0;

    private readonly ICallGateSubscriber<(int, int)> apiVersion;
    private readonly ICallGateSubscriber<int, string, string, object?> setTag;
    private readonly ICallGateSubscriber<int, string, string?> getTag;
    private readonly ICallGateSubscriber<int, string, object?> removeTag;

    public bool IsAvailable { get; private set; }

    /// SimpleHeels has just appeared, so the local tag has to be written again.
    public event Action? BecameAvailable;

    public SimpleHeelsIpc()
    {
        var pi = Plugin.PluginInterface;
        apiVersion = pi.GetIpcSubscriber<(int, int)>("SimpleHeels.ApiVersion");
        setTag = pi.GetIpcSubscriber<int, string, string, object?>("SimpleHeels.SetTag");
        getTag = pi.GetIpcSubscriber<int, string, string?>("SimpleHeels.GetTag");
        removeTag = pi.GetIpcSubscriber<int, string, object?>("SimpleHeels.RemoveTag");
        pi.ActivePluginsChanged += OnPluginsChanged;
        Refresh();
    }

    public void Dispose() => Plugin.PluginInterface.ActivePluginsChanged -= OnPluginsChanged;

    private void OnPluginsChanged(IActivePluginsChangedEventArgs args) => Refresh();

    private void Refresh()
    {
        var was = IsAvailable;
        try { IsAvailable = apiVersion.InvokeFunc().Item1 == SupportedMajorVersion; }
        catch { IsAvailable = false; }
        if (IsAvailable && !was)
            BecameAvailable?.Invoke();
    }

    /// Writes the local player's tag, or removes it when null. False when it couldn't be written.
    public bool SetLocal(string key, string? value)
    {
        if (!IsAvailable)
            return false;
        try
        {
            if (value is null) removeTag.InvokeAction(LocalPlayer, key);
            else setTag.InvokeAction(LocalPlayer, key, value);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"SimpleHeels tag write failed: {ex.Message}");
            return false;
        }
    }

    /// Another player's tag, or null when they have none.
    public string? Get(int objectIndex, string key)
    {
        if (!IsAvailable)
            return null;
        try { return getTag.InvokeFunc(objectIndex, key); }
        catch { return null; }
    }
}
