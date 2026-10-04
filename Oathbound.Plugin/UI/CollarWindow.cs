using System;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

/// The main window: character/pairing header and the nav grid. Module content opens in ModuleWindow.
/// No panic button on purpose - panic is the typed /obpanic safeword, so it can't be hit by accident.
public class CollarWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ModuleWindow moduleWindow;

    public void OpenMainWindow() => IsOpen = true;

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
        ("rulebook", FontAwesomeIcon.Book, "Rulebook"),
        ("sync", FontAwesomeIcon.CloudDownloadAlt, "Sync"),
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
        // Opens FavoritesWindow, not QuickAccessMenu's popup, whose links only make sense outside the main window.
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Star,
            Click = _ => plugin.FavoritesWindow.IsOpen = true,
            ShowTooltip = () => ImGui.SetTooltip("Favorites"),
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
        // Dalamud multiplies SizeConstraints by the global scale, but `height` is already measured in screen pixels.
        var scale = ImGuiHelpers.GlobalScale;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(MinWidth, height / scale), MaximumSize = new Vector2(float.MaxValue, height / scale) };

        var minWidth = MinWidth * scale;
        if (LastSize.Y > 0 && (Math.Abs(LastSize.Y - height) > 0.5f || LastSize.X < minWidth))
            ImGui.SetNextWindowSize(new Vector2(Math.Max(LastSize.X, minWidth), height), ImGuiCond.Always);
    }

    public override void PostDraw() => Theme.PopWindowStyle();

    /// Read by SubControlWindow to dock against this window.
    public Vector2 LastPosition { get; private set; }
    public Vector2 LastSize { get; private set; }

    public override void Draw()
    {
        LastPosition = ImGui.GetWindowPos();
        LastSize = ImGui.GetWindowSize();

        // Title-bar buttons aren't ImGui items, so the settings cog's area is reported by position.
        var titleBarHeight = ImGui.GetFrameHeight();
        TutorialService.AnchorRect(TutorialAnchors.MainSettings, LastPosition + new Vector2(LastSize.X - titleBarHeight * 3f, 0), LastPosition + new Vector2(LastSize.X, titleBarHeight));
        TutorialService.AnchorRect(TutorialAnchors.NavFavorites, LastPosition + new Vector2(LastSize.X - titleBarHeight * 4f, 0), LastPosition + new Vector2(LastSize.X - titleBarHeight * 3f, titleBarHeight));

        DrawCharacterHeader();
        ImGui.Spacing();

        // Dependency gating is re-evaluated every frame so a tile re-enables once its plugin appears.
        var clicked = NavBar.Draw(NavItems, id => DependencyGates.ModuleBlockedReason(plugin, id));
        TutorialService.Anchor(TutorialAnchors.MainNav);
        if (clicked is not null)
            moduleWindow.Show(clicked);

        // Not on the appearing frame: widths aren't settled, so wrapped text measures far too tall.
        if (!ImGui.IsWindowAppearing())
            lastContentHeight = ImGui.GetCursorPosY();
    }

    /// Right-aligned on the name's line when it fits, else on its own line, so it never pushes the name aside.
    private void DrawRelayActivity()
    {
        if (plugin.RelayActivity.Current is not { } count)
            return;
        var text = $"{count} active";
        var dot = ImGui.GetTextLineHeight() * 0.35f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = dot * 2 + spacing + ImGui.CalcTextSize(text).X;
        var right = ImGui.GetContentRegionMax().X;

        ImGui.SameLine();
        if (ImGui.GetCursorPosX() + spacing + width > right)
            ImGui.NewLine();
        ImGui.SetCursorPosX(right - width);

        var start = ImGui.GetCursorScreenPos();
        var center = start + new Vector2(dot, ImGui.GetTextLineHeight() / 2);
        ImGui.GetWindowDrawList().AddCircleFilled(center, dot, ImGui.GetColorU32(Theme.Success));
        ImGui.Dummy(new Vector2(dot * 2, ImGui.GetTextLineHeight()));
        var hovered = ImGui.IsItemHovered();
        ImGui.SameLine(0, spacing);
        ImGui.TextDisabled(text);
        if (hovered || ImGui.IsItemHovered())
            ImGui.SetTooltip("Oathbound players whose plugin checked in with the relay in the last half hour or so. Counts installs, never who they are.");
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
        TutorialService.Anchor(TutorialAnchors.MainCharacter);
        DrawRelayActivity();

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
            IconGlyph.HelpMarker("Pairs you with this person. You can unpair any time from Settings.");
            ImGui.SameLine();
            if (ImGui.Button("Reject"))
                plugin.PairingService.DismissPending();
            ImGui.Spacing();
            ImGui.Separator();
        }

        ImGui.BeginGroup();
        DrawPairingsList(config);
        ImGui.EndGroup();
        TutorialService.Anchor(TutorialAnchors.MainPairing);
        DrawCollarWarnings(config);
        DrawSubControlToggle();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.BeginGroup();
        IconGlyph.Text(FontAwesomeIcon.ShieldAlt, "Safeword");
        SafewordEditor.Draw(config, "mainHeader", ref revealSafeword);
        ImGui.EndGroup();
        TutorialService.Anchor(TutorialAnchors.MainSafeword);
        IconGlyph.HelpMarker("This only configures the typed /obpanic command; editing it never triggers panic or changes pairing.");

        DrawTeleportJourneyRow();

        if (config.ResolveActiveDirection() == PairingDirection.OwnerSide)
        {
            ImGui.Spacing();
            ImGui.Separator();
            var channelIndex = (int)config.OutgoingChannel;
            Layout.ItemWidth(200);
            if (ImGui.Combo("Send commands via##outgoingChannel", ref channelIndex, ChatChannelNames, ChatChannelNames.Length))
            {
                config.OutgoingChannel = (ChatChannel)channelIndex;
                config.Save();
            }
            TutorialService.Anchor(TutorialAnchors.MainChannel);
            IconGlyph.HelpMarker("The chat channel your commands go out on. Your Sub listens on all of them.");
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
            IconGlyph.WrappedDisabled("Pair from Settings when you're ready.");
            return;
        }

        if (pairedEntries.Count > 1)
        {
            var activeIndex = Math.Max(0, pairedEntries.FindIndex(p => p.Id == config.ActivePairingId));
            var labels = pairedEntries.Select(PairingLabel).ToArray();
            Layout.ItemWidth(320);
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

    /// Owner side: what each Sub's client last reported about the collar, through the relay.
    private void DrawCollarWarnings(PluginConfig config)
    {
        foreach (var pairing in config.Pairings.Where(p => p.IsPaired && p.Direction == PairingDirection.OwnerSide))
        {
            if (plugin.OwnerCollarStatus.For(pairing) is not { } status)
                continue;

            if (status.State == Relay.CollarStatusReporter.Broken)
            {
                IconGlyph.WrappedColored(Theme.StatusMissing, $"{pairing.PeerName}'s collar is unlocked");
                IconGlyph.HelpMarker($"Their collar came off without you unlocking it ({status.StateAt.ToLocalTime():g}). This clears once it's locked again.");
            }
            else if (status.State == Relay.CollarStatusReporter.Unlocked)
            {
                IconGlyph.WrappedColored(Theme.Warning, $"{pairing.PeerName}'s collar is unlocked");
                IconGlyph.HelpMarker($"You unlocked it ({status.StateAt.ToLocalTime():g}). This clears once you lock it again.");
            }
            else if (OwnerCollarStatusStore.IsStale(status) && status.CheckinAt is { } checkin)
            {
                IconGlyph.WrappedDisabled($"{pairing.PeerName}'s collar status unknown since {checkin.ToLocalTime():g}");
                IconGlyph.HelpMarker("Their plugin hasn't checked in for a while. They may just not have played - or their plugin is turned off.");
            }
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
        TutorialService.Anchor(TutorialAnchors.MainRevertAll);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canSend
                ? "Reverts everything on your active Sub back to nothing: restraints, outfit (back to their normal look), title, leash, animation, toy, and moodles.\nThe collar and the pairing are never touched. Each part only applies if your Sub allows that category."
                : "Select an Owner-side pairing first.");
        ImGui.SameLine();

        if (IconGlyph.Button(icon, new Vector2(size, size)))
            plugin.SubControlWindow.IsOpen = !isOpen;
        TutorialService.Anchor(TutorialAnchors.MainSubControl);
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
                teleport.StopBySub("stopped from the header");
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
            IconGlyph.WrappedColored(Theme.Warning, "No Sub to send to yet - pick an Owner-side pairing in the header, or pair in Settings. Copy still works.");
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
