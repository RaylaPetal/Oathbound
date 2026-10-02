using System;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

/// The main window: character/pairing header and the nav grid. Module content opens in ModuleWindow.
/// No panic button on purpose - panic is the typed /oathboundpanic safeword, so it can't be hit by accident.
public class CollarWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ModuleWindow moduleWindow;

    public void OpenMainWindow() => IsOpen = true;

    /// TutorialDriver's single entry point for switching modules.
    public void SetActiveModuleForTutorial(string moduleId)
    {
        moduleWindow.Show(moduleId);
        // The tutorial's Permissions step opens Settings on that tab.
        if (moduleId == "permissions")
            plugin.SettingsWindow.ShowPermissionsTab();
    }

    private string? teleportResolveError;

    /// Armed by the first click until this time; a second click before it sends.
    private DateTime revertAllConfirmUntil = DateTime.MinValue;
    private bool revealSafeword;

    /// Order matches the ChatChannel enum; the combo indexes into it.
    private static readonly string[] ChatChannelNames = ["Tell", "Party", "Alliance", "Linkshell", "Cross-world Linkshell"];

    private static readonly (string Id, FontAwesomeIcon Icon, string Tooltip)[] NavItems =
    [
        ("title", FontAwesomeIcon.Heading, "Title"),
        ("outfit", FontAwesomeIcon.Tshirt, "Outfit"),
        ("animation", FontAwesomeIcon.TheaterMasks, "Animation"),
        ("moodles", FontAwesomeIcon.Smile, "Moodles"),
        ("restraints", FontAwesomeIcon.Handcuffs, "Restraints"),
        ("toycontrol", FontAwesomeIcon.Plug, "Toy Control"),
        ("customtriggers", FontAwesomeIcon.BoltLightning, "Custom Triggers"),
        ("collar", FontAwesomeIcon.Lock, "Collar"),
        ("follow", FontAwesomeIcon.Link, "Follow / Leash"),
        ("reactions", FontAwesomeIcon.Magic, "Reactions"),
        ("sync", FontAwesomeIcon.CloudDownloadAlt, "Sync"),
        ("favorites", FontAwesomeIcon.Star, "Favorites"),
    ];

    public CollarWindow(Plugin plugin, ModuleWindow moduleWindow) : base("Oathbound###CollarWindow")
    {
        this.plugin = plugin;
        this.moduleWindow = moduleWindow;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            Click = _ => plugin.ToggleSettingsUi(),
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
        });
    }

    public void Dispose() { }

    /// Content height measured at the end of last frame's Draw(); PreDraw adds the title bar and bottom padding.
    private float lastContentHeight = 400f;

    private const float MinWidth = 465f;

    /// Height is pinned to the content in both directions; a grow-only version let one bad first-frame
    /// measurement stick. Set explicitly because SizeConstraints doesn't resize a size persisted in imgui.ini.
    public override void PreDraw()
    {
        Theme.PushWindowStyle();
        var titleBarHeight = ImGui.GetFrameHeight();
        var bottomPadding = ImGui.GetStyle().WindowPadding.Y;
        var height = titleBarHeight + lastContentHeight + bottomPadding;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(MinWidth, height), MaximumSize = new Vector2(float.MaxValue, height) };

        if (LastSize.Y > 0 && (Math.Abs(LastSize.Y - height) > 0.5f || LastSize.X < MinWidth))
            ImGui.SetNextWindowSize(new Vector2(Math.Max(LastSize.X, MinWidth), height), ImGuiCond.Always);
    }

    public override void PostDraw() => Theme.PopWindowStyle();

    /// Read by SubControlWindow to dock against this window.
    public Vector2 LastPosition { get; private set; }
    public Vector2 LastSize { get; private set; }

    public override void Draw()
    {
        LastPosition = ImGui.GetWindowPos();
        LastSize = ImGui.GetWindowSize();

        DrawCharacterHeader();
        ImGui.Spacing();

        // Dependency gating is re-evaluated every frame so a tile re-enables once its plugin appears.
        if (NavBar.Draw(NavItems, id => DependencyGates.ModuleBlockedReason(plugin, id)) is { } clicked)
        {
            // Opens FavoritesWindow, not QuickAccessMenu's popup, whose links only make sense outside the main window.
            if (clicked == "favorites")
                plugin.FavoritesWindow.IsOpen = true;
            else
                moduleWindow.Show(clicked);
        }

        // Not on the appearing frame: widths aren't settled, so wrapped text measures far too tall.
        if (!ImGui.IsWindowAppearing())
            lastContentHeight = ImGui.GetCursorPosY();
    }

    private void DrawCharacterHeader()
    {
        var pending = plugin.PairingService.Pending;
        var config = plugin.Configuration;
        var character = CharacterHeaderModel.Current();
        var sameRoleWarning = pending is { } p && p.SenderRole == config.Role;

        ImGui.PushID("characterHeader");
        if (!ImGui.BeginTable("banner", 1, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.PopID();
            return;
        }
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Theme.CardBg));
        ImGui.TableNextColumn();

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.AccentHover);
        IconGlyph.Text(FontAwesomeIcon.UserCircle, character.Name ?? "Character loading…");
        ImGui.PopStyleColor();

        if (character.IsAvailable)
        {
            var details = character.HomeWorld is { Length: > 0 } ? character.HomeWorld : "Home world unavailable";
            if (character.FreeCompany is { Length: > 0 })
                details += $"  ·  «{character.FreeCompany}»";
            IconGlyph.WrappedDisabled(details);
        }
        else
        {
            IconGlyph.WrappedDisabled("Local character details will appear after login. Pairing and safety controls remain available.");
        }

        ImGui.Spacing();
        ImGui.Separator();

        if (pending is { } request)
        {
            var roleLabel = request.SenderRole == PluginRole.Owner ? "your Owner" : "your Sub";
            var expiresIn = TimeSpan.FromSeconds(Math.Max(0, request.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            var invitationExpired = request.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            IconGlyph.WrappedColored(Theme.Warning, $"Pending request: {request.Name}@{request.World} wants to pair as {roleLabel} (expires in {expiresIn.Minutes}m {expiresIn.Seconds}s).");
            if (sameRoleWarning)
                IconGlyph.WrappedColored(Theme.Danger, $"You're both set to {config.Role} in Settings - one of you should switch, or nothing will ever trigger.");

            using (ImRaii.Disabled(invitationExpired))
                if (ImGui.Button("Accept"))
                    Plugin.FireAndForget(plugin.PairingService.AcceptPendingAsync(System.Threading.CancellationToken.None));
            IconGlyph.HelpMarker("Trusts this sender as your paired peer from now on. Either direction can be ended any time from Settings' Unpair section - panic no longer does this, it only reverts your current outfit/title/movement-lock/restraint state (a locked collar stays on).");
            ImGui.SameLine();
            if (ImGui.Button("Reject"))
                plugin.PairingService.DismissPending();
            ImGui.Spacing();
            ImGui.Separator();
        }

        DrawPairingsList(config);
        DrawSubControlToggle();

        ImGui.Spacing();
        ImGui.Separator();
        IconGlyph.Text(FontAwesomeIcon.ShieldAlt, "Safeword");
        SafewordEditor.Draw(config, "mainHeader", ref revealSafeword);
        IconGlyph.HelpMarker("This only configures the typed /oathboundpanic command; editing it never triggers panic or changes pairing.");

        DrawTeleportJourneyRow();

        if (config.ResolveActiveDirection() == PairingDirection.OwnerSide)
        {
            ImGui.Spacing();
            ImGui.Separator();
            var channelIndex = (int)config.OutgoingChannel;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.Combo("Send commands via##outgoingChannel", ref channelIndex, ChatChannelNames, ChatChannelNames.Length))
            {
                config.OutgoingChannel = (ChatChannel)channelIndex;
                config.Save();
            }
            IconGlyph.HelpMarker("Which channel your commands are sent on, for every Sub you own. They listen on all of these already, so nothing needs to change on their side. Linkshell/Cross-world Linkshell number is set in Settings.");
            DrawTeleportHeaderAction();
        }

        ImGui.Spacing();
        ImGui.EndTable();
        ImGui.PopID();
    }

    /// A "your peer ended this" notice is shown per pairing, active or not.
    private void DrawPairingsList(PluginConfig config)
    {
        var pairedEntries = config.Pairings.Where(p => p.IsPaired).ToList();
        var notices = plugin.ChatCommandListener.PeerUnpairedNotices;

        if (pairedEntries.Count == 0 && notices.Count == 0)
        {
            IconGlyph.WrappedColored(Theme.TextMuted, "Not paired");
            IconGlyph.WrappedDisabled("Send or accept a relay invitation from Settings when you're ready.");
            return;
        }

        if (pairedEntries.Count > 1)
        {
            var activeIndex = Math.Max(0, pairedEntries.FindIndex(p => p.Id == config.ActivePairingId));
            var labels = pairedEntries.Select(PairingLabel).ToArray();
            ImGui.SetNextItemWidth(320f);
            if (ImGui.Combo("Active pairing", ref activeIndex, labels, labels.Length))
            {
                config.ActivePairingId = pairedEntries[activeIndex].Id;
                config.Save();
            }
            IconGlyph.HelpMarker("Outgoing commands target this pairing, and shared category tabs show its Owner/Sub view.");
        }
        else if (pairedEntries.Count == 1)
        {
            IconGlyph.WrappedColored(Theme.Success, PairingLabel(pairedEntries[0]));
        }

        foreach (var (pairingId, notice) in notices)
        {
            var pairing = config.FindPairingById(pairingId);
            ImGui.PushID(pairingId.GetHashCode());
            var peerLabel = notice.PeerRole == PluginRole.Owner ? "Your Owner" : "Your Sub";
            IconGlyph.WrappedColored(Theme.Warning, $"{peerLabel} ({pairing?.PeerName}@{pairing?.PeerWorld}) ended this pairing on their side.");
            if (ImGui.SmallButton("Dismiss##peerUnpairedNotice"))
                plugin.ChatCommandListener.DismissPeerUnpairedNotice(pairingId);
            ImGui.PopID();
        }
    }

    private static string PairingLabel(PairingState p) => p.Direction == PairingDirection.OwnerSide
        ? $"Owns: {p.PeerName}@{p.PeerWorld}"
        : $"Owned by: {p.PeerName}@{p.PeerWorld}";

    /// Owner-side only. Read lazily through `plugin` because SubControlWindow is constructed after this window.
    private void DrawSubControlToggle()
    {
        if (plugin.Configuration.ResolveActiveDirection() != PairingDirection.OwnerSide)
            return;

        const float size = 24f;
        var isOpen = plugin.SubControlWindow.IsOpen;
        var icon = isOpen ? FontAwesomeIcon.ArrowLeft : FontAwesomeIcon.ArrowRight;

        // Two clicks - it undoes a lot at once, so a stray click shouldn't.
        var confirming = DateTime.UtcNow < revertAllConfirmUntil;
        var revertLabel = confirming ? "Click again to revert" : "Revert all";
        var revertWidth = ImGui.CalcTextSize(revertLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        var canSend = plugin.Configuration.ActivePairing is { Direction: PairingDirection.OwnerSide };
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - size - ImGui.GetStyle().ItemSpacing.X - revertWidth);
        using (ImRaii.Disabled(!canSend))
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Danger, confirming))
        {
            if (ImGui.Button($"{revertLabel}##revertAll", new Vector2(revertWidth, size)))
            {
                if (confirming)
                {
                    plugin.ChatSender.Send(plugin.ChatComposer.Compose("revert all"));
                    revertAllConfirmUntil = DateTime.MinValue;
                }
                else
                {
                    revertAllConfirmUntil = DateTime.UtcNow.AddSeconds(5);
                }
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canSend
                ? "Reverts everything on your active Sub back to nothing: restraints, outfit (back to their normal look), title, leash, animation, toy, and moodles.\nThe collar and the pairing are never touched. Each part only applies if your Sub allows that category."
                : "Select an Owner-side pairing first.");
        ImGui.SameLine();

        if (IconGlyph.Button(icon, new Vector2(size, size)))
            plugin.SubControlWindow.IsOpen = !isOpen;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isOpen ? "Close Sub Control" : "Open Sub Control - every configured command in one place");
    }

    /// Shown while any journey runs, whatever the active pairing. No confirmation: it's the way out of a stuck navigation.
    private void DrawTeleportJourneyRow()
    {
        var teleport = plugin.TeleportCommand;
        if (!teleport.IsInProgress)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        var stuck = teleport.Stage == TeleportStage.Stuck;
        IconGlyph.Text(FontAwesomeIcon.Route, "Teleporting to your Owner");
        IconGlyph.WrappedColored(stuck ? Theme.Warning : Theme.TextMuted, TeleportStageLabel(teleport));

        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Danger))
            if (ImGui.Button("Stop teleport##teleportStop"))
                teleport.Stop("stopped from the header");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stops navigation right away and gives you back control of your movement.");
    }

    private static string TeleportStageLabel(TeleportCommand teleport) => teleport.Stage switch
    {
        TeleportStage.ChangingWorld => "Changing world...",
        TeleportStage.TravelingToWard => "Traveling to your Owner's ward...",
        TeleportStage.Teleporting => "Teleporting...",
        TeleportStage.ChangingInstance => "Changing instance...",
        TeleportStage.PreparingNav => teleport.NavBuildProgress is var p and >= 0f and < 1f
            ? $"Preparing navigation for this zone ({p * 100f:0}%)... movement stays locked."
            : "Preparing navigation...",
        TeleportStage.Mounting => "Mounting...",
        TeleportStage.Navigating => "Navigating to your Owner...",
        TeleportStage.Stuck => "Navigation seems stuck - retrying. Movement stays locked; press Stop if it doesn't recover.",
        TeleportStage.Arriving => "Arriving...",
        _ => "",
    };

    /// Send logic lives in TeleportSendAction, shared with the quick-access menu.
    private void DrawTeleportHeaderAction()
    {
        // No Lifestream/vnavmesh check: the Owner only reports its location.
        IconGlyph.Text(FontAwesomeIcon.MapMarkerAlt, "Teleport");
        var canSend = DrawOwnerCanSendBanner();
        using (ImRaii.Disabled(!canSend))
        {
            if (ImGui.Button("Teleport Sub to me"))
            {
                var (success, error) = TeleportSendAction.TryResolveAndSend(plugin);
                teleportResolveError = success ? null : error;
            }
        }
        ImGui.SameLine();
        DrawFavoriteFixedActionToggle(FixedActionIds.Teleport);

        if (teleportResolveError is not null)
            IconGlyph.WrappedColored(Theme.Warning, teleportResolveError);
    }

    /// Duplicated from ModuleWindow for the header's Teleport action. Returns whether Send should be enabled.
    private bool DrawOwnerCanSendBanner()
    {
        var canSend = plugin.Configuration.ActivePairing is { Direction: PairingDirection.OwnerSide };
        if (!canSend)
            IconGlyph.WrappedColored(Theme.Warning, "No /tell target yet - Send is disabled until an Owner-side pairing is active (select one in the header, or pair from Settings' handshake if you have none). Copy still works any time.");
        return canSend;
    }

    /// Duplicated from ModuleWindow for the header's Teleport action.
    private void DrawFavoriteFixedActionToggle(string favoriteId)
    {
        var favorites = plugin.Configuration.QuickCommands.FavoriteFixedActions;
        var isFavorite = favorites.Contains(favoriteId);
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Warning, isFavorite))
        {
            if (ImGui.SmallButton($"{(isFavorite ? "Favorited" : "Favorite")}##fav_{favoriteId}"))
            {
                if (isFavorite) favorites.Remove(favoriteId);
                else favorites.Add(favoriteId);
                plugin.Configuration.Save();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isFavorite ? "Remove from favorites" : "Add to favorites");
    }
}
