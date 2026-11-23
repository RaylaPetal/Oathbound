using System;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using System.Numerics;

namespace Oathbound.Plugin.UI;

/// Persistent favorites window for the main window's nav entry. Shares QuickAccessMenu's data shaping so the two
/// never disagree about what's favorited.
public sealed class FavoritesWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public FavoritesWindow(Plugin plugin) : base("Favorites###CollarFavoritesWindow")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(360, 240), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.QuestionCircle,
            Click = _ => plugin.Tutorial.Start("favorites", plugin.Configuration.ResolveActiveDirection() == PairingDirection.OwnerSide),
            ShowTooltip = () => ImGui.SetTooltip("Tour of Favorites"),
        });
    }

    public void Dispose() { }

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void Draw()
    {
        // Title-bar buttons aren't ImGui items, so the ? button's area is reported by position.
        var windowPos = ImGui.GetWindowPos();
        var titleBarHeight = ImGui.GetFrameHeight();
        TutorialService.AnchorRect(TutorialAnchors.FavoritesHelp, windowPos + new Vector2(ImGui.GetWindowWidth() - titleBarHeight * 3f, 0),
            windowPos + new Vector2(ImGui.GetWindowWidth(), titleBarHeight));

        var isOwnerMode = plugin.Configuration.ResolveActiveDirection() == PairingDirection.OwnerSide;
        if (!isOwnerMode)
        {
            IconGlyph.WrappedDisabled("Favorites are for Owners - star a command in any module while paired as Owner.");
            return;
        }

        var favoritesByCategory = QuickAccessMenu.CategorizedFavorites(plugin.Configuration.QuickCommands);
        var teleportFavorited = plugin.Configuration.QuickCommands.FavoriteFixedActions.Contains(FixedActionIds.Teleport);

        if (favoritesByCategory.Count == 0 && !teleportFavorited)
        {
            IconGlyph.WrappedDisabled("Nothing favorited yet - favorite a command from any module's Quick section to see it here.");
            return;
        }

        if (SendReadiness.For(plugin).BlockedReason is { } reason)
            Status.Line(StatusKind.Caution, reason);

        foreach (var (label, favorites) in favoritesByCategory)
        {
            if (!ImGui.CollapsingHeader($"{label} ({favorites.Count})###favCategory_{label}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            ImGui.Indent();
            foreach (var cmd in favorites)
                DrawFavoriteRow(cmd);
            ImGui.Unindent();
        }

        if (teleportFavorited)
            DrawTeleportRow();
    }

    private void DrawFavoriteRow(QuickCommand cmd)
    {
        var spec = new SendSpec($"fav_{cmd.Label}_{cmd.Command}", "Send") { Command = OwnerMoodleOverride.ForFavoriteSend(plugin.Configuration, cmd) };
        ImGui.BeginGroup();
        SendButton.Draw(plugin, spec);
        ImGui.SameLine();
        ImGui.TextUnformatted(OwnerLockOption.DescribeFavorite(cmd) is { } favoriteLock ? $"{cmd.Label}  · {favoriteLock}" : cmd.Label);
        ImGui.EndGroup();
        TutorialService.Anchor(TutorialAnchors.FavoritesRow);
        SendGate.Tooltip(SendGate.Evaluate(plugin, spec));
    }

    /// Teleport has no static command text; resolved live via TeleportSendAction.
    private void DrawTeleportRow()
    {
        var spec = new SendSpec("favTeleport", "Send")
        {
            Help = "Teleport your paired Sub to your current position.",
            Custom = SendNotice.Teleport(plugin),
        };
        SendButton.Draw(plugin, spec);
        ImGui.SameLine();
        ImGui.TextUnformatted("Teleport");
        SendGate.Tooltip(SendGate.Evaluate(plugin, spec));
    }
}
