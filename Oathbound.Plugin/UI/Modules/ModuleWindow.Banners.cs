using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.UI;

/// The Active banner at the top of each module with state: the Sub's real state, or what the Owner last sent.
public sealed partial class ModuleWindow
{
    private static readonly HashSet<string> BannerModules = ["title", "outfit", "animation", "moodles", "restraints", "toycontrol", "collar", "follow"];

    private void DrawActiveBanner(bool isOwner)
    {
        if (!BannerModules.Contains(activeModule))
            return;
        if (isOwner)
            DrawOwnerBanner();
        else
            ActiveBanner.Draw("activeBanner", "Active now", DrawSubBannerBody);
    }

    /// False when there's nothing to show, so the banner says so.
    private static bool BannerLine(string? text, params string?[] pills)
    {
        if (text is null)
            return false;
        ImGui.TextUnformatted(text);
        foreach (var pill in pills)
            if (pill is not null)
                ActiveBanner.Pill(pill);
        return true;
    }

    internal static string MoodleRemoveCommand(EstimatedMoodle moodle) =>
        moodle.CustomId is { } id ? new CustomMoodle { Id = id }.RemoveCommand() : $"moodle {CustomMoodle.RemoveWord} {CommandSelector.Quote(moodle.Name)}";

    private void EndNowButton(string id, string command, string help)
    {
        ImGui.SameLine();
        SendButton.Draw(plugin, new SendSpec($"bannerEnd_{id}", "End now") { Command = command, Help = help, Small = true });
    }

    private string PeerName(Guid? pairingId, string fallback) =>
        pairingId is { } id && plugin.Configuration.FindPairingById(id)?.PeerName is { } name ? name : fallback;

    private bool DrawSubBannerBody()
    {
        var state = plugin.RuntimeState;
        switch (activeModule)
        {
            case "title":
                return BannerLine(state.TitleText ?? (state.TitleApplied ? "A title" : null), state.TitleForceLocked ? "locked by your Owner" : null,
                    TimeText.Left(plugin.Configuration.OwnerLocks.Title?.ExpiresAtUtc));
            case "outfit":
                return BannerLine(state.OutfitName ?? (state.OutfitForceLocked ? "An outfit" : null), state.OutfitForceLocked ? "locked by your Owner" : null,
                    TimeText.Left(plugin.Configuration.OwnerLocks.Outfit?.ExpiresAtUtc));
            case "animation":
                var hold = plugin.GestureCommand.HoldSecondsLeft;
                return BannerLine(plugin.GestureCommand.HeldName, hold is { } left ? TimeText.Left(TimeSpan.FromSeconds(left)) : null);
            case "moodles":
                return DrawSubMoodleLines();
            case "restraints":
                return DrawWornRestraintLines();
            case "toycontrol":
                return BannerLine(plugin.ToyControlCommand.CurrentStatus is { } toy ? $"{toy.IntensityPercent}% - {toy.Description}" : null,
                    state.ToyControlForceLocked ? "locked by your Owner" : null);
            case "collar":
                return BannerLine(state.CollarForceLocked ? $"Collar locked by {PeerName(plugin.Configuration.CollarOwningPairingId, "your Owner")}" : null);
            case "follow":
                var follow = plugin.FollowCommand;
                return BannerLine(follow.IsLeashed ? $"Leashed to {PeerName(follow.LeashedPairingId, "your Owner")}, {follow.EffectiveLength:0} yalms" : null,
                    follow.IsPaused ? "paused" : null, follow.IsSlack ? "slack" : null);
            default:
                return false;
        }
    }

    private bool DrawSubMoodleLines()
    {
        var catalog = plugin.Configuration.MoodlesMapping.LocalCatalog.Values;
        var any = false;
        foreach (var locked in plugin.Configuration.OwnerLocks.Moodles.ToList())
            any |= BannerLine(MoodlesTextFormat.StripMarkup(locked.Name), "locked by your Owner", TimeText.Left(locked.ExpiresAtUtc));
        foreach (var (source, statusId) in plugin.Configuration.AttachedMoodleHolds.ToList())
        {
            if (source.StartsWith(AttachedMoodleLedger.LockPrefix))
                continue;
            var name = catalog.FirstOrDefault(e => e.StatusId == statusId.ToString())?.Name ?? "A moodle";
            var from = source == AttachedMoodleLedger.OutfitSource ? "outfit"
                : source == AttachedMoodleLedger.FollowSource ? "leash"
                : source == AttachedMoodleLedger.CollarSource ? "collar"
                : source.StartsWith(AttachedMoodleLedger.RestraintPrefix) ? "restraint"
                : "from your Owner";
            any |= BannerLine(MoodlesTextFormat.StripMarkup(name), from);
        }
        return any;
    }

    /// Each worn restraint with its lock, and Take off / Struggle / key Unlock where allowed.
    private bool DrawWornRestraintLines()
    {
        var restraints = plugin.RestraintCommand;
        var worn = restraints.Worn.ToList();
        if (worn.Count == 0)
        {
            restraintActionResult = null;
            return false;
        }

        foreach (var w in worn)
        {
            ImGui.PushID($"worn_{w.RuntimeId}");
            var l = w.Lock;
            BannerLine(w.Reference,
                l is null ? "not locked" : l.ExpiresAtUtc is { } end ? $"locked, {TimeText.Left(end)}" : "locked",
                l is not null ? $"by {PeerName(l.ByPairingId, "your Owner")}" : null,
                l?.Struggle.Allowed == true ? "struggle" : null,
                l?.Key is not null ? "key" : null);
            if (l is null)
            {
                using var caged = ImRaii.Disabled(plugin.CageService.IsCaged);
                if (ImGui.Button("Take off"))
                    restraintActionResult = restraints.TakeOff(w.RuntimeId)
                        ? StatusMessage.Ok($"{w.Reference} taken off.")
                        : StatusMessage.Error(restraints.LastFailureReason ?? $"{w.Reference} couldn't be taken off.");
            }
            else
            {
                if (RestraintCommand.StruggleAvailable(w) is { } setting)
                    DrawStruggleButton(w, setting);
                if (l.Key is not null)
                    DrawRestraintKeyRow(w);
            }
            ImGui.PopID();
        }
        Status.Line(restraintActionResult);
        return true;
    }

    private void DrawOwnerBanner()
    {
        if (plugin.Configuration.GetActivePairing() is not { Direction: PairingDirection.OwnerSide, IsPaired: true } pairing)
            return;
        if (activeModule == "collar")
        {
            ActiveBanner.Draw("activeBanner", $"{pairing.PeerName}'s collar", () =>
            {
                if (plugin.OwnerCollarStatus.For(pairing) is not { } status)
                    return false;
                return BannerLine($"Collar {status.State}", OwnerCollarStatusStore.IsStale(status) ? "not checked in lately" : null);
            }, "Reported by your Sub's client.");
            return;
        }

        var estimate = plugin.OwnerStatusEstimates.ForBanner(pairing);
        var mark = CommandMarkText.Describe(estimate?.MarkFor(activeModule), pairing.PeerName ?? "your Sub", ModuleCommandWord(activeModule));
        ActiveBanner.Draw("activeBanner", $"Sent to {pairing.PeerName}", () =>
            {
                var any = estimate is not null && DrawOwnerBannerBody(estimate);
                if (mark is null)
                    return any;
                if (!any)
                    ImGui.TextDisabled("Nothing active.");
                Status.Line(mark);
                return true;
            },
            pairing.PeerSendsReceipts
                ? "Last sent by you that your Sub's client didn't refuse."
                : "Last sent by you - your client can't see your Sub's side.",
            estimate is null ? null : () => plugin.OwnerStatusEstimates.ClearModule(pairing.Id, activeModule));
    }

    private static string ModuleCommandWord(string module) => module switch
    {
        "animation" => "animation",
        "moodles" => "moodle",
        "restraints" => "restraint",
        "toycontrol" => "toy",
        "follow" => "leash",
        _ => module,
    };

    private bool DrawOwnerBannerBody(OwnerStatusEstimate estimate)
    {
        switch (activeModule)
        {
            case "title":
                if (!BannerLine(estimate.LastTitle, TimeText.Left(estimate.TitleEndsAtUtc) ?? "locked"))
                    return false;
                EndNowButton("title", "title clear", "Clears the title now.");
                return true;
            case "outfit":
                if (!BannerLine(estimate.LastOutfit, TimeText.Left(estimate.OutfitEndsAtUtc) ?? (estimate.LastOutfitLocked ? "locked" : null)))
                    return false;
                if (estimate.LastOutfitLocked)
                    EndNowButton("outfit", "outfit unlock", "Unlocks the outfit now. Your Sub keeps wearing it.");
                return true;
            case "animation":
                return BannerLine(estimate.LastAnimation);
            case "moodles":
                var any = false;
                foreach (var moodle in estimate.Moodles.ToList())
                {
                    any |= BannerLine(moodle.Name, TimeText.Left(moodle.EndsAtUtc));
                    EndNowButton($"moodle_{moodle.Name}", MoodleRemoveCommand(moodle), "Takes this moodle off now.");
                }
                return any;
            case "restraints":
                foreach (var restraint in estimate.Restraints.ToList())
                {
                    BannerLine(restraint.Reference,
                        restraint.ExpiresAtUtc is { } until ? TimeText.Left(until) : "locked",
                        restraint.HasKey ? "key" : null);
                    SendButton.Draw(plugin, new SendSpec($"bannerUnlock_{restraint.Reference}", "Unlock")
                    {
                        Command = $"restraint unlock {restraint.Reference}",
                        Help = "Unlocks and takes off this restraint now.",
                        Small = true,
                    });
                }
                return estimate.Restraints.Count > 0;
            case "toycontrol":
                return BannerLine(estimate.LastToy);
            case "follow":
                return BannerLine(estimate.Leashed ? $"Leashed, {estimate.LeashLength:0} yalms" : null, estimate.Leashed && estimate.PauseInDuties ? "pauses in duties" : null);
            default:
                return false;
        }
    }
}
