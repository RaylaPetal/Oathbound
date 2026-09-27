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

/// One window for both roles (collar/ui-organization's shared, role-aware category-tab model) - Role
/// (Settings) decides which view each shared tab (Title/Outfit/Animation/Moodles/Restraints/Custom
/// Triggers/Collar/Follow) renders: Sub-side alias-authoring, or Owner-side browse/send. Permissions
/// (Sub-only) and Sync (catalog relay sync/import/reset/export, role-aware content of its own) round out
/// the nav bar; there is no separate Owner-only destination anymore. Pairing status stays permanently
/// above the nav bar. There is deliberately no panic button here - panic is the /oathboundpanic safeword
/// (Settings), typed rather than clicked, so it can't be hit by accident or spotted by someone watching
/// over a shoulder.
///
/// collar/ui-organization "Module content opens in its own window, not inline" (redesign-nav-and-modules):
/// this window now holds only the character/pairing header and the nav grid - every module's content lives
/// in `ModuleWindow` instead, opened by nav clicks and by `SetActiveModuleForTutorial`.
public class CollarWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ModuleWindow moduleWindow;

    /// collar/ui-organization "A movable on-screen button opens the quick-access favorites menu": the
    /// menu's "Open main window" control - opens the window wherever it last was. There is no separate
    /// "open to Owner tab" control anymore: every shared category tab already renders its Owner-role view
    /// whenever Role is Owner, so this alone suffices to reach Owner content directly.
    public void OpenMainWindow() => IsOpen = true;

    /// collar/onboarding: the one point of entry `TutorialDriver` uses to switch tabs from outside this
    /// window - kept separate so the driver never needs its own copy of tab-switching logic. Delegates to
    /// `ModuleWindow.Show` (redesign-nav-and-modules) instead of setting a local field, since module content
    /// no longer renders in this window.
    public void SetActiveModuleForTutorial(string moduleId)
    {
        moduleWindow.Show(moduleId);
        // collar/ui-organization "Permissions live in a Settings tab": the tutorial's Permissions step opens
        // Settings on that tab; the module window keeps showing the step's guidance and a pointer to it.
        if (moduleId == "permissions")
            plugin.SettingsWindow.ShowPermissionsTab();
    }

    private string? teleportResolveError;

    /// Header "Revert all": armed (first click) until this time; a second click before it sends.
    private DateTime revertAllConfirmUntil = DateTime.MinValue;
    private bool revealSafeword;

    /// collar/chat-transport "Trigger-phrase command delivery over a selectable channel" - order matches
    /// the ChatChannel enum exactly, since the header combo indexes into this by (int)config.OutgoingChannel.
    private static readonly string[] ChatChannelNames = ["Tell", "Party", "Alliance", "Linkshell", "Cross-world Linkshell"];

    /// collar/ui-organization "Category tabs present role-aware content": one nav entry per shared category
    /// (each shows the Sub alias-authoring view or the Owner browse/send view depending on Role), plus
    /// Reactions (both roles, local-only), Sync (catalog relay sync/import/reset, no Sub-side counterpart), and Favorites
    /// (opens the same QuickAccessMenu popup the on-screen HUD button and DTR bar entry already do - see
    /// redesign-nav-and-modules's proposal.md - not a module, handled entirely in the nav-click routing
    /// below).
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

    /// The window's total *content-region* height actually used last frame - measured once, at the very end
    /// of `Draw()`, as `ImGui.GetCursorPosY()` (which already starts at the top WindowPadding and accumulates
    /// every widget drawn since, including the nav grid's own card). Replaces two earlier, less accurate
    /// approaches: a single hand-tuned constant (drifted out of sync every time the header's own content
    /// changed by Role/pairing state), and then a header-only measurement combined with a *separately
    /// recomputed* `NavBar.RequiredHeight` call (still under-counted, because neither of those two pieces -
    /// nor the fudge factor between them - ever accounted for the window's own title bar or its bottom
    /// WindowPadding, both of which `SizeConstraints`/`SetNextWindowSize` need included since they size the
    /// *whole* window, not just its content region). Measuring the true end-of-content cursor position once
    /// and adding only the two genuinely-missing pieces (title bar, bottom padding) in `PreDraw` below is
    /// both simpler and exact. Defaults to a reasonable guess for the very first frame, before `Draw()` has
    /// ever measured it.
    private float lastContentHeight = 400f;

    private const float MinWidth = 465f;

    /// Recomputed every frame from last frame's real measurement (see `lastContentHeight`) plus the title
    /// bar height and bottom window padding - the two pieces outside the content region itself, and so never
    /// captured by measuring cursor position, but still part of the *outer* window size `SizeConstraints`/
    /// `SetNextWindowSize` expect.
    ///
    /// Height is pinned to exactly the content's height, in both directions - this window only holds the
    /// header and nav grid, so a user-chosen height has nothing to offer. An earlier grow-only version (force
    /// the size up whenever it fell under the minimum, never back down) turned one bad measurement into a
    /// permanently huge window: on the first frame of a session the header's wrapped text can measure one
    /// word per line before the window/table width is settled, and that inflated height then stuck. Now a
    /// bad frame is corrected on the next one. `SizeConstraints` alone doesn't retroactively resize a
    /// window whose size was persisted in imgui.ini, so the size is set explicitly whenever it's off;
    /// width stays freely user-resizable (>= MinWidth), since this only fires when the height is wrong.
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

    /// collar/ui-organization "Sub Control window stays docked to the main window": this frame's actual
    /// on-screen position/size, read right after Dear ImGui's own `Begin()` (called by Dalamud's
    /// `DrawInternal` before `Draw()` runs) - `SubControlWindow.PreDraw` reads these every frame to dock
    /// itself to this window's right edge, the same cross-file "expose live layout data instead of guessing
    /// a constant" pattern `NavBar.RequiredHeight` already established for this window's own sizing.
    public Vector2 LastPosition { get; private set; }
    public Vector2 LastSize { get; private set; }

    public override void Draw()
    {
        LastPosition = ImGui.GetWindowPos();
        LastSize = ImGui.GetWindowSize();

        DrawCharacterHeader();
        ImGui.Spacing();

        // collar/ui-organization: every destination is shown for both roles - Permissions (the one Sub-only
        // screen) moved to a Settings tab, and each module renders its own role-aware view.
        if (NavBar.Draw(NavItems) is { } clicked)
        {
            // collar/ui-organization "Favorites destination reuses the existing favorites menu" (reworked):
            // opens the dedicated FavoritesWindow rather than QuickAccessMenu's popup - that popup's "Open
            // main window"/"Open settings" fallback links only make sense from its original callers (the
            // floating on-screen button and the DTR bar entry), not from a nav entry already inside the
            // main window; see FavoritesWindow's own doc comment.
            if (clicked == "favorites")
                plugin.FavoritesWindow.IsOpen = true;
            else
                moduleWindow.Show(clicked);
        }

        // Not on the frame the window appears: its width (and the header table's column width) isn't
        // settled yet, so wrapped text can measure far taller than it really is - see PreDraw.
        if (!ImGui.IsWindowAppearing())
            lastContentHeight = ImGui.GetCursorPosY();
    }

    /// Both roles can receive a Pending handshake now (collarpair's role token - see
    /// ChatCommandListener), so this is one role-aware card instead of two windows each handling their own
    /// half. Panic no longer ends any pairing - it only reverts local restriction state - so unpairing
    /// either direction is a deliberate action from Settings' Unpair section (DrawUnpairSection) now,
    /// available for Owner-side and Sub-side pairings alike.
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
            IconGlyph.HelpMarker("Trusts this sender as your paired peer from now on. Either direction can be ended any time from Settings' Unpair section - panic no longer does this, it only reverts your current outfit/title/collar/movement-lock state.");
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

    /// collar/pairing "Always-visible local character and relationship header" (multi-pairing): a dropdown
    /// selects which pairing is active whenever there's more than one; unpairing itself is a deliberate
    /// Settings action now (see DrawUnpairSection), not something this header does. Panic no longer ends
    /// any pairing, so a "your peer ended this" notice can now only ever come from their own deliberate
    /// unpair - shown here per pairing, regardless of whether that pairing is still active (ending a
    /// pairing over a verified peer notice clears `Paired` immediately, before this can ever render it
    /// alongside "still active").
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

    /// collar/ui-organization "Header includes a Sub Control toggle": right-aligned on its own row directly
    /// below the pairing status line. Reads `plugin.SubControlWindow` lazily through `plugin` rather than a
    /// constructor-injected reference, since `SubControlWindow` is constructed after this window (it needs
    /// this window's `LastPosition`/`LastSize` to dock against) - the same lazy-through-`plugin` access this
    /// window's Favorites nav entry already uses for `plugin.FavoritesWindow`. Owner-side only (revised from
    /// this change's own spec, which originally had it visible for both roles) - ordering a Sub to do
    /// something is inherently an Owner action, so a Sub never has a reason to open this window; hidden
    /// entirely rather than shown-but-empty, matching the same `ResolveActiveDirection() ==
    /// PairingDirection.OwnerSide` gate the outgoing-channel/Teleport row just below already uses.
    private void DrawSubControlToggle()
    {
        if (plugin.Configuration.ResolveActiveDirection() != PairingDirection.OwnerSide)
            return;

        const float size = 24f;
        var isOpen = plugin.SubControlWindow.IsOpen;
        var icon = isOpen ? FontAwesomeIcon.ArrowLeft : FontAwesomeIcon.ArrowRight;

        // "Revert all", sharing the arrow's row just to its left. Two clicks: the first arms it for a few
        // seconds, the second sends - it undoes a lot at once, so a stray click shouldn't.
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

    /// collar/ui-organization "Header includes a quick Teleport action": relocated here from the Follow /
    /// Leash tab (design.md "Teleport moves, doesn't duplicate") since it's commonly used enough to want in
    /// the always-visible header rather than several clicks deep. Resolve/compose/send now lives in the
    /// shared `TeleportSendAction` helper so the quick-access menu's favorited entry (once favorited) can
    /// call the exact same logic instead of duplicating it.
    private void DrawTeleportHeaderAction()
    {
        IconGlyph.Text(FontAwesomeIcon.MapMarkerAlt, "Teleport");
        if (!plugin.LifestreamIpc.IsAvailable)
        {
            IconGlyph.WrappedDisabled("Requires the Lifestream plugin, installed and running on your own client.");
            return;
        }

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

    /// collar/ui-organization "Category tabs present role-aware content": the "no /tell target yet"
    /// warning shown before every Owner-role Send action - the header's own Teleport action needs this
    /// exact check too, so it's duplicated here rather than reaching into ModuleWindow for a header-only
    /// concern (ModuleWindow has its own copy for every module's Owner view). Returns whether Send should
    /// be enabled.
    private bool DrawOwnerCanSendBanner()
    {
        var canSend = plugin.Configuration.ActivePairing is { Direction: PairingDirection.OwnerSide };
        if (!canSend)
            IconGlyph.WrappedColored(Theme.Warning, "No /tell target yet - Send is disabled until an Owner-side pairing is active (select one in the header, or pair from Settings' handshake if you have none). Copy still works any time.");
        return canSend;
    }

    /// Same shape as ModuleWindow's copy (favoriting a built-in fixed action, e.g. Teleport, by its stable
    /// id rather than a backing QuickCommand) - duplicated here for the header's Teleport action for the
    /// same reason as DrawOwnerCanSendBanner above.
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
