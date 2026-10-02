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
public sealed class HonorificIpc
{
    private const int LocalPlayerObjectIndex = 0;

    private readonly ICallGateSubscriber<int, string, object> setCharacterTitle;
    private readonly ICallGateSubscriber<int, object> clearCharacterTitle;
    private readonly ICallGateSubscriber<(uint, uint)> apiVersion = Plugin.PluginInterface.GetIpcSubscriber<(uint, uint)>("Honorific.ApiVersion");

    /// Any exception means unavailable.
    public bool IsAvailable { get { try { apiVersion.InvokeFunc(); return true; } catch { return false; } } }

    public HonorificIpc()
    {
        setCharacterTitle = Plugin.PluginInterface.GetIpcSubscriber<int, string, object>("Honorific.SetCharacterTitle");
        clearCharacterTitle = Plugin.PluginInterface.GetIpcSubscriber<int, object>("Honorific.ClearCharacterTitle");
    }

    public void SetTitle(HonorificTitleData title) =>
        setCharacterTitle.InvokeAction(LocalPlayerObjectIndex, JsonConvert.SerializeObject(title));

    public void ClearTitle() => clearCharacterTitle.InvokeAction(LocalPlayerObjectIndex);
}
