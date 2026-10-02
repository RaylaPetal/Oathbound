using System;
using System.Collections.Generic;
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
            if (estimate.Restrained)
                parts.Add(estimate.RestrainedUntilUtc is { } until
                    ? $"restrained (unlocks in {Commands.RestraintLock.Format(until - DateTime.UtcNow)})"
                    : "restrained");
            if (estimate.Leashed) parts.Add("leashed");
            IconGlyph.WrappedColored(Theme.Success, $"Estimated state: {string.Join(", ", parts)}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear estimate##ownerStatusClear"))
                plugin.OwnerStatusEstimates.Clear(pairing.Id);
        }
        IconGlyph.HelpMarker("An estimate from the commands you sent - it drives the icons and leash line you see. The leash is the one exception: your Sub's client tells yours when their leash comes off on their side. For everything else, your client can't see your Sub's panic, their own releases, or a command their client refused; use Clear estimate if it's stale.");
    }
}
