using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Relay;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

/// Role, pairing, trigger phrase, permissions and acknowledgements. Visible regardless of Role.
public class SettingsWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly CodePairingView codePairingView;
    private readonly RecoveryView recoveryView;
    private string inviteTargetInput = "";
    private bool sendingInvitation;
    private bool acceptingInvitation;

    /// Only meaningful for Switch: true invites as the Owner-side.
    private bool inviteAsOwnerSide = true;
    private bool confirmingIdentityReset;
    private Guid? confirmingUnpairId;
    private bool confirmingInviteReplace;
    private string triggerPhraseInput = "";
    private string testCommandInput = "";
    private int testCustomTriggerIndex;

    /// Session-only test feedback; each entry auto-clears shortly after being shown.
    private readonly Dictionary<string, (LocalTestResult Result, long ShownAtTicks)> testResults = new();

    private const long TestResultDisplayMs = 4_000;

    private static readonly string[] RoleNames = ["Sub", "Owner", "Switch"];

    public SettingsWindow(Plugin plugin) : base("Oathbound - Settings###CollarSettingsWindow")
    {
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(460, 700), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        this.plugin = plugin;
        codePairingView = new CodePairingView(plugin);
        recoveryView = new RecoveryView(plugin);
    }

    public void Dispose() { }

    private bool selectPermissionsTab;

    public void ShowPermissionsTab()
    {
        selectPermissionsTab = true;
        IsOpen = true;
        BringToFront();
    }

    public override void OnOpen()
    {
        var config = plugin.Configuration;
        triggerPhraseInput = config.TriggerPhrase;
    }

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    /// One chip per dependency, with its exact state in the tooltip.
    private void DrawDependencyHeader()
    {
        var status = plugin.DependencyStatus;
        IconGlyph.Text(FontAwesomeIcon.PuzzlePiece, "Dependencies");
        IconGlyph.HelpMarker("Other plugins and apps Oathbound works with. Hover one for details. Owners don't need any of them to send commands.");

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var available = ImGui.GetContentRegionAvail().X;
        var used = 0f;
        foreach (var dependency in DependencyStatusService.All)
        {
            var state = status.Get(dependency.Id);
            var color = state == DependencyState.Ready ? Theme.Success : Theme.StatusMissing;

            var width = ImGui.CalcTextSize(dependency.Name).X + 18f;
            if (used > 0f && used + spacing + width <= available)
                ImGui.SameLine();
            else
                used = 0f;
            used += (used > 0f ? spacing : 0f) + width;

            ImGui.BeginGroup();
            using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
            using (ImRaii.PushColor(ImGuiCol.Text, color))
                ImGui.TextUnformatted(FontAwesomeIcon.Circle.ToIconString());
            ImGui.SameLine(0f, 4f);
            ImGui.TextUnformatted(dependency.Name);
            ImGui.EndGroup();

            if (ImGui.IsItemHovered())
            {
                var described = DependencyStatusService.Describe(state);
                var stateText = char.ToUpperInvariant(described[0]) + described[1..];
                ImGui.SetTooltip($"{dependency.Name}: {stateText}\nUsed for: {dependency.Purpose}\nWho needs it: {dependency.WhoNeedsIt}");
            }
        }
    }

    public override void Draw()
    {
        var config = plugin.Configuration;

        using (Section.Begin("dependenciesCard"))
            DrawDependencyHeader();

        if (!ImGui.BeginTabBar("settingsTabs"))
            return;

        if (ImGui.BeginTabItem("Identity & Pairing"))
        {
            DrawIdentityCard(config);
            using (Section.Begin("recoveryCard"))
                recoveryView.Draw();
            using (Section.Begin("worldVisualsCard"))
                DrawWorldVisualsCard(config);
            using (Section.Begin("tutorialCard"))
                DrawTutorialCard(config);
            ImGui.EndTabItem();
        }

        // Only a Sub accepts anything from a peer, so an Owner gets an explanation.
        var selectPermissions = selectPermissionsTab;
        selectPermissionsTab = false;
        if (ImGui.BeginTabItem("Permissions", selectPermissions ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
        {
            if (config.Role == PluginRole.Owner)
                IconGlyph.WrappedDisabled("Permissions only apply while you're set to Sub - they're what a Sub accepts from a paired Owner. Switch your role in Identity & Pairing to configure them.");
            else
                using (Section.Begin("permissionsCard"))
                    plugin.ModuleWindow.DrawPermissionsCard();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("ToS"))
        {
            using (Section.Begin("tosCard"))
                DrawTosCard(config);
            using (Section.Begin("customChatCard"))
                DrawCustomChatCard(config);
            using (Section.Begin("toyControlCard"))
                DrawToyControlCard(config);
            using (Section.Begin("toyTriggersCard"))
                DrawToyTriggersCard(config);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Test"))
        {
            using (Section.Begin("testCommandCard"))
                DrawTestCommandCard(config);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    /// Runs raw Owner-style text through the real dispatch path (trigger phrase, permissions, parsing). Nothing is sent.
    private void DrawTestCommandCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.FlaskVial, "Test an Owner command");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Type the exact text an Owner would send after \"/tell you\" - trigger phrase included - and run it locally. No pairing or peer needed, and nothing is sent or received.");

        var aliases = config.Aliases;
        var savedTriggers = new List<(string Label, string Command)>
        {
            ("Title · Clear", ControlWords.ClearTitle),
            ("Outfit · Unlock", ControlWords.Unlock),
            ("Follow · Engage", ControlWords.Leash),
            ("Follow · Release", ControlWords.Unleash),
            ("Moodle · Clear", ControlWords.ClearMoodle),
            ("Collar · Lock", "collar lock"),
            ("Collar · Unlock", "collar unlock"),
            ("Restraints · Unlock all", "restraint unlock"),
            ("Custom Triggers · Revert", "customtrigger revert"),
            ("Everything · Revert all (keeps collar)", "revert all"),
        };
        savedTriggers.AddRange(aliases.Titles.Select(a => ($"Title · {a.Alias}", a.Alias)));
        savedTriggers.AddRange(aliases.Outfits.Select(a => ($"Outfit · {a.Alias}", a.Alias)));
        savedTriggers.AddRange(aliases.Gestures.Select(a => ($"Animation · {a.Alias}", a.Alias)));
        savedTriggers.AddRange(aliases.Moodles.Select(a => ($"Moodle · {a.Alias}", a.Alias)));
        savedTriggers.AddRange(config.RestraintMapping.Devices.Values.Where(d => d.Name.Trim().Length > 0).Select(d => ($"Restraint · {d.Name} (toggle)", d.Name.Trim())));
        savedTriggers.AddRange(config.RestraintMapping.ConfiguredMods.Where(m => m.Alias.Trim().Length > 0).Select(m => ($"Restraint · {m.Alias} (toggle)", m.Alias.Trim())));
        savedTriggers.AddRange(aliases.CustomTriggers.Select(a => ($"Custom Trigger · {a.Alias}", a.Alias)));
        savedTriggers.AddRange(config.RestraintMapping.ConfiguredMods
            .Where(x => x.ItemId > 0 && GlamourerIpc.GetItemSlot((uint)x.ItemId.Value) is not null && x.Rules.Count > 0 &&
                        config.RestraintMapping.LocalCatalog.ContainsKey(x.CatalogId))
            .Select(x => ($"Restraint · {x.Name}", RestraintCommand.BuildCatalogLockCommand(
                x.CatalogId, x.Name, x.ItemId!.Value, x.Rules))));

        ImGui.TextUnformatted("Choose one of your triggers");
        testCustomTriggerIndex = Math.Clamp(testCustomTriggerIndex, 0, savedTriggers.Count - 1);
        var triggerNames = savedTriggers.Select(t => t.Label).ToArray();
        ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X));
        ImGui.Combo("##testSavedTrigger", ref testCustomTriggerIndex, triggerNames, triggerNames.Length);
        var selectedCommand = $"{config.TriggerPhrase.Trim()} {savedTriggers[testCustomTriggerIndex].Command}".Trim();
        if (ImGui.SmallButton("Put in test box"))
            testCommandInput = selectedCommand;
        ImGui.SameLine();
        DrawTestButton("testSelectedTrigger", "Run selected trigger", () => plugin.ChatCommandListener.TestIncomingCommand(selectedCommand));
        IconGlyph.WrappedDisabled($"What the Owner sends: {selectedCommand}");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##testCommandInput", "e.g. ray outfit lock kagome", ref testCommandInput, 500);
        if (ImGui.SmallButton("Paste"))
        {
            var clipboard = ImGui.GetClipboardText() ?? "";
            testCommandInput = clipboard[..Math.Min(clipboard.Length, 500)];
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(testCommandInput.Length == 0))
        {
            if (ImGui.SmallButton("Copy"))
                ImGui.SetClipboardText(testCommandInput);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
                testCommandInput = "";
        }
        DrawTestButton("testOwnerCommand", "Run test", () => plugin.ChatCommandListener.TestIncomingCommand(testCommandInput));
    }

    /// Role and trigger phrase are read-only while this device holds any Sub-side pairing; enforced in the UI only.
    private void DrawIdentityCard(PluginConfig config)
    {
        var subLocked = config.HasActiveSubSidePairing;
        var activePairings = config.Pairings.Where(p => p.IsPaired).ToList();

        IconGlyph.Text(FontAwesomeIcon.UserShield, "Identity & Pairing");
        ImGui.Separator();

        using (Section.Begin("yourPairings", activePairings.Count > 0 ? $"Your pairings ({activePairings.Count})" : "Your pairings"))
            DrawPairingsList(config, activePairings);

        using (Section.Begin("pairWith", "Pair with someone"))
            DrawPairWithSection(config);

        using (Section.Begin("roleTrigger", "Role & trigger phrase"))
            DrawRoleSection(config, subLocked);

        using (Section.Begin("deviceIdentity"))
            DrawDeviceIdentitySection();
    }

    /// Unpair asks for confirmation; see PanicHandler.ReleasePairing for what it does.
    private void DrawPairingsList(PluginConfig config, List<PairingState> activePairings)
    {
        if (activePairings.Count == 0)
        {
            IconGlyph.WrappedColored(Theme.TextMuted, "Not paired yet - create or enter a pairing code below.");
            DrawRevocationDeliveryStatus(config);
            return;
        }

        const int visibleRows = 6;
        var rowHeight = ImGui.GetFrameHeightWithSpacing();
        var listHeight = rowHeight * (Math.Min(activePairings.Count, visibleRows) + 1) + ImGui.GetStyle().WindowPadding.Y * 2;
        // Section.List is the scrolling child, so the table itself doesn't scroll.
        using (Section.List("pairingsList", listHeight))
        {
            if (ImGui.BeginTable("pairingsTable", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Side", ImGuiTableColumnFlags.WidthFixed, 80f);
                ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableSetupColumn("Trigger phrase", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("##unpair", ImGuiTableColumnFlags.WidthFixed, 64f);
                ImGui.TableHeadersRow();

                foreach (var p in activePairings)
                {
                    ImGui.PushID(p.Id.ToString());
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    var owns = p.Direction == PairingDirection.OwnerSide;
                    IconGlyph.WrappedColored(owns ? Theme.Accent : Theme.Success, owns ? "Owns" : "Owned by");

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{p.PeerName}@{p.PeerWorld}");
                    if (config.ActivePairingId == p.Id)
                    {
                        ImGui.SameLine();
                        IconGlyph.WrappedDisabled("(active)");
                    }

                    ImGui.TableNextColumn();
                    var (phrase, source) = TriggerPhraseInEffect(config, p);
                    ImGui.TextUnformatted(phrase);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(source);

                    ImGui.TableNextColumn();
                    using (ImRaii.Disabled(confirmingUnpairId is not null))
                    {
                        if (ImGui.SmallButton("Unpair"))
                            confirmingUnpairId = p.Id;
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
        }

        if (confirmingUnpairId is { } unpairId)
        {
            if (activePairings.FirstOrDefault(p => p.Id == unpairId) is { } target)
            {
                IconGlyph.WrappedColored(Theme.Danger, $"End your pairing with {target.PeerName}@{target.PeerWorld}? This reverts your current outfit, title, movement lock and restraints the same way panic does, removes your collar if this is the pairing that collared you, and can't be undone. Your other pairings aren't affected.");
                if (ImGui.Button("Confirm unpair"))
                {
                    plugin.PanicHandler.ReleasePairing(target);
                    confirmingUnpairId = null;
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##unpairCancel"))
                    confirmingUnpairId = null;
            }
            else
            {
                confirmingUnpairId = null; // That pairing ended some other way while the prompt was open.
            }
        }

        DrawRevocationDeliveryStatus(config);
    }

    /// Sub-side: always our own. Owner-side: the peer's, falling back to ours when they never sent one.
    private static (string Phrase, string Source) TriggerPhraseInEffect(PluginConfig config, PairingState p)
    {
        if (p.Direction == PairingDirection.SubSide)
            return (config.TriggerPhrase, "Your own trigger phrase - it's what your Owner's commands must start with.");
        if (p.PeerTriggerPhrase is { Length: > 0 } peerPhrase)
            return (peerPhrase, "Your Sub's trigger phrase - your commands to them start with it.");
        return (config.TriggerPhrase, "Your own trigger phrase - your Sub hasn't sent theirs.");
    }

    private void DrawRevocationDeliveryStatus(PluginConfig config)
    {
        var mostRecentDelivery = config.Pairings
            .Where(p => p.LastRevocationDeliveryStatus is { Length: > 0 })
            .OrderByDescending(p => p.LastRevocationDeliveryUpdatedAt)
            .FirstOrDefault();
        if (mostRecentDelivery?.LastRevocationDeliveryStatus is not { Length: > 0 } delivery)
            return;
        var label = delivery switch
        {
            "delivered" => "Last unpair relay notice was delivered.",
            "pending" => "Local unpair completed; relay notification is pending retry.",
            "expired" => "Local unpair completed; its relay notification expired before delivery.",
            _ => "Local unpair completed; its relay notification failed.",
        };
        IconGlyph.WrappedColored(delivery == "delivered" ? Theme.Success : Theme.Warning, label);
    }

    /// An invitation from someone on an older version shows here even while "Pair by tell" is collapsed.
    private void DrawPairWithSection(PluginConfig config)
    {
        var pairingService = plugin.PairingService;
        codePairingView.Draw(config);

        if (pairingService.Pending is { } request)
        {
            ImGui.Spacing();
            var roleLabel = request.SenderRole == PluginRole.Owner ? "your Owner" : "your Sub";
            var expiresIn = TimeSpan.FromSeconds(Math.Max(0, request.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            var invitationExpired = request.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            IconGlyph.WrappedColored(Theme.Warning, $"Invitation by tell from {request.Name}@{request.World} (verified sender, signature checked) - they say they'll be {roleLabel}. Expires in {expiresIn.Minutes}m {expiresIn.Seconds}s.");
            if (request.SenderRole == config.Role)
                IconGlyph.WrappedColored(Theme.Danger, $"You're both set to {config.Role} - one of you should switch Role, or nothing will ever trigger.");
            using (ImRaii.Disabled(acceptingInvitation || invitationExpired))
            {
                if (ImGui.Button(acceptingInvitation ? "Accepting..." : "Accept##tellAccept"))
                {
                    acceptingInvitation = true;
                    Plugin.FireAndForget(AcceptInvitationAsync());
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Reject##tellReject"))
                pairingService.DismissPending();
            if (invitationExpired)
                IconGlyph.WrappedColored(Theme.Warning, "This invitation expired. Reject it and ask the sender to create another.");
        }
        else if (pairingService.AwaitingActivation)
        {
            IconGlyph.WrappedColored(Theme.Warning, "Waiting for the other side to confirm your tell pairing...");
        }

        if (pairingService.LastError is { Length: > 0 } lastError)
            IconGlyph.WrappedColored(Theme.Danger, lastError);

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Older version? Pair by tell###tellPairing"))
            DrawTellPairing(config);

        ImGui.Spacing();
        if (plugin.RelayClient.LastReachable is false)
            IconGlyph.WrappedColored(Theme.Warning, "The relay was unreachable on the last attempt - pairing needs it, but existing pairings, commands and panic keep working.");
        else
            IconGlyph.WrappedDisabled("Pairing goes through Oathbound's own secure relay.");
        IconGlyph.HelpMarker("Pairing, recovery backups and encrypted catalog sync use Oathbound's fixed Cloudflare relay; it never sees character names or command contents. The endpoint can't be changed by plugin configuration.");
    }

    private void DrawTellPairing(PluginConfig config)
    {
        var pairingService = plugin.PairingService;
        ImGui.TextWrapped("Enter who to pair with, exactly as you'd address a tell, then Send. Both of you need to be online and able to send tells.");
        if (config.Role == PluginRole.Switch)
        {
            if (ImGui.RadioButton("Invite as Owner (they'll be your Sub)", inviteAsOwnerSide)) inviteAsOwnerSide = true;
            ImGui.SameLine();
            if (ImGui.RadioButton("Invite as Sub (they'll be your Owner)", !inviteAsOwnerSide)) inviteAsOwnerSide = false;
        }
        using (ImRaii.Disabled(sendingInvitation))
        {
            ImGui.InputTextWithHint("Pair with", "Name Surname@World", ref inviteTargetInput, 64);
            using (ImRaii.Disabled(inviteTargetInput.Trim().Length == 0 || confirmingInviteReplace))
            {
                if (ImGui.Button(sendingInvitation ? "Sending..." : "Send Invitation"))
                {
                    if (pairingService.DescribeOutstandingInvitation() is not null)
                        confirmingInviteReplace = true;
                    else
                    {
                        sendingInvitation = true;
                        Plugin.FireAndForget(SendInvitationAsync(inviteTargetInput.Trim()));
                    }
                }
            }
        }
        IconGlyph.HelpMarker("Creates a single-use relay invitation (expires in 15 minutes) and sends its reference in one tell. They accept it, an acknowledgement tell comes back automatically, and you're both paired.");
        if (confirmingInviteReplace && pairingService.DescribeOutstandingInvitation() is { } outstandingInvite)
        {
            IconGlyph.WrappedColored(Theme.Danger, $"You already have an unconfirmed invitation outstanding to {outstandingInvite.Target}. Sending a new one abandons it - if they accept it later, nothing will happen on your side.");
            if (ImGui.Button("Send new invitation anyway"))
            {
                confirmingInviteReplace = false;
                sendingInvitation = true;
                Plugin.FireAndForget(SendInvitationAsync(inviteTargetInput.Trim()));
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##inviteReplaceCancel"))
                confirmingInviteReplace = false;
        }
        else if (confirmingInviteReplace)
        {
            // The outstanding invitation expired or completed while this prompt was open.
            confirmingInviteReplace = false;
        }
        if (pairingService.OutgoingInvitationExpiresAt is { } outgoingExpiry)
        {
            var remaining = TimeSpan.FromSeconds(Math.Max(0, outgoingExpiry - DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            IconGlyph.WrappedDisabled($"Invitation sent to {pairingService.OutgoingInvitationTarget}; expires in {remaining.Minutes}m {remaining.Seconds}s.");
        }
    }

    private void DrawRoleSection(PluginConfig config, bool subLocked)
    {
        using (ImRaii.Disabled(subLocked))
        {
            var roleIndex = config.Role switch { PluginRole.Owner => 1, PluginRole.Switch => 2, _ => 0 };
            ImGui.SetNextItemWidth(160f);
            if (ImGui.Combo("Role", ref roleIndex, RoleNames, RoleNames.Length))
            {
                config.Role = roleIndex switch { 1 => PluginRole.Owner, 2 => PluginRole.Switch, _ => PluginRole.Sub };
                config.Save();
                plugin.TutorialDriver.StartIfUnseenForRole(config.Role);
            }
        }
        IconGlyph.HelpMarker("Which side(s) of a pairing you can hold. A Sub reacts to command tells and applies them locally; an Owner sends them; a Switch can be both at once. Every category tab shows its Sub or Owner view based on the active pairing.");

        using (ImRaii.Disabled(subLocked))
        {
            ImGui.SetNextItemWidth(160f);
            if (ImGui.InputText("Trigger phrase", ref triggerPhraseInput, 32))
            {
                config.TriggerPhrase = triggerPhraseInput;
                config.Save();
            }
        }
        IconGlyph.HelpMarker("The word that must start every ongoing command tell, e.g. \"command strip\".");

        if (subLocked)
            IconGlyph.WrappedColored(Theme.TextMuted, "Role and trigger phrase are locked while you hold a Sub-side pairing - unpair it above to change them.");

        ImGui.Spacing();
        var linkshellNumber = config.LinkshellNumber;
        ImGui.SetNextItemWidth(80f);
        if (ImGui.InputInt("Linkshell number", ref linkshellNumber))
        {
            config.LinkshellNumber = Math.Clamp(linkshellNumber, 1, 8);
            config.Save();
        }
        IconGlyph.HelpMarker("Which of your 8 linkshells outgoing commands use when the header's channel selector is set to Linkshell.");

        var cwlsNumber = config.CrossWorldLinkshellNumber;
        ImGui.SetNextItemWidth(80f);
        if (ImGui.InputInt("Cross-world Linkshell number", ref cwlsNumber))
        {
            config.CrossWorldLinkshellNumber = Math.Clamp(cwlsNumber, 1, 8);
            config.Save();
        }
        IconGlyph.HelpMarker("Which of your 8 cross-world linkshells outgoing commands use when the header's channel selector is set to Cross-world Linkshell.");
    }

    private async System.Threading.Tasks.Task SendInvitationAsync(string target)
    {
        var direction = plugin.Configuration.Role switch
        {
            PluginRole.Owner => PairingDirection.OwnerSide,
            PluginRole.Sub => PairingDirection.SubSide,
            _ => inviteAsOwnerSide ? PairingDirection.OwnerSide : PairingDirection.SubSide,
        };
        try
        {
            await plugin.PairingService.CreateAndSendInvitationAsync(target, direction, CancellationToken.None);
        }
        finally
        {
            sendingInvitation = false;
        }
    }

    private async System.Threading.Tasks.Task AcceptInvitationAsync()
    {
        try
        {
            await plugin.PairingService.AcceptPendingAsync(CancellationToken.None);
        }
        finally
        {
            acceptingInvitation = false;
        }
    }

    /// Fingerprint only - never the key itself.
    private void DrawDeviceIdentitySection()
    {
        var identity = plugin.DeviceIdentityService;
        IconGlyph.Text(FontAwesomeIcon.Fingerprint, "Device Identity");
        var fingerprint = identity.DeviceKeyId is { Length: >= 16 } id ? id[..16] : identity.DeviceKeyId ?? "(none)";
        ImGui.TextUnformatted($"Fingerprint: {fingerprint}...");
        IconGlyph.HelpMarker("Identifies this installation to the relay - never your character. Regenerated only on an explicit reset below.");

        if (!OperatingSystem.IsWindows())
        {
            IconGlyph.WrappedColored(Theme.Warning, "Running under Wine: the private key is stored without a real OS-backed protection guarantee (Wine's DPAPI does not provide one). Treat a compromised machine as requiring a reset below.");
        }

        var cooldownRemaining = identity.CooldownRemaining;
        using (ImRaii.Disabled(confirmingIdentityReset || cooldownRemaining is not null))
        {
            if (ImGui.Button("Reset device identity"))
                confirmingIdentityReset = true;
        }
        if (cooldownRemaining is { } remaining)
            IconGlyph.WrappedColored(Theme.TextMuted, $"Available again in {(int)remaining.TotalMinutes}m {remaining.Seconds}s.");
        if (confirmingIdentityReset)
        {
            IconGlyph.WrappedColored(Theme.Danger, "This ends every relay-assisted pairing this device holds and cannot be undone. Are you sure?");
            using (ImRaii.Disabled(!identity.CanReset))
            {
                if (ImGui.Button("Confirm reset"))
                {
                    Plugin.FireAndForget(plugin.PairingService.ResetDeviceIdentityAsync(CancellationToken.None));
                    confirmingIdentityReset = false;
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                confirmingIdentityReset = false;
        }
    }

    /// Only changes what this client draws; the leash keeps working with its line hidden.
    private void DrawWorldVisualsCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Eye, "In-world visuals");
        ImGui.Separator();
        ImGui.TextWrapped("Only you and your paired Owner/Sub see these - nobody else, and nothing extra is sent.");

        var showIcons = config.ShowStatusIcons;
        if (ImGui.Checkbox("Show status icons on nameplates", ref showIcons))
        {
            config.ShowStatusIcons = showIcons;
            config.Save();
        }
        IconGlyph.HelpMarker("Gagged, restrained and leashed icons next to the name. On an Owner's screen these are an estimate from the commands you sent (the leash icon also clears when your Sub's leash comes off on their side).");

        var showLeash = config.ShowLeashLine;
        if (ImGui.Checkbox("Show leash line", ref showLeash))
        {
            config.ShowLeashLine = showLeash;
            config.Save();
        }
        IconGlyph.HelpMarker("A line from the Sub's neck to the Owner's hand while leashed. Hiding it doesn't release the leash.");
    }

    private void DrawTutorialCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.GraduationCap, "Guided tutorial");
        ImGui.Separator();
        ImGui.TextWrapped("Replays the guided tour of each tab for your active pairing's direction (or Role, with nothing active), even if you've already seen it.");

        if (ImGui.Button("Rerun Tutorial"))
            plugin.TutorialDriver.Start(config.ResolveActiveDirection());
        IconGlyph.HelpMarker("Doesn't affect the other direction's own first-time tutorial.");
    }

    private void DrawTestButton(string key, string label, Func<LocalTestResult> run)
    {
        if (ImGui.SmallButton($"{label}##{key}"))
            testResults[key] = (run(), Environment.TickCount64);

        if (testResults.TryGetValue(key, out var last))
        {
            if (Environment.TickCount64 - last.ShownAtTicks >= TestResultDisplayMs)
            {
                testResults.Remove(key);
            }
            else
            {
                ImGui.SameLine();
                IconGlyph.WrappedColored(last.Result.Success ? Theme.Success : Theme.Danger, last.Result.Message);
            }
        }
    }

    private void DrawTosCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.ExclamationTriangle, "Automation risk acknowledgement");
        ImGui.Separator();
        ImGui.TextWrapped("Required before the Animation/Follow permission toggles can be enabled - see the README.");

        if (ImGuiCheckbox("I understand the automation-risk caveat (input blocking; sending is always your own click)", config.TosAcknowledged, out var newTos))
        {
            config.TosAcknowledged = newTos;
            config.Save();
        }
        IconGlyph.HelpMarker("Required once before the Animation and Follow permission toggles (in the Sub window's Permissions tab) can be enabled at all - Title and Outfit don't need it.");
    }

    /// Its own card: arbitrary chat on any channel is a broader surface than the general acknowledgement covers.
    private void DrawCustomChatCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Comments, "Custom Trigger chat messages");
        ImGui.Separator();
        IconGlyph.WrappedColored(Theme.Danger, "A Custom Trigger's chat action can send ANY text to ANY channel (including public party/say/yell chat), as your own character, triggered remotely by your Owner - unlike Animation, which only ever fires a closed set of self-targeting pose/emote commands. Required before the \"Custom chat messages\" permission (Permissions tab) can be enabled at all.");

        if (ImGuiCheckbox("I understand a Custom Trigger's chat action can send arbitrary text to any channel, visible to other players, as my own character", config.CustomChatAcknowledged, out var newAck))
        {
            config.CustomChatAcknowledged = newAck;
            config.Save();
        }
    }

    /// Its own card: this one actuates a physical device.
    private void DrawToyControlCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.BoltLightning, "Toy control");
        ImGui.Separator();
        IconGlyph.WrappedColored(Theme.Danger, "Toy Control lets your Owner directly actuate a physical device connected through Intiface Central - a real vibration on real hardware, not just an in-game change. This plugin enforces a maximum duration per command and always stops every device on panic, but Intiface Central itself and your device's own connection are outside this plugin's control. Required before the \"Toy control\" permission (Permissions tab) can be enabled at all.");

        if (ImGuiCheckbox("I understand my Owner can directly actuate a connected physical device through Intiface, and I have reviewed Intiface's own safety settings myself", config.ToyControlAcknowledged, out var newToyAck))
        {
            config.ToyControlAcknowledged = newToyAck;
            config.Save();
        }
    }

    /// Its own card: the Sub's device firing automatically off local game state is a different risk from Owner commands.
    private void DrawToyTriggersCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Bolt, "Automatic toy triggers");
        ImGui.Separator();
        IconGlyph.WrappedColored(Theme.Danger, "Automatic Triggers let your own client fire a toy action on its own - with no per-occurrence click from you or your Owner - in reaction to your own local game state (health dropping, being hit, a spell or emote used on you, a restriction becoming active). This can fire during combat or other content. Required before any trigger rule (Toy Control tab) can be enabled at all. Panic always suspends every trigger until you explicitly resume them.");

        if (ImGuiCheckbox("I understand my own device can automatically vibrate in reaction to my game state, with no click required each time, and this can happen during combat or other content", config.ToyTriggersAcknowledged, out var newTriggerAck))
        {
            config.ToyTriggersAcknowledged = newTriggerAck;
            config.Save();
        }
    }

    /// ImGui.Checkbox never wraps its label, so the label is drawn separately, wrapped.
    private static bool ImGuiCheckbox(string label, bool value, out bool newValue)
    {
        newValue = value;
        var changed = ImGui.Checkbox($"##{label}", ref newValue);
        ImGui.SameLine();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(label);
        ImGui.PopTextWrapPos();
        return changed;
    }
}
