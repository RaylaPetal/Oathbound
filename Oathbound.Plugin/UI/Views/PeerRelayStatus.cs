using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// What the relay last said about the other person's live connection, with the update reminder when their version is
/// too old for live messaging.
public static class PeerRelayStatus
{
    public static string Describe(PairingState pairing) => pairing.PeerRelayState switch
    {
        PeerRelayState.Reachable => "Online",
        PeerRelayState.Offline => "Offline",
        PeerRelayState.NeedsUpdate => "Needs to update Oathbound",
        _ => "Not heard from yet",
    };

    public static void Draw(Plugin plugin, PairingState pairing)
    {
        var state = pairing.PeerRelayState;
        var color = state switch
        {
            PeerRelayState.Reachable => Theme.Success,
            PeerRelayState.NeedsUpdate => Theme.Warning,
            _ => Theme.TextMuted,
        };
        ImGui.TextColored(color, Describe(pairing));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(state == PeerRelayState.NeedsUpdate
                ? $"{pairing.PeerName}'s Oathbound is from before commands moved off tells, so nothing you send reaches them until they update."
                : "From the last command or notice between you.");
        if (state == PeerRelayState.NeedsUpdate)
            DrawAskToUpdate(plugin, pairing);
    }

    /// Sends one plain tell only when pressed, at most once a minute per pairing.
    public static void DrawAskToUpdate(Plugin plugin, PairingState pairing)
    {
        var wait = plugin.ChatSender.ReminderWaitSeconds(pairing);
        using (ImRaii.Disabled(wait > 0))
        {
            if (ImGui.SmallButton($"Ask them to update##askUpdate_{pairing.Id}"))
                plugin.ChatSender.SendUpdateReminder(pairing);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(wait > 0
                ? $"Sent - you can send it again in {wait}s."
                : $"Sends {pairing.PeerName} one tell:\n\"{Commands.ChatSender.UpdateReminderText}\"");
    }
}
