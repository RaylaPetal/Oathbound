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

public enum SettingsTab { Identity, Permissions, Tos, Test }

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

    private SettingsTab? selectTab;

    public void ShowTab(SettingsTab tab)
    {
        selectTab = tab;
        IsOpen = true;
        BringToFront();
    }

    public void ShowPermissionsTab() => ShowTab(SettingsTab.Permissions);

    private ImGuiTabItemFlags TabFlags(SettingsTab? requested, SettingsTab tab) =>
        requested == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

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

            var width = ImGui.CalcTextSize(dependency.Name).X + Layout.Scaled(18f);
            if (used > 0f && used + spacing + width <= available)
                ImGui.SameLine();
            else
                used = 0f;
            used += (used > 0f ? spacing : 0f) + width;

            ImGui.BeginGroup();
            using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
            using (ImRaii.PushColor(ImGuiCol.Text, color))
                ImGui.TextUnformatted(FontAwesomeIcon.Circle.ToIconString());
            ImGui.SameLine(0f, Layout.Scaled(4f));
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

        var requested = selectTab;
        selectTab = null;

        if (ImGui.BeginTabItem("Identity & Pairing", TabFlags(requested, SettingsTab.Identity)))
        {
            DrawIdentityCard(config);
            using (Section.Begin("recoveryCard"))
                recoveryView.Draw();
            using (Section.Begin("tutorialCard"))
                DrawTutorialCard(config);
            ImGui.EndTabItem();
        }

        // Only a Sub accepts anything from a peer, so an Owner gets an explanation.
        if (ImGui.BeginTabItem("Permissions", TabFlags(requested, SettingsTab.Permissions)))
        {
            if (config.Role == PluginRole.Owner)
                IconGlyph.WrappedDisabled("Permissions only apply to Subs. Switch your role in Identity & Pairing to set them.");
            else
                using (Section.Begin("permissionsCard"))
                    plugin.ModuleWindow.DrawPermissionsCard();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("ToS", TabFlags(requested, SettingsTab.Tos)))
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

        if (ImGui.BeginTabItem("Test Commands", TabFlags(requested, SettingsTab.Test)))
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
        IconGlyph.WrappedDisabled("Type what an Owner would send after \"/tell you\" and run it on yourself. Nothing is sent.");

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
        ImGui.SetNextItemWidth(-1);
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
                ImGui.TableSetupColumn("Side", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Owned by").X);
                ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableSetupColumn("Trigger phrase", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("##unpair", ImGuiTableColumnFlags.WidthFixed, Layout.ButtonWidth("Unpair"));
                ImGui.TableHeadersRow();

                foreach (var p in activePairings)
                {
                    ImGui.PushID(p.Id.ToString());
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    var owns = p.Direction == PairingDirection.OwnerSide;
                    ImGui.TextColored(owns ? Theme.Accent : Theme.Success, owns ? "Owns" : "Owned by");

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped($"{p.PeerName}@{p.PeerWorld}");
                    // Wrapping text after SameLine in a narrow cell breaks it one character per line.
                    if (config.ActivePairingId == p.Id)
                    {
                        Layout.ContinueRowOrWrap(ImGui.CalcTextSize("(active)").X);
                        ImGui.TextDisabled("(active)");
                    }

                    ImGui.TableNextColumn();
                    var (phrase, source) = TriggerPhraseInEffect(config, p);
                    ImGui.TextWrapped(phrase);
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
            "delivered" => "Your last unpair reached them.",
            "pending" => "Unpaired here; still letting them know.",
            "expired" => "Unpaired here; they weren't notified in time.",
            _ => "Unpaired here; they couldn't be notified.",
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
            IconGlyph.WrappedColored(Theme.Warning, $"Invitation from {request.Name}@{request.World} - they'll be {roleLabel}. Expires in {expiresIn.Minutes}m {expiresIn.Seconds}s.");
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
            IconGlyph.WrappedColored(Theme.Warning, "Couldn't reach Oathbound's server last time. Pairing needs it, but commands and panic still work.");
    }

    private void DrawTellPairing(PluginConfig config)
    {
        var pairingService = plugin.PairingService;
        ImGui.TextWrapped("Enter who to pair with as you'd address a tell. You both need to be online.");
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
        IconGlyph.HelpMarker("Sends a pairing invitation by tell. It expires in 15 minutes.");
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
            Layout.ItemWidth(160);
            if (ImGui.Combo("Role", ref roleIndex, RoleNames, RoleNames.Length))
            {
                config.Role = roleIndex switch { 1 => PluginRole.Owner, 2 => PluginRole.Switch, _ => PluginRole.Sub };
                config.Save();
                plugin.Tutorial.StartOverviewIfUnseen(config.Role);
            }
        }
        IconGlyph.HelpMarker("Sub receives commands, Owner sends them, Switch can do both.");

        using (ImRaii.Disabled(subLocked))
        {
            Layout.ItemWidth(160);
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
        Layout.ItemWidth(80);
        if (ImGui.InputInt("Linkshell number", ref linkshellNumber))
        {
            config.LinkshellNumber = Math.Clamp(linkshellNumber, 1, 8);
            config.Save();
        }
        IconGlyph.HelpMarker("Which of your 8 linkshells outgoing commands use when the header's channel selector is set to Linkshell.");

        var cwlsNumber = config.CrossWorldLinkshellNumber;
        Layout.ItemWidth(80);
        if (ImGui.InputInt("Cross-world Linkshell number", ref cwlsNumber))
        {
            config.CrossWorldLinkshellNumber = Math.Clamp(cwlsNumber, 1, 8);
            config.Save();
        }
        IconGlyph.HelpMarker("Which cross-world linkshell commands use when that channel is picked.");
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
        IconGlyph.HelpMarker("Identifies this install, never your character.");

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
            IconGlyph.WrappedColored(Theme.Danger, "This ends every pairing on this device and can't be undone. Are you sure?");
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

    private void DrawTutorialCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.GraduationCap, "Guided tutorial");
        ImGui.Separator();
        ImGui.TextWrapped("Replays the overview tour. Each module also has its own tour behind the ? in its title bar.");

        if (ImGui.Button("Rerun Tutorial"))
            plugin.Tutorial.StartOverview(config.ResolveActiveDirection());
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
        IconGlyph.Text(FontAwesomeIcon.ExclamationTriangle, "Automation");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Animations, the leash, restraints and teleport move or hold your character for you.");

        if (ImGuiCheckbox("I understand my character can be moved and held", config.TosAcknowledged, out var newTos))
        {
            config.TosAcknowledged = newTos;
            config.Save();
        }
        IconGlyph.HelpMarker("Needed before the Animation, Follow / Leash, Restraints and Teleport permissions can be turned on. Automating your character is against the game's rules - see the README.");
    }

    /// Its own card: arbitrary chat on any channel is a broader surface than the general acknowledgement covers.
    private void DrawCustomChatCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Comments, "Custom chat messages");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("A Custom Trigger can make you say any text, in any channel, where other players see it.");

        if (ImGuiCheckbox("I understand my Owner can make me send chat", config.CustomChatAcknowledged, out var newAck))
        {
            config.CustomChatAcknowledged = newAck;
            config.Save();
        }
        IconGlyph.HelpMarker("Needed before the Custom chat messages permission can be turned on.");
    }

    /// Its own card: this one actuates a physical device.
    private void DrawToyControlCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.BoltLightning, "Toy control");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Your Owner can run a real device connected through Intiface. Commands have a time limit, and your safeword stops every device.");

        if (ImGuiCheckbox("I understand my Owner can control my device, and I've checked Intiface's own safety settings", config.ToyControlAcknowledged, out var newToyAck))
        {
            config.ToyControlAcknowledged = newToyAck;
            config.Save();
        }
        IconGlyph.HelpMarker("Needed before the Toy control permission can be turned on.");
    }

    /// Its own card: the Sub's device firing automatically off local game state is a different risk from Owner commands.
    private void DrawToyTriggersCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Bolt, "Automatic toy triggers");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Your device can react by itself to your game - health, hits, emotes, spells - including during combat. Your safeword pauses all triggers.");

        if (ImGuiCheckbox("I understand my device can react on its own", config.ToyTriggersAcknowledged, out var newTriggerAck))
        {
            config.ToyTriggersAcknowledged = newTriggerAck;
            config.Save();
        }
        IconGlyph.HelpMarker("Needed before any trigger rule in Toy Control can be turned on.");
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
