using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Commands;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
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
        var activePairing = plugin.Configuration.ActivePairing;
        var isOwnerMode = plugin.Configuration.ResolveActiveDirection() == PairingDirection.OwnerSide;
        if (isOwnerMode)
        {
            var canSend = activePairing is { Direction: PairingDirection.OwnerSide };
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
                        DrawFavoriteMenuItem(plugin, cmd, canSend);
                    ImGui.EndMenu();
                }

                // Teleport has no static command text, so it gets its own top-level entry.
                if (teleportFavorited)
                    DrawTeleportMenuItem(plugin, canSend);
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
        (FixedActionIds.ClearMoodle, "Clear moodle", "Moodles", "moodle clear"),
        (FixedActionIds.RestraintUnlock, "Restraint unlock", "Restraints", "restraint unlock"),
        (FixedActionIds.ClearTitle, "Clear title", "Title", "title clear"),
        (FixedActionIds.UnlockOutfit, "Unlock outfit", "Outfit", "outfit unlock"),
        (FixedActionIds.LeashDefault, "Leash", "Follow", ControlWords.Leash),
        (FixedActionIds.UnleashDefault, "Unleash", "Follow", ControlWords.Unleash),
        (FixedActionIds.CustomTriggerRevert, "Revert custom triggers", "Custom Triggers", "customtrigger revert"),
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

    private static void DrawFavoriteMenuItem(Plugin plugin, QuickCommand cmd, bool canSend)
    {
        var messages = plugin.ChatComposer.ComposeAll(OwnerMoodleOverride.ForFavoriteSend(plugin.Configuration, cmd));
        var fits = ChatComposer.AllFit(messages);
        using (ImRaii.Disabled(!canSend || !fits))
        {
            if (ImGui.MenuItem(OwnerLockOption.DescribeFavorite(cmd) is { } favoriteLock ? $"{cmd.Label}  · {favoriteLock}" : cmd.Label))
                plugin.ChatSender.SendAll(messages);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(!fits ? "Command is too long for a safe chat payload." : canSend ? string.Join("\n", messages) : "No /tell target yet - pairing hasn't captured your Sub's name.");
    }

    /// The popup closes on click, so a resolution failure is reported as a notification.
    private static void DrawTeleportMenuItem(Plugin plugin, bool canSend)
    {
        using (ImRaii.Disabled(!canSend))
        {
            if (ImGui.MenuItem("Teleport"))
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
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(canSend ? "Teleport your paired Sub to your current position." : "No /tell target yet - pairing hasn't captured your Sub's name.");
    }
}
