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

public enum SettingsTab { Identity, Characters, Permissions, Tos }

/// Role, pairing, trigger phrase, permissions and acknowledgements. Visible regardless of Role.
public class SettingsWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly CodePairingView codePairingView;
    private readonly RecoveryView recoveryView;
    private bool confirmingIdentityReset;
    private Guid? confirmingUnpairId;
    private bool charactersTabWasOpen;
    private string triggerPhraseInput = "";

    /// Session-only test feedback; each entry auto-clears shortly after being shown.

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
            using (Section.Begin("achievementsCard"))
                DrawAchievementsCard(config);
            using (Section.Begin("chatCard"))
                DrawChatCard(config);
            ImGui.EndTabItem();
        }

        var charactersOpen = ImGui.BeginTabItem("Characters", TabFlags(requested, SettingsTab.Characters));
        // Opening the tab reads the partners' latest records from the relay instead of waiting for the next check.
        if (charactersOpen && !charactersTabWasOpen)
            plugin.OwnerCageStatus.RequestRefresh();
        charactersTabWasOpen = charactersOpen;
        if (charactersOpen)
        {
            IconGlyph.Text(FontAwesomeIcon.Users, "Characters");
            ImGui.Separator();
            using (Section.Begin("characters"))
                DrawCharactersSection(config, config.Pairings.Where(p => p.IsPaired).ToList());
            ImGui.EndTabItem();
        }

        // Only a Sub accepts anything from a peer, so an Owner gets an explanation.
        if (ImGui.BeginTabItem("Permissions", TabFlags(requested, SettingsTab.Permissions)))
        {
            if (config.Role == PluginRole.Owner)
                IconGlyph.WrappedDisabled("Permissions only apply to Subs. Switch your role in Identity & Pairing to set them.");
            else
            {
                using (Section.Begin("permissionsCard"))
                    plugin.ModuleWindow.DrawPermissionsCard();
                using (Section.Begin("redrawCard"))
                    DrawRedrawCard(config);
                using (Section.Begin("builtinMoodlesCard"))
                    DrawBuiltinMoodlesCard(config);
            }
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("ToS", TabFlags(requested, SettingsTab.Tos)))
        {
            if (plugin.CageService.IsCaged)
                IconGlyph.WrappedColored(Theme.Warning, "You're caged, so these stay as they are until the cage opens. Panic still opens it.");
            using (ImRaii.Disabled(plugin.CageService.IsCaged))
            {
                using (Section.Begin("tosCard"))
                    DrawTosCard(config);
                using (Section.Begin("customChatCard"))
                    DrawCustomChatCard(config);
                using (Section.Begin("toyControlCard"))
                    DrawToyControlCard(config);
                using (Section.Begin("toyTriggersCard"))
                    DrawToyTriggersCard(config);
                using (Section.Begin("rulebookCard"))
                    DrawRulebookCard(config);
                using (Section.Begin("cageCard"))
                    DrawCageCard(config);
            }
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

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
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 1f);
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
                    if (config.ActivePairing?.Id == p.Id)
                    {
                        Layout.ContinueRowOrWrap(ImGui.CalcTextSize("(active)").X);
                        ImGui.TextDisabled("(active)");
                    }

                    ImGui.TableNextColumn();
                    PeerRelayStatus.Draw(plugin, p);

                    ImGui.TableNextColumn();
                    using (ImRaii.Disabled(confirmingUnpairId is not null))
                    {
                        if (ImGui.Button("Unpair"))
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

    /// Which of this install's characters the pairings know about, and which of each peer's characters count as them.
    private void DrawCharactersSection(PluginConfig config, List<PairingState> activePairings)
    {
        var records = plugin.CharacterRecords;
        IconGlyph.WrappedDisabled("Every character you link counts as you for all your pairings. Restrictions follow you to every character either way.");

        ImGui.TextUnformatted("Your characters");
        if (records.Current is { } current && config.FindMyCharacter(current.Name, current.World) is null)
        {
            ImGui.BulletText($"{current} (this character, not linked)");
            ImGui.SameLine();
            if (ImGui.Button("Link##linkCurrent")) records.LinkCurrent();
        }
        foreach (var mine in config.MyCharacters.ToList())
        {
            ImGui.PushID($"mine_{mine}");
            var state = mine.State switch
            {
                MyCharacterState.Linked => "linked",
                MyCharacterState.Never => "never linked",
                _ => "unlinked",
            };
            ImGui.BulletText($"{mine} ({state})");
            ImGui.SameLine();
            if (mine.State == MyCharacterState.Linked)
            {
                using (ImRaii.Disabled(!records.CanUnlink(mine)))
                    if (ImGui.Button("Unlink")) records.SetMine(mine.Name, mine.World, MyCharacterState.Unlinked);
                if (!records.CanUnlink(mine) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Your only linked character. Unpair instead to end a pairing.");
            }
            else if (ImGui.Button("Link"))
            {
                records.SetMine(mine.Name, mine.World, MyCharacterState.Linked);
            }
            ImGui.PopID();
        }
        if (config.MyCharacters.Count == 0 && records.Current is null)
            IconGlyph.WrappedDisabled("Log in to link this character.");

        foreach (var pairing in activePairings)
        {
            ImGui.PushID($"peerCharacters_{pairing.Id}");
            ImGui.Spacing();
            var peerIsOwner = pairing.Direction == PairingDirection.SubSide;
            var who = pairing.ConfirmedCharacters.FirstOrDefault()?.ToString() ?? "them";
            ImGui.TextUnformatted(peerIsOwner ? $"Your Owner {who}" : $"Your Sub {who}");
            foreach (var character in pairing.PeerCharacters.ToList())
            {
                ImGui.PushID(character.ToString());
                var state = character.State switch
                {
                    PeerCharacterState.Confirmed => character.Is(pairing.PeerName, pairing.PeerWorld) ? "confirmed, current" : "confirmed",
                    PeerCharacterState.Pending => "waiting for you to confirm",
                    _ => "rejected",
                };
                ImGui.BulletText($"{character} ({state})");
                if (peerIsOwner)
                {
                    if (character.State != PeerCharacterState.Confirmed)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("Confirm")) records.Confirm(pairing, character);
                    }
                    if (character.State == PeerCharacterState.Pending)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("Reject")) records.Reject(pairing, character);
                    }
                    if (records.CanRemove(pairing, character))
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("Remove")) records.Remove(pairing, character);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Stop accepting commands from this character.");
                    }
                }
                ImGui.PopID();
            }

            if (!peerIsOwner && config.LinkedCharacters.Any())
            {
                ImGui.TextDisabled("Your characters, as they see them:");
                foreach (var mine in config.LinkedCharacters)
                {
                    var state = CharacterRecordService.AcceptanceBySub(pairing, mine) switch
                    {
                        CharacterRecordService.SubAcceptance.Confirmed => "confirmed",
                        CharacterRecordService.SubAcceptance.Waiting => "waiting for them to confirm",
                        _ => "not reported yet",
                    };
                    ImGui.BulletText($"{mine} ({state})");
                }
                if (!pairing.PeerPublishesCharacters)
                    IconGlyph.WrappedDisabled("Their plugin hasn't sent its list yet. The character you paired from keeps working either way. If this doesn't change after they've logged in on 0.2.7 or later, their plugin may be out of date.");
            }
            ImGui.PopID();
        }
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

    private void DrawPairWithSection(PluginConfig config)
    {
        codePairingView.Draw(config);

        ImGui.Spacing();
        if (plugin.RelayClient.LastReachable is false)
            IconGlyph.WrappedColored(Theme.Warning, "Couldn't reach Oathbound's server last time. Pairing and commands need it; panic always works.");
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
        IconGlyph.HelpMarker("Starts a chat phrase for your Reactions, e.g. \"collar kneel\". Commands from your Owner don't use it - they come through Oathbound directly.");

        if (subLocked)
            IconGlyph.WrappedColored(Theme.TextMuted, "Role and trigger phrase are locked while you hold a Sub-side pairing - unpair it above to change them.");
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

    private static void DrawRedrawCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Sync, "Mod changes");
        ImGui.Separator();
        if (ImGuiCheckbox("Redraw my character after a mod change", config.RedrawAfterModChange, out var redraw))
        {
            config.RedrawAfterModChange = redraw;
            config.Save();
        }
        IconGlyph.HelpMarker("Only turn this on if animations, restraint mods or Reaction mods don't show up until something else redraws you. Penumbra can redraw on its own, and each extra redraw makes a sync plugin re-send your character, so people synced with you see you flash.");
    }

    private void DrawBuiltinMoodlesCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Tag, "Moodles");
        ImGui.Separator();
        if (ImGuiCheckbox("Use Oathbound's moodles", config.UseBuiltinMoodles, out var use))
            plugin.MoodlesCommand.SetUseBuiltinMoodles(use);
        IconGlyph.HelpMarker("Shows Collared, Leashed, Caged, Restrained, Gagged or Blindfolded while that's happening to you. They're never added to your Moodles library. A moodle you picked yourself for your collar, leash or a restraint is used instead, as is one your Owner chose.");
        if (config.UseBuiltinMoodles && plugin.MoodlesIpc.BuiltinProblem is { } problem)
            IconGlyph.WrappedColored(Theme.Warning, problem);
    }

    private static readonly string[] AchievementSoundNames =
        ["No sound", .. Enumerable.Range(1, 16).Select(n => $"<se.{n}>")];

    /// Shown for both roles, so it sits outside the Sub-only permissions block.
    private void DrawAchievementsCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Trophy, "Achievements");
        ImGui.Separator();
        var state = config.Achievements;
        if (ImGuiCheckbox("Show a popup when I unlock an achievement", state.ShowToasts, out var show))
        {
            state.ShowToasts = show;
            config.Save();
        }
        var sound = Math.Clamp(state.Sound, 0, 16);
        Layout.ItemWidth(140);
        if (ImGui.Combo("Sound##achievementSound", ref sound, AchievementSoundNames, AchievementSoundNames.Length))
        {
            state.Sound = sound;
            config.Save();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(sound == 0))
        {
            if (ImGui.Button("Preview##achievementSound"))
                plugin.AchievementToastWindow.PlaySound(sound);
        }
        IconGlyph.HelpMarker("The same sounds as <se.1> to <se.16> in chat, at your game's sound effect volume.");
    }

    private void DrawChatCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Comments, "Chat");
        ImGui.Separator();
        var sound = Math.Clamp(config.ChatSound, 0, GameSound.Count);
        Layout.ItemWidth(140);
        if (ImGui.Combo("New message sound##chatSound", ref sound, AchievementSoundNames, AchievementSoundNames.Length))
        {
            config.ChatSound = sound;
            config.Save();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(sound == 0))
        {
            if (ImGui.Button("Preview##chatSound"))
                GameSound.Play(sound);
        }
        IconGlyph.HelpMarker("Plays when a chat message arrives, unless that conversation is open in front of you. The same sounds as <se.1> to <se.16> in chat, at your game's sound effect volume.");
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

    /// Its own card: rulebook consequences fire with no click from either person.
    private void DrawRulebookCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Book, "Rulebook");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("A rulebook you accept runs by itself: when you break an oath, enter a place, or your Owner comes near, its consequences happen without anyone clicking anything. Only categories that don't send chat or move you are allowed, and your safeword pauses it.");

        if (ImGuiCheckbox("I understand a rulebook acts on its own", config.RulebookAcknowledged, out var newAck))
        {
            config.RulebookAcknowledged = newAck;
            if (!newAck)
                config.Permissions.Rulebook = false;
            config.Save();
        }
        IconGlyph.HelpMarker("Needed before the Rulebook permission can be turned on.");
    }

    /// Its own card: a cage moves the character on its own and closes the Sub's escapes until it opens.
    private void DrawCageCard(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Lock, "Cage");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Your Owner can put you in a cage they saved. Your character is walked to it and kept inside until its time is up or they open it, including while you're away from the keyboard. While caged you can't leave the area, change any Oathbound permission or acknowledgement, struggle, use a restraint key, take a restraint off, run commands on yourself or stop what your Owner does. Only the categories you allow apply. Panic and unpairing always open the cage.");

        if (ImGuiCheckbox("I understand a cage holds me until it opens", config.CageAcknowledged, out var newAck))
        {
            config.CageAcknowledged = newAck;
            if (!newAck)
                config.Permissions.Cage = false;
            config.Save();
        }
        IconGlyph.HelpMarker("Needed before the Cage permission can be turned on. Automating your character is against the game's rules - see the README.");
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
