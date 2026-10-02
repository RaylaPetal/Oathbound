using System;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
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
    }

    public void Dispose() { }

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void Draw()
    {
        var isOwnerMode = plugin.Configuration.ResolveActiveDirection() == PairingDirection.OwnerSide;
        if (!isOwnerMode)
        {
            IconGlyph.WrappedDisabled("Favorited commands are an Owner-side concept - favorite one from any module's Quick section while paired as Owner to see it here.");
            return;
        }

        var canSend = plugin.Configuration.ActivePairing is { Direction: PairingDirection.OwnerSide };
        var favoritesByCategory = QuickAccessMenu.CategorizedFavorites(plugin.Configuration.QuickCommands);
        var teleportFavorited = plugin.Configuration.QuickCommands.FavoriteFixedActions.Contains(FixedActionIds.Teleport);

        if (favoritesByCategory.Count == 0 && !teleportFavorited)
        {
            IconGlyph.WrappedDisabled("Nothing favorited yet - favorite a command from any module's Quick section to see it here.");
            return;
        }

        if (!canSend)
            IconGlyph.WrappedColored(Theme.Warning, "No /tell target yet - Send is disabled until an Owner-side pairing is active.");

        foreach (var (label, favorites) in favoritesByCategory)
        {
            if (!ImGui.CollapsingHeader($"{label} ({favorites.Count})###favCategory_{label}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            ImGui.Indent();
            foreach (var cmd in favorites)
                DrawFavoriteRow(cmd, canSend);
            ImGui.Unindent();
        }

        if (teleportFavorited)
            DrawTeleportRow(canSend);
    }

    private void DrawFavoriteRow(QuickCommand cmd, bool canSend)
    {
        var messages = plugin.ChatComposer.ComposeAll(OwnerMoodleOverride.ForFavoriteSend(plugin.Configuration, cmd));
        var fits = ChatComposer.AllFit(messages);
        using (ImRaii.Disabled(!canSend || !fits))
        {
            if (ImGui.SmallButton($"Send##fav_{cmd.Label}_{cmd.Command}"))
                plugin.ChatSender.SendAll(messages);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(OwnerLockOption.DescribeFavorite(cmd) is { } favoriteLock ? $"{cmd.Label}  · {favoriteLock}" : cmd.Label);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(!fits ? "Command is too long for a safe chat payload." : canSend ? string.Join("\n", messages) : "No /tell target yet - pairing hasn't captured your Sub's name.");
    }

    /// Teleport has no static command text; resolved live via TeleportSendAction.
    private void DrawTeleportRow(bool canSend)
    {
        using (ImRaii.Disabled(!canSend))
        {
            if (ImGui.SmallButton("Send##favTeleport"))
            {
                var (success, error) = TeleportSendAction.TryResolveAndSend(plugin);
                if (!success)
                    Plugin.NotificationManager.AddNotification(new Notification
                    {
                        Title = "Oathbound",
                        Content = error ?? "Teleport failed.",
                        Type = NotificationType.Warning,
                        InitialDuration = TimeSpan.FromSeconds(5),
                    });
            }
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Teleport");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(canSend ? "Teleport your paired Sub to your current position." : "No /tell target yet - pairing hasn't captured your Sub's name.");
    }
}
