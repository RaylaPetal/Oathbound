using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;
using Newtonsoft.Json;

namespace Oathbound.Plugin.Ipc;

/// Matches Honorific's TitleData field-for-field so it round-trips; Honorific ships no API package.
public sealed class HonorificTitleData
{
    public string Title { get; set; } = "";
    public bool IsPrefix { get; set; }
    public Vector3? Color { get; set; }
    public Vector3? Glow { get; set; }
}

/// Always targets the local player (objectIndex 0).
public sealed class HonorificIpc : IDisposable
{
    private const int LocalPlayerObjectIndex = 0;

    private readonly ICallGateSubscriber<int, string, object> setCharacterTitle;
    private readonly ICallGateSubscriber<int, object> clearCharacterTitle;
    private readonly ICallGateSubscriber<string> getLocalCharacterTitle;
    private readonly ICallGateSubscriber<string, object> localCharacterTitleChanged;
    private readonly ICallGateSubscriber<object> ready;
    private readonly ICallGateSubscriber<(uint, uint)> apiVersion = Plugin.PluginInterface.GetIpcSubscriber<(uint, uint)>("Honorific.ApiVersion");

    /// The title now showing on the local character, null when none. May be raised off the framework thread.
    public event Action<HonorificTitleData?>? LocalTitleChanged;
    public event Action? Ready;

    /// Any exception means unavailable.
    public bool IsAvailable { get { try { apiVersion.InvokeFunc(); return true; } catch { return false; } } }

    public HonorificIpc()
    {
        setCharacterTitle = Plugin.PluginInterface.GetIpcSubscriber<int, string, object>("Honorific.SetCharacterTitle");
        clearCharacterTitle = Plugin.PluginInterface.GetIpcSubscriber<int, object>("Honorific.ClearCharacterTitle");
        getLocalCharacterTitle = Plugin.PluginInterface.GetIpcSubscriber<string>("Honorific.GetLocalCharacterTitle");
        localCharacterTitleChanged = Plugin.PluginInterface.GetIpcSubscriber<string, object>("Honorific.LocalCharacterTitleChanged");
        ready = Plugin.PluginInterface.GetIpcSubscriber<object>("Honorific.Ready");
        localCharacterTitleChanged.Subscribe(OnLocalCharacterTitleChanged);
        ready.Subscribe(OnReady);
    }

    public void Dispose()
    {
        localCharacterTitleChanged.Unsubscribe(OnLocalCharacterTitleChanged);
        ready.Unsubscribe(OnReady);
    }

    public void SetTitle(HonorificTitleData title) =>
        setCharacterTitle.InvokeAction(LocalPlayerObjectIndex, JsonConvert.SerializeObject(title));

    public void ClearTitle() => clearCharacterTitle.InvokeAction(LocalPlayerObjectIndex);

    /// False when Honorific can't be asked (missing, or too old to expose it).
    public bool TryGetLocalTitle(out HonorificTitleData? title)
    {
        title = null;
        try
        {
            title = Parse(getLocalCharacterTitle.InvokeFunc());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnLocalCharacterTitleChanged(string json) => LocalTitleChanged?.Invoke(Parse(json));

    private void OnReady() => Ready?.Invoke();

    private static HonorificTitleData? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try { return JsonConvert.DeserializeObject<HonorificTitleData>(json); }
        catch (JsonException) { return null; }
    }
}
