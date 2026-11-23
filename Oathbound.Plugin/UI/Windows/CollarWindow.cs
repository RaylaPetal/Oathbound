using System;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;
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


    /// Armed by the first click until this time; a second click before it sends.
    private DateTime revertAllConfirmUntil = DateTime.MinValue;
    private bool revealSafeword;

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
        ("cage", FontAwesomeIcon.Dungeon, "Cage"),
        ("games", FontAwesomeIcon.Dice, "Games"),
        ("rulebook", FontAwesomeIcon.Book, "Rulebook"),
        ("reactions", FontAwesomeIcon.Magic, "Reactions"),
        ("sync", FontAwesomeIcon.CloudDownloadAlt, "Sync"),
        (FavoritesNavId, FontAwesomeIcon.Star, "Favorites"),
        ("achievements", FontAwesomeIcon.Trophy, "Achievements"),
    ];

    /// Opens FavoritesWindow, not QuickAccessMenu's popup, whose links only make sense outside the main window.
    private const string FavoritesNavId = "favorites";

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
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.QuestionCircle,
            Click = _ => plugin.Tutorial.StartOverview(plugin.Configuration.ResolveActiveDirection()),
            ShowTooltip = () => ImGui.SetTooltip("Tour of Oathbound"),
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.LifeRing,
            Click = _ => HelpLinks.RequestOpen(),
            ShowTooltip = () => ImGui.SetTooltip("Help & bug reports"),
        });
    }

    public void Dispose() { }

    /// Where last frame's content ended, from the window's top edge (so it already includes the title bar); PreDraw
    /// adds the bottom padding.
    private float lastContentHeight = 400f;

    private const float MinWidth = 465f;

    /// Height is pinned to the content in both directions; a grow-only version let one bad first-frame
    /// measurement stick. Set explicitly because SizeConstraints doesn't resize a size persisted in imgui.ini.
    public override void PreDraw()
    {
        Theme.PushWindowStyle();
        var height = lastContentHeight + ImGui.GetStyle().WindowPadding.Y;
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

        // Title-bar buttons aren't ImGui items, so the area of the close button and our three buttons (cog among them)
        // is reported by position.
        var titleBarHeight = ImGui.GetFrameHeight();
        TutorialService.AnchorRect(TutorialAnchors.MainSettings, LastPosition + new Vector2(LastSize.X - titleBarHeight * 4f, 0), LastPosition + new Vector2(LastSize.X, titleBarHeight));

        HelpLinks.Draw(plugin);
        DrawCharacterHeader();
        moduleWindow.DrawStruggleRow("header");
        ImGui.Spacing();

        // Dependency gating is re-evaluated every frame so a tile re-enables once its plugin appears.
        var rulebookWaiting = plugin.RulebookService.NeedsAttention();
        var clicked = NavBar.Draw(NavItems, id => DependencyGates.ModuleBlockedReason(plugin, id),
            id => id switch { FavoritesNavId => TutorialAnchors.NavFavorites, "achievements" => TutorialAnchors.NavAchievements, _ => null },
            id => id == "rulebook" && rulebookWaiting ? ("!", "A rulebook version or an oath offer is waiting for your answer.") : null);
        TutorialService.Anchor(TutorialAnchors.MainNav);
        if (clicked == FavoritesNavId)
            plugin.FavoritesWindow.IsOpen = true;
        else if (clicked is not null)
            moduleWindow.Show(clicked);

        // Not on the appearing frame: widths aren't settled, so wrapped text measures far too tall.
        // Minus the spacing ImGui leaves after the last item, which would otherwise show as a gap under the nav grid.
        if (!ImGui.IsWindowAppearing())
            lastContentHeight = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y;
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
        ImGui.GetWindowDrawList().AddCircleFilled(center, dot, ImGui.GetColorU32(Theme.Ok));
        ImGui.Dummy(new Vector2(dot * 2, ImGui.GetTextLineHeight()));
        var hovered = ImGui.IsItemHovered();
        ImGui.SameLine(0, spacing);
        ImGui.TextDisabled(text);
        if (hovered || ImGui.IsItemHovered())
            ImGui.SetTooltip("Oathbound players whose plugin used the relay today or yesterday. Counts installs, never who they are.");
    }

    /// The support icons sit right-aligned under the relay count; the details wrap short of them.
    private static void DrawDetailsWithSupport(string details)
    {
        var start = ImGui.GetCursorPos();
        var right = ImGui.GetContentRegionMax().X;
        ImGui.SetCursorPosX(right - SupportLinks.Width);
        SupportLinks.Draw();

        ImGui.SetCursorPos(start);
        ImGui.PushTextWrapPos(right - SupportLinks.Width - ImGui.GetStyle().ItemSpacing.X);
        ImGui.TextDisabled(details);
        ImGui.PopTextWrapPos();

        var iconsBottom = start.Y + SupportLinks.Height + ImGui.GetStyle().ItemSpacing.Y;
        if (ImGui.GetCursorPosY() < iconsBottom)
            ImGui.SetCursorPosY(iconsBottom);
    }

    private void DrawCharacterHeader()
    {
        var config = plugin.Configuration;
        var character = CharacterHeaderModel.Current();

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

        string details;
        if (character.IsAvailable)
        {
            details = character.HomeWorld is { Length: > 0 } ? character.HomeWorld : "Home world unavailable";
            if (character.FreeCompany is { Length: > 0 })
                details += $"  ·  «{character.FreeCompany}»";
        }
        else
        {
            details = "Local character details will appear after login. Pairing and safety controls remain available.";
        }
        DrawDetailsWithSupport(details);

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.BeginGroup();
        DrawPairingsList(config);
        ImGui.EndGroup();
        TutorialService.Anchor(TutorialAnchors.MainPairing);
        DrawCharacterPrompts(config);
        DrawSafewordWarnings(config);
        DrawCollarWarnings(config);
        DrawSubControlToggle();
        moduleWindow.DrawHeaderOaths();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.BeginGroup();
        IconGlyph.Text(FontAwesomeIcon.ShieldAlt, "Safeword");
        SafewordEditor.Draw(config, "mainHeader", ref revealSafeword,
            "This only configures the typed /obpanic command; editing it never triggers panic or changes pairing.");
        ImGui.EndGroup();
        TutorialService.Anchor(TutorialAnchors.MainSafeword);

        DrawTeleportJourneyRow();
        DrawCageRow();

        if (config.ResolveActiveDirection() == PairingDirection.OwnerSide)
        {
            ImGui.Spacing();
            ImGui.Separator();
            DrawSendTarget(config);
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
            var activeIndex = Math.Max(0, pairedEntries.FindIndex(p => p.Id == config.ActivePairing?.Id));
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
            Status.Line(StatusKind.Ok, PairingLabel(pairedEntries[0]));
        }

        foreach (var (pairingId, notice) in notices)
        {
            var pairing = config.FindPairingById(pairingId);
            ImGui.PushID(pairingId.GetHashCode());
            var peerLabel = notice.PeerRole == PluginRole.Owner ? "Your Owner" : "Your Sub";
            Status.Line(StatusKind.Caution, $"{peerLabel} ({pairing?.PeerName}@{pairing?.PeerWorld}) ended this pairing on their side.");
            if (ImGui.Button("Dismiss##peerUnpairedNotice"))
                plugin.ChatCommandListener.DismissPeerUnpairedNotice(pairingId);
            ImGui.PopID();
        }
    }

    /// Linking the logged-in character, and (Sub side) confirming characters an Owner says are theirs.
    private void DrawCharacterPrompts(PluginConfig config)
    {
        var records = plugin.CharacterRecords;
        if (records.NeedsLinkPrompt && records.Current is { } current)
        {
            ImGui.PushID("linkCharacter");
            Status.Line(StatusKind.Caution, $"{current} isn't linked to your pairings yet. Link it so they can reach you on this character?");
            if (ImGui.Button("Link")) records.LinkCurrent();
            IconGlyph.HelpMarker("Your pairings learn this character is also you. Your restrictions follow you to every character either way.");
            ImGui.SameLine();
            if (ImGui.Button("Not now")) records.NotNowForCurrent();
            ImGui.SameLine();
            if (ImGui.Button("Never for this character")) records.NeverForCurrent();
            ImGui.PopID();
        }

        foreach (var (pairing, character) in CharacterRecordService.PendingOwnerCharacters(config).ToList())
        {
            ImGui.PushID($"pendingOwnerCharacter_{pairing.Id}_{character}");
            var owner = pairing.ConfirmedCharacters.FirstOrDefault()?.ToString() ?? "Your Owner";
            Status.Line(StatusKind.Caution, $"{owner} says {character} is also them. Its commands are ignored until you confirm.");
            if (ImGui.Button("Confirm")) records.Confirm(pairing, character);
            IconGlyph.HelpMarker("Commands from that character will count exactly like ones from your Owner. You can remove it again in Settings.");
            ImGui.SameLine();
            if (ImGui.Button("Reject")) records.Reject(pairing, character);
            ImGui.PopID();
        }
    }

    /// Owner side: the Sub's current character and whether their Oathbound can be reached.
    private void DrawSendTarget(PluginConfig config)
    {
        if (config.ActivePairing is not { Direction: PairingDirection.OwnerSide, IsPaired: true } pairing) return;
        if (pairing.PeerRelayState == PeerRelayState.NeedsUpdate)
        {
            Status.Line(StatusKind.Caution, $"{pairing.PeerName} needs to update Oathbound - nothing you send reaches them until they do.");
            PeerRelayStatus.DrawAskToUpdate(plugin, pairing);
            return;
        }
        if (pairing.ConfirmedCharacters.Count() < 2) return;
        IconGlyph.WrappedDisabled(SeenText(pairing));
    }

    private static string SeenText(PairingState pairing)
    {
        var target = pairing.FindPeerCharacter(pairing.PeerName, pairing.PeerWorld);
        var presence = target is not null && target.Is(pairing.PresenceName, pairing.PresenceWorld) ? pairing.PresenceAt : 0;
        var seen = Math.Max(presence, target?.LastSeenAt ?? 0);
        if (seen == 0) return $"{pairing.PeerName}@{pairing.PeerWorld}.";
        return $"On {pairing.PeerName}@{pairing.PeerWorld}, last seen {TimeText.Ago(seen)}.";
    }

    /// Owner side: a Sub's safeword, shown until dismissed, for every Sub rather than only the active one.
    private void DrawSafewordWarnings(PluginConfig config)
    {
        foreach (var pairing in config.Pairings.Where(p => p.IsPaired && p.Direction == PairingDirection.OwnerSide && OwnerSafewordAlerts.IsShowing(p)).ToList())
        {
            ImGui.PushID($"safeword_{pairing.Id}");
            Layout.TextWithActions($"{pairing.PeerName} used their safeword {TimeText.Ago(pairing.LastSafewordAt)}", Layout.ActionsWidth("Dismiss"),
                t => Status.Inline(StatusKind.Caution, t));
            if (ImGui.Button("Dismiss"))
                plugin.OwnerSafewordAlerts.Dismiss(pairing);
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
                Status.Line(StatusKind.Caution, $"{pairing.PeerName}'s collar is unlocked");
                IconGlyph.HelpMarker($"Their collar came off without you unlocking it ({TimeText.Date(status.StateAt)}). This clears once it's locked again.");
            }
            else if (status.State == Relay.CollarStatusReporter.Unlocked)
            {
                Status.Line(StatusKind.Caution, $"{pairing.PeerName}'s collar is unlocked");
                IconGlyph.HelpMarker($"You unlocked it ({TimeText.Date(status.StateAt)}). This clears once you lock it again.");
            }
            else if (OwnerCollarStatusStore.IsStale(status) && status.CheckinAt is { } checkin)
            {
                IconGlyph.WrappedDisabled($"{pairing.PeerName}'s collar status unknown since {TimeText.Date(checkin)}");
                IconGlyph.HelpMarker("Their plugin hasn't checked in for a while. They may just not have played - or their plugin is turned off.");
            }
        }
    }

    private static string PairingLabel(PairingState p) => p.Direction == PairingDirection.OwnerSide
        ? $"Owns: {p.PeerName}@{p.PeerWorld}"
        : $"Owned by: {p.PeerName}@{p.PeerWorld}";

    /// Right-aligned: chat, then (Owner side) Revert all and the Sub Control toggle. Read lazily through `plugin`
    /// because SubControlWindow is constructed after this window.
    private void DrawSubControlToggle()
    {
        const float size = 24f;
        var showChat = plugin.Configuration.Pairings.Any(p => p.IsPaired) || plugin.Chat.History.All.Any(c => c.Messages.Count > 0);
        if (plugin.Configuration.ResolveActiveDirection() != PairingDirection.OwnerSide)
        {
            if (!showChat) return;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - size);
            DrawChatIcon(size);
            return;
        }

        var isOpen = plugin.SubControlWindow.IsOpen;
        var icon = isOpen ? FontAwesomeIcon.ArrowLeft : FontAwesomeIcon.ArrowRight;

        // Two clicks - it undoes a lot at once, so a stray click shouldn't.
        var confirming = DateTime.UtcNow < revertAllConfirmUntil;
        var revertLabel = confirming ? "Click again to revert" : "Revert all";
        var revertWidth = ImGui.CalcTextSize(revertLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        var revertSpec = new SendSpec("revertAll", "Revert all")
        {
            Command = "revert all",
            Help = "Reverts everything on your active Sub back to nothing: restraints, outfit (back to their normal look), title, leash, animation, toy, and moodles.\nThe collar and the pairing are never touched. Each part only applies if your Sub allows that category.",
        };
        var gate = SendGate.Evaluate(plugin, revertSpec);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var chatWidth = showChat ? size + spacing : 0f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - size - spacing - revertWidth - chatWidth);
        if (showChat)
        {
            DrawChatIcon(size);
            ImGui.SameLine();
        }
        using (ImRaii.Disabled(!gate.Enabled))
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Destructive, confirming))
        {
            if (ImGui.Button($"{revertLabel}##revertAll", new Vector2(revertWidth, size)) && gate.Enabled)
            {
                if (confirming)
                {
                    SendGate.Run(plugin, revertSpec);
                    revertAllConfirmUntil = DateTime.MinValue;
                }
                else
                {
                    revertAllConfirmUntil = DateTime.UtcNow.AddSeconds(5);
                }
            }
        }
        TutorialService.Anchor(TutorialAnchors.MainRevertAll);
        SendGate.Tooltip(gate);
        ImGui.SameLine();

        if (IconGlyph.Button(icon, new Vector2(size, size)))
            plugin.SubControlWindow.IsOpen = !isOpen;
        TutorialService.Anchor(TutorialAnchors.MainSubControl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isOpen ? "Close Sub Control" : "Open Sub Control - every configured command in one place");
    }

    /// Opens chat on a conversation with unread messages, else the active pairing's; a badge counts what's unread.
    private void DrawChatIcon(float size)
    {
        var chat = plugin.Chat;
        var unread = chat.TotalUnread;
        var clicked = IconGlyph.CenteredButton("openChat", FontAwesomeIcon.Comments, size, unread > 0 ? Theme.AccentHover : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        TutorialService.Anchor(TutorialAnchors.MainChat);
        if (clicked)
        {
            var target = chat.Conversations().FirstOrDefault(c => c.Conversation.Unread > 0).Conversation?.PairingId
                ?? plugin.Configuration.ActivePairing?.Id
                ?? plugin.Configuration.Pairings.FirstOrDefault(p => p.IsPaired)?.Id;
            if (target is { } id) chat.RequestOpen(id);
            else plugin.ChatWindow.IsOpen = !plugin.ChatWindow.IsOpen;
        }
        if (unread > 0)
            Badge.OnLastItem(unread > 99 ? "99+" : unread.ToString());
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(unread > 0
                ? $"Chat - {unread} unread message{(unread == 1 ? "" : "s")}"
                : "Chat with your partner - works across worlds and data centers, and messages wait for them if they're offline.");
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
        IconGlyph.WrappedColored(stuck ? Theme.Caution : Theme.TextMuted, TeleportStageLabel(teleport));

        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Destructive))
            if (ImGui.Button("Stop teleport##teleportStop"))
                teleport.StopBySub("stopped from the header");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stops navigation right away and gives you back control of your movement.");
    }

    /// Shown while a cage is armed or closed, whatever the active pairing. No way out but panic, by design.
    private void DrawCageRow()
    {
        if (plugin.CageService.Cage is not { } cage)
            return;

        ImGui.Spacing();
        ImGui.Separator();
        var caged = cage.Phase == CagePhase.Caged;
        IconGlyph.Text(FontAwesomeIcon.Lock, caged ? $"Caged: {cage.Name}" : $"Cage waiting: {cage.Name}");
        string detail;
        if (caged)
            detail = cage.EndsAtUtc is { } end ? $"{cage.OwnerName} holds you here - {TimeText.Left(end)}." : $"{cage.OwnerName} holds you here until they open it.";
        else if (cage.HasClosed)
            detail = $"You left it. It closes again when you're back at {CagePlaces.Describe(cage.Place)}{(cage.EndsAtUtc is { } left ? $" ({TimeText.Left(left)})" : "")}.";
        else
            detail = $"{cage.OwnerName} set it at {CagePlaces.Describe(cage.Place)}. It closes when you're there, and waits {TimeText.Duration(cage.ArmExpiresAtUtc - DateTime.UtcNow)} more.";
        if (caged && plugin.CageService.IsWalkingIn)
            detail += " Walking you in...";
        IconGlyph.WrappedColored(caged ? Theme.Caution : Theme.TextMuted, detail);
        if (caged)
            IconGlyph.WrappedDisabled("Your permissions are locked until it opens. Panic still opens it.");
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
        if (SendReadiness.For(plugin).BlockedReason is { } reason)
            Status.Line(StatusKind.Caution, reason);
        SendButton.Draw(plugin, new SendSpec("headerTeleport", "Teleport Sub to me")
        {
            Help = "Teleport your paired Sub to your current position.",
            Custom = SendNotice.Teleport(plugin),
        });
        ImGui.SameLine();
        DrawFavoriteFixedActionToggle(FixedActionIds.Teleport);
    }

    /// Duplicated from ModuleWindow for the header's Teleport action.
    private void DrawFavoriteFixedActionToggle(string favoriteId)
    {
        var favorites = plugin.Configuration.QuickCommands.FavoriteFixedActions;
        var isFavorite = favorites.Contains(favoriteId);
        if (IconGlyph.Star($"##fav_{favoriteId}", isFavorite, isFavorite ? "Remove from favorites" : "Add to favorites"))
        {
            if (isFavorite) favorites.Remove(favoriteId);
            else favorites.Add(favoriteId);
            plugin.Configuration.Save();
        }
    }
}
