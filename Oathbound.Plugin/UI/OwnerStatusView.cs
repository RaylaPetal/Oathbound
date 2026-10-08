using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// The active Sub's estimated state with its caveat and a Clear button.
public static class OwnerStatusView
{
    public static void Draw(Plugin plugin)
    {
        if (plugin.Configuration.GetActivePairing() is not { Direction: PairingDirection.OwnerSide } pairing)
            return;

        var estimate = plugin.OwnerStatusEstimates.For(pairing);
        if (estimate is null)
        {
            IconGlyph.WrappedDisabled("Estimated state: nothing active.");
        }
        else
        {
            var parts = new List<string>();
            if (estimate.Gagged) parts.Add("gagged");
            if (estimate.Restrained) parts.Add("restrained");
            if (estimate.Leashed) parts.Add("leashed");
            Layout.TextWithActions($"Estimated state: {string.Join(", ", parts)}", Layout.ActionsWidth("Clear estimate"), t => ImGui.TextColored(Theme.Success, t));
            if (ImGui.Button("Clear estimate##ownerStatusClear"))
                plugin.OwnerStatusEstimates.Clear(pairing.Id);
            DrawRestraints(plugin, estimate);
        }
        if (plugin.OwnerStatusEstimates.ForBanner(pairing) is { } sent)
            DrawTimedLocks(plugin, sent);
        IconGlyph.HelpMarker("Based on what you sent - your client can't see your Sub's side. Clear it if it's wrong.");
    }

    /// Titles, outfits and moodles sent with a timer, each with an End now.
    private static void DrawTimedLocks(Plugin plugin, Commands.OwnerStatusEstimate estimate)
    {
        void Row(string what, DateTime end, string command, string id)
        {
            Layout.TextWithActions($"{what} (comes off in {Commands.RestraintLock.Format(end - DateTime.UtcNow)})", Layout.ActionsWidth("End now"));
            if (ImGui.Button($"End now##ownerEnd_{id}"))
                plugin.ChatSender.Send(plugin.ChatComposer.Compose(command));
        }
        if (estimate.LastTitle is { } title && estimate.TitleEndsAtUtc is { } titleEnd)
            Row($"Title \"{title}\"", titleEnd, "title clear", "title");
        if (estimate.LastOutfit is { } outfit && estimate.OutfitEndsAtUtc is { } outfitEnd)
            Row($"Outfit \"{outfit}\"", outfitEnd, "outfit unlock", "outfit");
        foreach (var moodle in estimate.Moodles.Where(m => m.EndsAtUtc is not null).ToList())
            Row($"Moodle \"{moodle.Name}\"", moodle.EndsAtUtc!.Value, ModuleWindow.MoodleRemoveCommand(moodle), $"moodle_{moodle.Name}");
    }

    /// One row per restraint sent, with its lock and an Unlock that sends `restraint unlock <name>`.
    private static void DrawRestraints(Plugin plugin, Commands.OwnerStatusEstimate estimate)
    {
        foreach (var restraint in estimate.Restraints.ToList())
        {
            var lockText = restraint.ExpiresAtUtc is { } until
                ? $"unlocks in {Commands.RestraintLock.Format(until - DateTime.UtcNow)}"
                : "locked";
            if (restraint.HasKey)
                lockText += ", key";
            Layout.TextWithActions($"{restraint.Reference} ({lockText})", Layout.ActionsWidth("Unlock"));
            if (ImGui.Button($"Unlock##ownerUnlock_{restraint.Reference}"))
                plugin.ChatSender.Send(plugin.ChatComposer.Compose($"restraint unlock {restraint.Reference}"));
        }
    }
}
