using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay.UiOverlay;
using Lumina.Excel.Sheets;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.UI;

/// Status icons in KamiToolKit's own overlay, positioned each frame from the nameplate's name text. Nothing is
/// attached to the NamePlate addon, so other nameplate edits stay untouched. Disposing the controller removes every icon.
public sealed unsafe class StatusIconOverlay : OverlayNode
{
    /// Status icons are 24x32 (sampling 32 wide shows a sliver of the next icon). IconHeight is in nameplate units.
    private static readonly Vector2 IconTextureSize = new(24f, 32f);
    private const float IconHeight = 28f;
    private const float Gap = 2f;

    private readonly StatusIndicatorState state;
    private readonly uint[] iconIds;
    // Created lazily per slot.
    private readonly IconImageNode?[] icons = new IconImageNode?[AddonNamePlate.NumNamePlateObjects * 3];
    private bool warned;

    public StatusIconOverlay(StatusIndicatorState state, uint[] iconIds)
    {
        this.state = state;
        this.iconIds = iconIds;
        Size = new Vector2(1f, 1f);
    }

    public override OverlayLayer OverlayLayer => OverlayLayer.BehindUserInterface;

    protected override void OnUpdate()
    {
        try
        {
            Place();
        }
        catch (Exception ex)
        {
            HideAll();
            if (!warned)
            {
                warned = true;
                Plugin.Log.Warning(ex, "Status icons: nameplate layout couldn't be read - icons are off for this session.");
            }
        }
    }

    private void Place()
    {
        if (warned) { HideAll(); return; }

        var addon = (AddonNamePlate*)Plugin.GameGui.GetAddonByName("NamePlate").Address;
        if (addon == null || addon->AtkUnitBase.RootNode == null || !addon->AtkUnitBase.RootNode->IsVisible()) { HideAll(); return; }

        var framework = Framework.Instance();
        var ui3D = framework == null || framework->GetUIModule() == null ? null : framework->GetUIModule()->GetUI3DModule();
        if (ui3D == null) { HideAll(); return; }

        Span<bool> shown = stackalloc bool[AddonNamePlate.NumNamePlateObjects];
        var infos = ui3D->NamePlateObjectInfoPointers;
        var count = Math.Min(ui3D->NamePlateObjectInfoCount, infos.Length);
        for (var i = 0; i < count; i++)
        {
            var info = infos[i].Value;
            if (info == null || info->GameObject == null) continue;
            var index = info->NamePlateIndex;
            if (index < 0 || index >= AddonNamePlate.NumNamePlateObjects) continue;
            if (Plugin.ObjectTable.CreateObjectReference((nint)info->GameObject) is not IPlayerCharacter pc) continue;

            var status = state.Get(pc);
            if (!status.Any) continue;

            var nameText = addon->NamePlateObjectArray[index].NameText;
            if (nameText == null || !nameText->AtkResNode.IsVisible() || !addon->NamePlateObjectArray[index].NameContainer->IsVisible()) continue;

            PlaceRow(index, nameText, status);
            shown[index] = true;
        }

        for (var i = 0; i < shown.Length; i++)
            if (!shown[i]) HideSlot(i);
    }

    /// Scaled with the nameplate, which shrinks with distance.
    private void PlaceRow(int index, AtkTextNode* nameText, CharacterStatus status)
    {
        var res = &nameText->AtkResNode;
        var scale = AccumulatedScale(res);
        ushort textWidth = 0, textHeight = 0;
        nameText->GetTextDrawSize(&textWidth, &textHeight, null, 0, -1, false);

        var boxWidth = res->Width * scale;
        var drawnWidth = MathF.Min(textWidth * scale, boxWidth);
        // Names are centered in their text box.
        var textLeft = res->ScreenX + (boxWidth - drawnWidth) / 2f;
        var centerY = res->ScreenY + res->Height * scale / 2f;

        var height = IconHeight * scale;
        var width = height * IconTextureSize.X / IconTextureSize.Y;
        var active = new[] { status.Gagged, status.Restrained, status.Leashed };
        var x = textLeft - Gap * scale;
        for (var slot = 2; slot >= 0; slot--)
        {
            var node = Icon(index, slot);
            if (!active[slot] || iconIds[slot] == 0)
            {
                node.IsVisible = false;
                continue;
            }
            x -= width;
            node.Size = new Vector2(width, height);
            node.Position = new Vector2(x, centerY - height / 2f);
            node.IsVisible = true;
            x -= Gap * scale;
        }
    }

    private static float AccumulatedScale(AtkResNode* node)
    {
        var scale = 1f;
        for (var n = node; n != null; n = n->ParentNode)
            scale *= n->ScaleX > 0 ? n->ScaleX : 1f;
        return scale;
    }

    private IconImageNode Icon(int index, int slot)
    {
        ref var node = ref icons[index * 3 + slot];
        if (node is null)
        {
            node = new IconImageNode { TextureSize = IconTextureSize, IconId = iconIds[slot], IsVisible = false };
            node.AttachNode(this);
        }
        return node;
    }

    private void HideSlot(int index)
    {
        for (var slot = 0; slot < 3; slot++)
            if (icons[index * 3 + slot] is { IsVisible: true } node)
                node.IsVisible = false;
    }

    private void HideAll()
    {
        foreach (var node in icons)
            if (node is { IsVisible: true })
                node.IsVisible = false;
    }

    /// From the Status sheet by English name, so it works in any client language. 0 = not found, not drawn.
    public static uint[] ResolveIconIds()
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Status>(ClientLanguage.English);
        uint Find(string name) => sheet.FirstOrDefault(s => s.Icon != 0 && s.Name.ExtractText() == name).Icon;
        var ids = new[] { Find("Silence"), Find("Fetters"), Find("Bind") };
        if (ids.Any(id => id == 0))
            Plugin.Log.Warning($"Status icons: some icons weren't found in the Status sheet ({string.Join(", ", ids)}).");
        return ids;
    }
}

public sealed class StatusIconRenderer : IDisposable
{
    private readonly OverlayController? controller;

    public StatusIconRenderer(StatusIndicatorState state)
    {
        try
        {
            KamiToolKitLibrary.Initialize(Plugin.PluginInterface, "Oathbound");
            var iconIds = StatusIconOverlay.ResolveIconIds();
            controller = new OverlayController();
            controller.CreateNode(() => new StatusIconOverlay(state, iconIds));
        }
        catch (Exception ex)
        {
            // Fails closed: no icons, everything else keeps working.
            controller = null;
            Plugin.Log.Warning(ex, "Status icons: overlay failed to initialize - nameplate icons are off for this session.");
        }
    }

    public void Dispose()
    {
        controller?.Dispose();
        KamiToolKitLibrary.Dispose();
    }
}
