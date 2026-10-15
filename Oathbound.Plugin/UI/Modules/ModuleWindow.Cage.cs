using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.UI;

/// The Owner's saved cages and what their Sub's client last reported; the Sub's own cage.
public sealed partial class ModuleWindow
{
    private static readonly string[] CageLockModes = ["Until opened", "Timed"];
    private const int DefaultCageLockMinutes = 60;

    private string newCageName = "";
    private float newCageRadius = 2;
    private float newCageRadiusZ = 2;
    private bool newCagePinned;
    private string? cageSaveError;
    private Guid? cageViewedPairingId;
    private DateTime cagePreviewUntil;

    /// The Owner's Cage module was drawn in the last moment, so every saved cage here is previewed on the ground.
    public bool CagePreviewActive => DateTime.UtcNow < cagePreviewUntil;

    /// The half-size of the cage being made, drawn around where the Owner stands; null when not previewing.
    public System.Numerics.Vector2? NewCagePreviewHalf =>
        CagePreviewActive && plugin.Configuration.ShowCageBounds ? CageLimits.HalfExtents(newCagePinned ? 0 : newCageRadius, newCageRadiusZ) : null;

    private void DrawCageModule(bool isOwner)
    {
        IconGlyph.Text(FontAwesomeIcon.Dungeon, "Cage");
        ImGui.Separator();
        if (isOwner)
            DrawOwnerCage(DrawOwnerCanSendBanner());
        else
        {
            using (Section.Begin("cageSub", "Your cage"))
                DrawSubCage();
        }
    }

    private void DrawOwnerCage(bool canSend)
    {
        var config = plugin.Configuration;
        cagePreviewUntil = DateTime.UtcNow.AddSeconds(0.5);
        IconGlyph.WrappedDisabled("Save a cage where you stand, then send it. If your Sub is there it closes now and walks them to the spot; otherwise it waits for them to arrive. While caged they can't leave or change their permissions - only what they allow applies. Panic always opens it.");

        if (config.ActivePairing is { Direction: PairingDirection.OwnerSide } pairing)
        {
            // Opening a Sub's view checks their cage soon, at most once a minute.
            if (cageViewedPairingId != pairing.Id)
            {
                cageViewedPairingId = pairing.Id;
                plugin.OwnerCageStatus.RequestRefresh();
            }
            using (Section.Begin("cageStatus", "Status"))
                DrawOwnerCageStatus(pairing, canSend);
        }

        using (Section.Begin("cageSave", "Save a cage here"))
        {
            DrawCageSaveForm();
            IconGlyph.WrappedDisabled("While this module is open, your saved cages here are outlined on the ground. A cage you sent stays outlined while your Sub is in it or it's waiting for them.");
        }

        using (Section.Begin("cageSaved", "Saved cages"))
        {
            if (config.SavedCages.Count == 0)
                IconGlyph.WrappedDisabled("No cages yet. Stand where it should be and save one above.");
            foreach (var cage in config.SavedCages.ToList())
            {
                ImGui.PushID($"cage_{cage.Id}");
                DrawSavedCage(cage, canSend);
                ImGui.PopID();
            }
        }
    }

    private void DrawOwnerCageStatus(PairingState pairing, bool canSend)
    {
        var status = plugin.OwnerCageStatus.For(pairing);
        var sent = plugin.OwnerCageStatus.LastSent(pairing);
        var name = pairing.PeerName ?? "Your Sub";
        if (status is null)
            IconGlyph.WrappedDisabled(sent is { Open: false }
                ? $"You sent \"{sent.Name}\" {Ago(sent.At)}. Waiting for {name}'s client to report it."
                : $"{name}'s client hasn't reported a cage.");
        else
        {
            var since = Ago(status.StateAt);
            switch (status.State)
            {
                case CageStatusReporter.Caged:
                    IconGlyph.WrappedColored(Theme.Warning, status.EndsAt is { } end
                        ? $"{name} is caged ({RestraintLock.Format(end - DateTimeOffset.UtcNow)} left), since {since}."
                        : $"{name} is caged until you open it, since {since}.");
                    break;
                case CageStatusReporter.Armed:
                    IconGlyph.WrappedDisabled(status.EndsAt is { } wait
                        ? $"The cage is waiting for {name} to arrive (until {RestraintLock.Format(wait - DateTimeOffset.UtcNow)} from now), since {since}."
                        : $"The cage is waiting for {name} to arrive, since {since}.");
                    break;
                default:
                    IconGlyph.WrappedDisabled($"{name} isn't caged (as of {since}).");
                    break;
            }
            if (sent is { Open: false } s && s.At > status.StateAt.AddSeconds(5))
                IconGlyph.WrappedDisabled($"You sent \"{s.Name}\" {Ago(s.At)} - checking every few minutes until it's reported.");
        }

        using (ImRaii.Disabled(!canSend))
        {
            if (ImGui.Button("Open cage"))
                ReportCageSend(CageSendAction.TryOpen(plugin));
        }
        IconGlyph.HelpMarker("Opens the cage you put your Sub in, or calls off one that's still waiting for them. Only the Owner who caged them can open it.");
    }

    private void DrawCageSaveForm()
    {
        Layout.ItemWidth(200);
        ImGui.InputTextWithHint("##newCageName", "Name, e.g. Pet Corner", ref newCageName, CageLimits.MaxNameLength);
        Layout.ContinueRowOrWrap(Layout.Scaled(260));
        DrawRadiusFields("newCage", ref newCageRadius, ref newCageRadiusZ, ref newCagePinned);

        var valid = CageLimits.IsValidName(newCageName);
        using (ImRaii.Disabled(!valid))
        {
            if (ImGui.Button("Save cage here"))
            {
                if (CagePlaces.Capture(out var label, out var error) is { } place && Plugin.ObjectTable.LocalPlayer is { } player)
                {
                    plugin.Configuration.SavedCages.Add(new SavedCage
                    {
                        Name = newCageName.Trim(),
                        PlaceLabel = label,
                        Place = place,
                        Position = player.Position,
                        Radius = newCagePinned ? 0 : newCageRadius,
                        RadiusZ = newCageRadiusZ == newCageRadius ? null : newCageRadiusZ,
                    });
                    plugin.Configuration.Save();
                    newCageName = "";
                    cageSaveError = null;
                }
                else
                    cageSaveError = error ?? "Could not read your current location.";
            }
        }
        IconGlyph.HelpMarker("Saves where you're standing. Inside a house it's that exact house or apartment, never another one of the same size. Names can't contain quotes.");
        ImGui.SameLine();
        DrawShowCageBounds();
        if (cageSaveError is not null)
            IconGlyph.WrappedColored(Theme.Warning, cageSaveError);
    }

    private void DrawSavedCage(SavedCage cage, bool canSend)
    {
        var config = plugin.Configuration;
        ImGui.TextUnformatted(cage.Name);
        ImGui.SameLine();
        IconGlyph.WrappedDisabled($"{cage.PlaceLabel} - {CageLimits.DescribeSize(cage.Radius, cage.RadiusZ)}");

        var name = cage.Name;
        Layout.ItemWidth(160);
        if (ImGui.InputText("##name", ref name, CageLimits.MaxNameLength) && CageLimits.IsValidName(name))
        {
            cage.Name = name.Trim();
            config.Save();
        }
        Layout.ContinueRowOrWrap(Layout.Scaled(260));
        var radius = CageLimits.RoundSize(cage.Radius);
        var radiusZ = CageLimits.RoundSize(cage.RadiusZ ?? radius);
        var pinned = cage.Radius <= 0;
        if (DrawRadiusFields("saved", ref radius, ref radiusZ, ref pinned))
        {
            cage.Radius = pinned ? 0 : radius;
            cage.RadiusZ = radiusZ == radius ? null : radiusZ;
            config.Save();
        }

        var mode = cage.LockSeconds is null ? 0 : 1;
        Layout.ItemWidth(120);
        if (ImGui.Combo("##lockMode", ref mode, CageLockModes, CageLockModes.Length))
        {
            cage.LockSeconds = mode == 0 ? null : DefaultCageLockMinutes * 60;
            config.Save();
        }
        if (cage.LockSeconds is { } seconds)
        {
            Layout.ContinueRowOrWrap(Layout.Scaled(110));
            var minutes = seconds / 60;
            Layout.ItemWidth(80);
            if (ImGui.InputInt("min##lockMinutes", ref minutes, 5, 60))
            {
                cage.LockSeconds = (int)RestraintLock.Timed(TimeSpan.FromMinutes(Math.Max(1, minutes))).Duration!.Value.TotalSeconds;
                config.Save();
            }
        }
        IconGlyph.HelpMarker("Until opened: they stay caged until you open it (or they panic). Timed: it opens by itself this long after it closes - the time starts when they're actually caged, not when you send it, and keeps counting while they're logged out.");

        var armHours = CageLimits.ClampArmSeconds(cage.ArmSeconds) / 3600;
        Layout.ItemWidth(80);
        if (ImGui.InputInt("h waiting##armHours", ref armHours, 1, 24))
        {
            cage.ArmSeconds = CageLimits.ClampArmSeconds(Math.Max(1, armHours) * 3600);
            config.Save();
        }
        IconGlyph.HelpMarker("If your Sub isn't there when you send it, the cage waits this long (1 hour to 7 days) for them to arrive, then gives up.");

        using (ImRaii.Disabled(!canSend))
        {
            if (ImGui.Button("Send"))
                ReportCageSend(CageSendAction.TrySend(plugin, cage));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Cages your Sub here: it closes now if they're there, otherwise when they arrive.");
        ImGui.SameLine();
        var apartment = CagePlaces.IsApartment(cage.Place);
        using (ImRaii.Disabled(!canSend || apartment))
        {
            if (ImGui.Button("Bring & lock"))
                ReportCageSend(CageSendAction.TrySend(plugin, cage, bring: true));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(apartment
                ? "Apartments can't be travelled to. Send it instead; it closes when your Sub walks in."
                : "Like Send, but if your Sub is elsewhere their character travels here by itself (world, teleport, then walking) and is locked in on arrival. Needs their Teleport permission. For a house, they're also taken through the door and walked to the cage.");
        ImGui.SameLine();
        if (ImGui.Button("Move here"))
        {
            if (CagePlaces.Capture(out var label, out var error) is { } place && Plugin.ObjectTable.LocalPlayer is { } player)
            {
                cage.Place = place;
                cage.PlaceLabel = label;
                cage.Position = player.Position;
                config.Save();
                cageSaveError = null;
            }
            else
                cageSaveError = error;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Moves this cage to where you're standing now.");
        ImGui.SameLine();
        if (ImGui.Button("Delete"))
        {
            config.SavedCages.Remove(cage);
            config.Save();
        }
        ImGui.Separator();
    }

    /// Returns whether any changed.
    private static bool DrawRadiusFields(string id, ref float radius, ref float radiusZ, ref bool pinned)
    {
        var changed = ImGui.Checkbox($"Pinned##{id}Pinned", ref pinned);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pinned: they can't move at all. Otherwise they can move freely inside the rectangle.");
        if (pinned)
            return changed;
        ImGui.SameLine();
        Layout.ItemWidth(80);
        if (ImGui.SliderFloat($"X##{id}Radius", ref radius, CageLimits.MinRadius, CageLimits.MaxRadius, "%.1f"))
        {
            radius = CageLimits.RoundSize(radius);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How far they can go from the spot along X (east-west), in yalms. Ctrl+click to type an exact value.");
        ImGui.SameLine();
        Layout.ItemWidth(80);
        if (ImGui.SliderFloat($"Z yalms##{id}RadiusZ", ref radiusZ, CageLimits.MinRadius, CageLimits.MaxRadius, "%.1f"))
        {
            radiusZ = CageLimits.RoundSize(radiusZ);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How far they can go from the spot along Z (north-south), in yalms. Set both the same for a square. Ctrl+click to type an exact value.");
        return changed;
    }

    private static void ReportCageSend((bool Success, string? Error) result)
    {
        if (result.Success)
            return;
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Oathbound",
            Content = result.Error ?? "The cage command couldn't be sent.",
            Type = NotificationType.Warning,
            InitialDuration = TimeSpan.FromSeconds(5),
        });
    }

    private void DrawShowCageBounds()
    {
        var config = plugin.Configuration;
        var show = config.ShowCageBounds;
        if (ImGui.Checkbox("Show cage bounds", ref show))
        {
            config.ShowCageBounds = show;
            config.Save();
        }
        IconGlyph.HelpMarker("Draws the cage you're making around where you stand, at the size you've set, so you can see it before saving. It follows you until you save.");
    }

    private void DrawSubCage()
    {
        var config = plugin.Configuration;
        if (!config.Permissions.Cage || !config.CageAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Your Cage permission is off, so no Owner can cage you. Turn it on in Settings > Permissions once you've read its acknowledgement on the ToS tab.");

        if (plugin.CageService.Cage is not { } cage)
        {
            IconGlyph.WrappedDisabled("You're not in a cage.");
            return;
        }

        var caged = cage.Phase == CagePhase.Caged;
        IconGlyph.WrappedColored(caged ? Theme.Warning : Theme.TextMuted,
            caged ? $"{cage.OwnerName} has you in \"{cage.Name}\"." : $"{cage.OwnerName} set \"{cage.Name}\" - it closes when you're at {CagePlaces.Describe(cage.Place)}.");
        if (caged)
            IconGlyph.WrappedDisabled(cage.EndsAtUtc is { } end ? $"{RestraintLock.Format(end - DateTime.UtcNow)} left." : "Until they open it.");
        else if (!cage.HasClosed)
            IconGlyph.WrappedDisabled($"It waits {RestraintLock.Format(cage.ArmExpiresAtUtc - DateTime.UtcNow)} more for you to arrive. Turning your Cage permission off calls it off.");
        IconGlyph.WrappedDisabled(cage.IsPinned ? "Pinned: you can't move while it's closed." : CageLimits.HalfExtents(cage.Radius, cage.RadiusZ) is var half && half.X == half.Y
            ? $"You can move within {half.X:0.#} yalms of its spot."
            : $"You can move within {half.X:0.#} yalms of its spot along X (east-west) and {half.Y:0.#} along Z (north-south).");
        IconGlyph.WrappedDisabled("While caged you can't leave, change your permissions, struggle, use keys or take restraints off. Panic always opens it.");
    }

    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        return span < TimeSpan.FromMinutes(1) ? "just now" : $"{RestraintLock.Format(span)} ago";
    }
}
