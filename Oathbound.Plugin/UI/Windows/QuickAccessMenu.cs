using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Commands;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Oathbound.Plugin.UI;

/// The favorites popup, opened from the server info bar entry.
/// Toggle() may run outside any ImGui frame (a DTR click callback), so it only flips a flag: calling popup APIs there
/// computes mismatched IDs or crashes the game. Every real popup call happens in Draw().
public static class QuickAccessMenu
{
    private const string PopupId = "CollarQuickAccessMenu";
    private static bool openRequested;
    private static bool closeRequested;

    public static void Toggle()
    {
        if (closeRequested || (!openRequested && IsLikelyOpen))
            closeRequested = true;
        else
            openRequested = true;
    }

    /// Best-effort; avoids re-requesting an open that's already pending.
    private static bool IsLikelyOpen { get; set; }

    public static void Draw(Plugin plugin)
    {
        if (openRequested)
        {
            openRequested = false;
            ImGui.OpenPopup(PopupId);
        }

        // `using` declarations so every return path pops these exactly once.
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, Theme.CardBg);
        using var popupRounding = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Theme.CardRounding);
        using var headerHovered = ImRaii.PushColor(ImGuiCol.HeaderHovered, Theme.TileBgHover);

        if (!ImGui.BeginPopup(PopupId))
        {
            IsLikelyOpen = false;
            closeRequested = false;
            return;
        }

        IsLikelyOpen = true;

        if (closeRequested)
        {
            closeRequested = false;
            IsLikelyOpen = false;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        // A Sub has no one to send Owner commands to, so the menu shows only the open-window shortcuts.
        var isOwnerMode = plugin.Configuration.ResolveActiveDirection() == PairingDirection.OwnerSide;
        if (isOwnerMode)
        {
            var favoritesByCategory = CategorizedFavorites(plugin.Configuration.QuickCommands);
            var teleportFavorited = plugin.Configuration.QuickCommands.FavoriteFixedActions.Contains(FixedActionIds.Teleport);

            if (favoritesByCategory.Count == 0 && !teleportFavorited)
            {
                ImGui.TextUnformatted("Nothing favorited yet");
            }
            else
            {
                foreach (var (label, favorites) in favoritesByCategory)
                {
                    if (!ImGui.BeginMenu($"{label} ({favorites.Count})"))
                        continue;

                    foreach (var cmd in favorites)
                        DrawFavoriteMenuItem(plugin, cmd);
                    ImGui.EndMenu();
                }

                // Teleport has no static command text, so it gets its own top-level entry.
                if (teleportFavorited)
                    DrawTeleportMenuItem(plugin);
            }
        }

        if (ImGui.MenuItem("Open main window"))
            plugin.OpenMainWindow();
        if (ImGui.MenuItem("Open settings"))
            plugin.ToggleSettingsUi();

        ImGui.EndPopup();
    }

    /// Fixed actions with static command text, grouped like their rows in the UI. Shared with FavoritesWindow.
    internal static readonly (string Id, string Label, string Category, string Command)[] FixedActions =
    [
        (FixedActionIds.CollarLock, "Collar lock", "Collar", "collar lock"),
        (FixedActionIds.CollarUnlock, "Collar unlock", "Collar", "collar unlock"),
        (FixedActionIds.ClearMoodle, "Clear moodles", "Moodles", "moodle clear"),
        (FixedActionIds.RestraintUnlock, "Unlock restraints", "Restraints", "restraint unlock"),
        (FixedActionIds.ClearTitle, "Clear title", "Title", "title clear"),
        (FixedActionIds.UnlockOutfit, "Unlock outfit", "Outfit", "outfit unlock"),
        (FixedActionIds.LeashDefault, "Leash", "Follow", ControlWords.Leash),
        (FixedActionIds.UnleashDefault, "Unleash", "Follow", ControlWords.Unleash),
        (FixedActionIds.CustomTriggerRevert, "Revert custom triggers", "Custom Triggers", "customtrigger revert"),
        (FixedActionIds.StopAnimation, "Stop animation", "Animation", $"gesture {CommandComposer.StopGestureWord}"),
    ];

    internal static List<(string Label, List<QuickCommand> Favorites)> CategorizedFavorites(OwnerQuickCommands quick)
    {
        (string Label, List<QuickCommand> List)[] categories =
        [
            ("Title", quick.Titles),
            ("Outfit", quick.Outfits),
            ("Animation", quick.Gestures),
            ("Follow", quick.Follow),
            ("Moodles", quick.Moodles),
            ("Custom moodles", quick.CustomMoodles),
            ("Restraints", quick.Restraints),
            ("Custom Trigger Bundles", quick.Aliases),
        ];

        return categories
            .Select(c => (c.Label, Favorites: c.List.Where(cmd => cmd.IsFavorite)
                .Concat(FavoritedFixedActionsFor(c.Label, quick.FavoriteFixedActions))
                .OrderBy(cmd => cmd.Label, StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .Where(c => c.Favorites.Count > 0)
            .ToList();
    }

    /// Built fresh each frame for display, never saved.
    private static IEnumerable<QuickCommand> FavoritedFixedActionsFor(string category, HashSet<string> favoriteIds) =>
        FixedActions.Where(a => a.Category == category && favoriteIds.Contains(a.Id))
            .Select(a => new QuickCommand { Label = a.Label, Command = a.Command });

    /// Every entry per category, favorite or not, so the "show everything" and favorites views always agree.
    internal static List<(string Label, List<QuickCommand> Commands)> CategorizedAll(OwnerQuickCommands quick)
    {
        (string Label, List<QuickCommand> List)[] categories =
        [
            ("Title", quick.Titles),
            ("Outfit", quick.Outfits),
            ("Animation", quick.Gestures),
            ("Follow", quick.Follow),
            ("Moodles", quick.Moodles),
            ("Custom moodles", quick.CustomMoodles),
            ("Restraints", quick.Restraints),
            ("Custom Trigger Bundles", quick.Aliases),
        ];

        return categories
            .Select(c => (c.Label, Commands: c.List
                .Concat(FixedActionsFor(c.Label))
                .OrderBy(cmd => cmd.Label, StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .ToList();
    }

    private static IEnumerable<QuickCommand> FixedActionsFor(string category) =>
        FixedActions.Where(a => a.Category == category)
            .Select(a => new QuickCommand { Label = a.Label, Command = a.Command });

    private static void DrawFavoriteMenuItem(Plugin plugin, QuickCommand cmd)
    {
        var label = OwnerLockOption.DescribeFavorite(cmd) is { } favoriteLock ? $"{cmd.Label}  · {favoriteLock}" : cmd.Label;
        if (!RestraintPieces.IsPickAtSend(cmd))
        {
            DrawSendMenuItem(plugin, new SendSpec($"fav_{cmd.Label}_{cmd.Command}", label) { Command = OwnerMoodleOverride.ForFavoriteSend(plugin.Configuration, cmd) });
            return;
        }
        if (!ImGui.BeginMenu($"{label}##favPieces_{cmd.Label}"))
            return;
        var pieces = RestraintPieces.Of(plugin.Configuration, cmd);
        if (pieces.Count == 0)
            ImGui.TextDisabled("No pieces shared");
        foreach (var piece in pieces)
        {
            var copy = cmd.ForPiece(piece.ItemId, piece.Name);
            if (DrawSendMenuItem(plugin, new SendSpec($"fav_{cmd.Label}_{piece.ItemId}", piece.Name) { Command = OwnerMoodleOverride.ForFavoriteSend(plugin.Configuration, copy) }))
                RestraintPieces.Sent(plugin.Configuration, cmd, copy);
        }
        ImGui.EndMenu();
    }

    /// The popup closes on click, so a failure is reported as a notification.
    private static void DrawTeleportMenuItem(Plugin plugin) =>
        DrawSendMenuItem(plugin, new SendSpec("favTeleport", "Teleport")
        {
            Help = "Teleport your paired Sub to your current position.",
            Custom = SendNotice.Teleport(plugin),
        });

    /// True when it was clicked and the send ran.
    private static bool DrawSendMenuItem(Plugin plugin, SendSpec spec)
    {
        var gate = SendGate.Evaluate(plugin, spec);
        bool clicked;
        using (ImRaii.Disabled(!gate.Enabled))
            clicked = ImGui.MenuItem($"{spec.Label}##{spec.Id}");
        SendGate.Tooltip(gate);
        if (!clicked || !gate.Enabled)
            return false;
        SendGate.Run(plugin, spec);
        return true;
    }
}
