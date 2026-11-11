using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay.UiOverlay;
using KamiToolKit.Premade.Node;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.UI;

/// Darkens the game world under the game's own UI, so chat, hotbars and /obpanic stay usable. Full and Heavy also
/// hide nameplates, which would otherwise float over the dark and give away who's around.
public sealed unsafe class BlindfoldOverlay : OverlayNode
{
    private const float HeavyAlpha = 0.92f;
    /// Light's clear circle, as a fraction of the screen height.
    private const float ClearRadius = 0.12f;
    /// In the vignette texture, as a fraction of its half-size: clear inside Inner, fully dark from Outer.
    private const float Inner = 0.06f;
    private const float Outer = 0.12f;
    private const int VignetteSize = 1024;

    private readonly BlindfoldService blindfold;
    private readonly BackgroundImageNode fill;
    private readonly ImGuiImageNode vignette;
    private IDalamudTextureWrap? vignetteTexture;
    private bool hidNameplates;

    public BlindfoldOverlay(BlindfoldService blindfold)
    {
        this.blindfold = blindfold;
        fill = new BackgroundImageNode { Color = new Vector4(0f, 0f, 0f, 1f), IsVisible = false };
        fill.AttachNode(this);
        vignette = new ImGuiImageNode { FitTexture = true, IsVisible = false };
        vignette.AttachNode(this);
    }

    public override OverlayLayer OverlayLayer => OverlayLayer.BehindUserInterface;

    /// Hiding the game UI must not lift the blindfold; cutscenes and GPose are handled in OnUpdate.
    public override bool HideWithNativeUi => false;

    protected override void OnUpdate()
    {
        var level = Shown() ? blindfold.Level : null;
        var screen = ImGui.GetMainViewport().Size;
        Size = screen;

        if (level is null)
        {
            fill.IsVisible = false;
            vignette.IsVisible = false;
            ShowNameplates();
            return;
        }

        if (level == BlindfoldLevel.Light && PlayerOnScreen(out var center))
        {
            var size = 2f * ClearRadius * screen.Y / Inner;
            vignette.LoadTextureIfNeeded(ref vignetteTexture);
            vignette.Size = new Vector2(size, size);
            vignette.Position = center - new Vector2(size / 2f);
            vignette.IsVisible = vignetteTexture is not null;
            fill.IsVisible = vignetteTexture is null;
            fill.Color = new Vector4(0f, 0f, 0f, HeavyAlpha);
            ShowNameplates();
            return;
        }

        vignette.IsVisible = false;
        fill.Position = Vector2.Zero;
        fill.Size = screen;
        fill.Color = new Vector4(0f, 0f, 0f, level == BlindfoldLevel.Full ? 1f : HeavyAlpha);
        fill.IsVisible = true;
        if (level == BlindfoldLevel.Light)
            ShowNameplates();
        else
            HideNameplates();
    }

    private static bool Shown()
    {
        var c = Plugin.Condition;
        return !Plugin.ClientState.IsGPosing && !c[ConditionFlag.OccupiedInCutSceneEvent]
            && !c[ConditionFlag.WatchingCutscene] && !c[ConditionFlag.WatchingCutscene78];
    }

    private static bool PlayerOnScreen(out Vector2 center)
    {
        center = default;
        return Plugin.ObjectTable.LocalPlayer is { } me && Plugin.GameGui.WorldToScreen(me.Position + new Vector3(0f, 1f, 0f), out center);
    }

    private void HideNameplates()
    {
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("NamePlate").Address;
        if (addon == null || addon->RootNode == null)
            return;
        if (addon->RootNode->IsVisible())
        {
            addon->RootNode->ToggleVisibility(false);
            hidNameplates = true;
        }
    }

    /// Only undoes our own hiding.
    public void ShowNameplates()
    {
        if (!hidNameplates)
            return;
        hidNameplates = false;
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName("NamePlate").Address;
        if (addon != null && addon->RootNode != null)
            addon->RootNode->ToggleVisibility(true);
    }

    protected override void Dispose(bool disposing, bool isNativeDestructor)
    {
        ShowNameplates();
        base.Dispose(disposing, isNativeDestructor);
        vignetteTexture?.Dispose();
        vignetteTexture = null;
    }

    /// Black with a radial alpha ramp: clear in the middle, `HeavyAlpha` from `Outer` out.
    internal static IDalamudTextureWrap? CreateVignette()
    {
        try
        {
            var pixels = new byte[VignetteSize * VignetteSize * 4];
            var half = VignetteSize / 2f;
            for (var y = 0; y < VignetteSize; y++)
            for (var x = 0; x < VignetteSize; x++)
            {
                var d = MathF.Sqrt((x + 0.5f - half) * (x + 0.5f - half) + (y + 0.5f - half) * (y + 0.5f - half)) / half;
                var t = Math.Clamp((d - Inner) / (Outer - Inner), 0f, 1f);
                var alpha = t * t * (3f - 2f * t) * HeavyAlpha;
                pixels[(y * VignetteSize + x) * 4 + 3] = (byte)(alpha * 255f);
            }
            return Plugin.TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(VignetteSize, VignetteSize), pixels, "Oathbound blindfold");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Blindfold: couldn't build the Light texture - Light draws like Heavy.");
            return null;
        }
    }
}

internal static class BlindfoldOverlayExtensions
{
    /// Built once, on first use, and kept for the session.
    public static void LoadTextureIfNeeded(this ImGuiImageNode node, ref IDalamudTextureWrap? texture)
    {
        if (texture is not null)
            return;
        texture = BlindfoldOverlay.CreateVignette();
        if (texture is not null)
            node.LoadTexture(texture);
    }
}
