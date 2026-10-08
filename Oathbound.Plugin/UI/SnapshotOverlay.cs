using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;

namespace Oathbound.Plugin.UI;

/// A movable, resizable frame over the game screen; Capture saves what's inside it as a picture. Every Oathbound
/// window is hidden while it's open, so the frame can sit anywhere.
public sealed class SnapshotOverlay
{
    private const float Inset = 6f;

    private readonly Plugin plugin;
    private Action<string>? onCaptured;
    private Vector2 framePos;
    private Vector2 frameSize;
    private (bool User, bool Gpose, bool Cutscene) savedUiHide;
    private bool focusPending;

    public SnapshotOverlay(Plugin plugin) => this.plugin = plugin;

    public bool IsOpen { get; private set; }

    /// `onCaptured` gets the saved picture's file name in the images folder, on the framework thread.
    public void Open(Action<string> captured)
    {
        onCaptured = captured;
        var vp = ImGui.GetMainViewport();
        var height = vp.Size.Y * 0.6f;
        frameSize = new Vector2(height * ImageTile.Aspect, height);
        framePos = vp.Pos + (vp.Size - frameSize) / 2f;

        // Keep the frame up while the game's UI is hidden or in group pose, where a clean picture is usually taken.
        var ui = Plugin.PluginInterface.UiBuilder;
        savedUiHide = (ui.DisableUserUiHide, ui.DisableGposeUiHide, ui.DisableCutsceneUiHide);
        ui.DisableUserUiHide = ui.DisableGposeUiHide = ui.DisableCutsceneUiHide = true;
        IsOpen = true;
        focusPending = true;
    }

    public void Close()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        onCaptured = null;
        var ui = Plugin.PluginInterface.UiBuilder;
        (ui.DisableUserUiHide, ui.DisableGposeUiHide, ui.DisableCutsceneUiHide) = savedUiHide;
    }

    public void Draw()
    {
        if (!IsOpen)
            return;
        // Holding Shift lets the mouse through to the game, to turn the camera.
        if (ImGui.GetIO().KeyShift)
        {
            DrawHint("Release Shift to move the frame.");
            return;
        }

        var vp = ImGui.GetMainViewport();
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(vp.Pos);
        ImGui.SetNextWindowSize(vp.Size);
        // Focused, so Enter and Escape reach the frame instead of opening chat or the game menu.
        if (focusPending)
        {
            ImGui.SetNextWindowFocus();
            focusPending = false;
        }
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav;
        if (ImGui.Begin("##oathboundSnapshot", flags))
            DrawFrame(vp);
        ImGui.End();
    }

    private void DrawFrame(ImGuiViewportPtr vp)
    {
        var dl = ImGui.GetWindowDrawList();
        var handle = Layout.Scaled(18);
        var minHeight = Layout.Scaled(150);

        // Corners first: when items overlap, the first one submitted takes the mouse.
        for (var i = 0; i < 4; i++)
        {
            var corner = Corner(i);
            ImGui.SetCursorScreenPos(corner - new Vector2(handle / 2f));
            ImGui.InvisibleButton($"##corner{i}", new Vector2(handle));
            if (ImGui.IsItemHovered() || ImGui.IsItemActive())
                ImGui.SetMouseCursor(i is 0 or 3 ? ImGuiMouseCursor.ResizeNwse : ImGuiMouseCursor.ResizeNesw);
            if (ImGui.IsItemActive())
                ResizeFrom(Corner(3 - i), ImGui.GetMousePos(), minHeight, vp);
        }
        ImGui.SetCursorScreenPos(framePos);
        ImGui.InvisibleButton("##frame", frameSize);
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        if (ImGui.IsItemActive())
            framePos += ImGui.GetIO().MouseDelta;

        var nudge = ImGui.GetIO().KeyCtrl ? 10f : 1f;
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) framePos.X -= nudge;
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) framePos.X += nudge;
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) framePos.Y -= nudge;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) framePos.Y += nudge;
        framePos = Vector2.Clamp(framePos, vp.Pos, Vector2.Max(vp.Pos, vp.Pos + vp.Size - frameSize));

        // Everything outside the frame is dimmed. None of this is in the picture: it's taken before any of it is drawn.
        var dim = ImGui.GetColorU32(new Vector4(0, 0, 0, 0.55f));
        var end = framePos + frameSize;
        var vpEnd = vp.Pos + vp.Size;
        dl.AddRectFilled(vp.Pos, new Vector2(vpEnd.X, framePos.Y), dim);
        dl.AddRectFilled(new Vector2(vp.Pos.X, end.Y), vpEnd, dim);
        dl.AddRectFilled(new Vector2(vp.Pos.X, framePos.Y), new Vector2(framePos.X, end.Y), dim);
        dl.AddRectFilled(new Vector2(end.X, framePos.Y), new Vector2(vpEnd.X, end.Y), dim);
        dl.AddRect(framePos, end, ImGui.GetColorU32(Theme.Accent), 0f, ImDrawFlags.None, 2f);
        for (var i = 0; i < 4; i++)
            dl.AddCircleFilled(Corner(i), Layout.Scaled(5), ImGui.GetColorU32(Theme.AccentHover), 12);

        var hint = "Drag to move, drag a corner to resize. Hold Shift to turn the camera.";
        var hintSize = ImGui.CalcTextSize(hint);
        var hintPos = new Vector2(framePos.X + (frameSize.X - hintSize.X) / 2f, framePos.Y - hintSize.Y - Layout.Scaled(8));
        if (hintPos.Y < vp.Pos.Y)
            hintPos.Y = end.Y + Layout.Scaled(8);
        dl.AddText(hintPos + Vector2.One, ImGui.GetColorU32(new Vector4(0, 0, 0, 1)), hint);
        dl.AddText(hintPos, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), hint);

        var buttonSize = new Vector2(Layout.Scaled(110), ImGui.GetFrameHeight() * 1.4f);
        var gap = Layout.Scaled(12);
        var rowWidth = buttonSize.X * 2 + gap;
        var buttonsY = end.Y + Layout.Scaled(10);
        if (buttonsY + buttonSize.Y > vpEnd.Y)
            buttonsY = end.Y - buttonSize.Y - Layout.Scaled(10);
        ImGui.SetCursorScreenPos(new Vector2(framePos.X + (frameSize.X - rowWidth) / 2f, buttonsY));
        if (ImGui.Button("Capture", buttonSize) || ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))
        {
            Capture(vp);
            return;
        }
        ImGui.SameLine(0, gap);
        if (ImGui.Button("Cancel", buttonSize) || ImGui.IsKeyPressed(ImGuiKey.Escape))
            Close();
    }

    private Vector2 Corner(int i) => framePos + new Vector2(i is 1 or 3 ? frameSize.X : 0, i is 2 or 3 ? frameSize.Y : 0);

    /// Keeps the picture's aspect, with `anchor` (the opposite corner) staying put.
    private void ResizeFrom(Vector2 anchor, Vector2 mouse, float minHeight, ImGuiViewportPtr vp)
    {
        var delta = Vector2.Abs(mouse - anchor);
        var height = Math.Clamp(Math.Max(delta.Y, delta.X / ImageTile.Aspect), minHeight, Math.Min(vp.Size.Y, vp.Size.X / ImageTile.Aspect));
        frameSize = new Vector2(height * ImageTile.Aspect, height);
        framePos = new Vector2(mouse.X >= anchor.X ? anchor.X : anchor.X - frameSize.X, mouse.Y >= anchor.Y ? anchor.Y : anchor.Y - frameSize.Y);
    }

    private void Capture(ImGuiViewportPtr vp)
    {
        var callback = onCaptured;
        var inner0 = framePos + new Vector2(Inset);
        var inner1 = framePos + frameSize - new Vector2(Inset);
        var args = new ImGuiViewportTextureArgs
        {
            ViewportId = vp.ID,
            TakeBeforeImGuiRender = true,
            Uv0 = Vector2.Clamp((inner0 - vp.Pos) / vp.Size, Vector2.Zero, Vector2.One),
            Uv1 = Vector2.Clamp((inner1 - vp.Pos) / vp.Size, Vector2.Zero, Vector2.One),
        };
        Close();

        Task.Run(async () =>
        {
            string? file = null;
            try
            {
                using var shot = await Plugin.TextureProvider.CreateFromImGuiViewportAsync(args);
                file = await ImageTile.SaveCaptureAsync(shot);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Couldn't take a snapshot.");
            }
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (file is not null)
                    callback?.Invoke(file);
                else
                    Plugin.NotificationManager.AddNotification(new Notification
                    {
                        Title = "Snapshot not saved",
                        Content = "The picture couldn't be taken or saved. Choosing a picture file still works.",
                        Type = NotificationType.Warning,
                    });
            });
        });
    }

    private static void DrawHint(string text)
    {
        var vp = ImGui.GetMainViewport();
        var size = ImGui.CalcTextSize(text);
        var pos = vp.Pos + new Vector2((vp.Size.X - size.X) / 2f, Layout.Scaled(40));
        var dl = ImGui.GetForegroundDrawList(vp);
        dl.AddText(pos + Vector2.One, ImGui.GetColorU32(new Vector4(0, 0, 0, 1)), text);
        dl.AddText(pos, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), text);
    }
}
