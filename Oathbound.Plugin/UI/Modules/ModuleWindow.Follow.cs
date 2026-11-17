using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Glamourer.Api.Enums;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    private void DrawFollowLeashModule()
    {
        var config = plugin.Configuration;
        DrawFixedWordRow("leashFixed",
            new FixedWordInfo("Leash", ControlWords.Leash, "Puts on a leash of your Owner's chosen length. You move freely inside it and get pulled along."),
            new FixedWordInfo("Unleash", ControlWords.Unleash, "Takes the leash off and gives you full control back."));

        using (Section.Begin("leashLimit", "Leash length"))
        {
            var follow = config.Aliases.Follow;
            var maxLength = follow.MaxLeashLengthYalms;
            ItemWidth(200);
            if (ImGui.SliderInt("Longest leash I'll accept (yalms)##subLeashMax", ref maxLength, LengthOption.MinYalms, LengthOption.MaxYalms))
            {
                follow.MaxLeashLengthYalms = Math.Clamp(maxLength, LengthOption.MinYalms, LengthOption.MaxYalms);
                config.Save();
            }
            IconGlyph.HelpMarker("A longer leash from your Owner is shortened to this.");
        }

        using (Section.Begin("leashMoodle", "Attached moodle"))
        {
            var follow = config.Aliases.Follow;
            if (DrawAttachedMoodlePicker("leash", follow.AttachedMoodle, config, out var leashMoodle))
            {
                follow.AttachedMoodle = leashMoodle;
                config.Save();
            }
        }
    }

    /// Only changes what this client draws; the leash keeps working with its line hidden.
    /// Sharing has no switch, so it's at least always said plainly.
    private void DrawSharedStatusNote()
    {
        if (plugin.SimpleHeelsIpc.IsAvailable)
            IconGlyph.WrappedDisabled("SimpleHeels is installed, so your leash and status icons are visible to Oathbound players you're synced with, and theirs to you. This can't be turned off; uninstalling SimpleHeels stops it.");
        else
            IconGlyph.WrappedDisabled("With SimpleHeels installed, your leash and status icons would be visible to Oathbound players you're synced with, and theirs to you.");
    }

    private void DrawLeashLineSection(PluginConfig config)
    {
        using var box = Section.Begin("leashAppearance", "Leash line");
        DrawSharedStatusNote();
        var shown = config.ShowLeashLine;
        DrawOptionRows("leashLineOptions",
            new OptionRow("Show", "Only changes what you see. Hiding it doesn't release the leash.", _ =>
            {
                var showLeash = config.ShowLeashLine;
                if (ImGui.Checkbox("Show leash line", ref showLeash))
                {
                    config.ShowLeashLine = showLeash;
                    config.Save();
                }
            }),
            new OptionRow("Color", "The leash line's colour. Reset also puts brightness and opacity back.", _ =>
            {
                using var disabled = ImRaii.Disabled(!shown);
                var color = config.LeashColor;
                if (ImGui.ColorEdit3("##leashColor", ref color, ImGuiColorEditFlags.NoInputs))
                {
                    config.LeashColor = color;
                    config.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button("Reset##leashAppearance"))
                {
                    config.LeashColor = PluginConfig.DefaultLeashColor;
                    config.LeashBrightness = 1f;
                    config.LeashOpacity = 1f;
                    config.Save();
                }
            }),
            new OptionRow("Brightness", "How bright the line is drawn.", width =>
            {
                using var disabled = ImRaii.Disabled(!shown);
                var brightness = (int)MathF.Round(config.LeashBrightness * 100f);
                ImGui.SetNextItemWidth(Math.Min(width, Scaled(280)));
                if (ImGui.SliderInt("##leashBrightness", ref brightness, (int)(PluginConfig.MinLeashBrightness * 100f), 100, "%d%%"))
                {
                    config.LeashBrightness = brightness / 100f;
                    config.Save();
                }
            }),
            new OptionRow("Opacity", "How see-through the line is.", width =>
            {
                using var disabled = ImRaii.Disabled(!shown);
                var opacity = (int)MathF.Round(config.LeashOpacity * 100f);
                ImGui.SetNextItemWidth(Math.Min(width, Scaled(280)));
                if (ImGui.SliderInt("##leashOpacity", ref opacity, (int)(PluginConfig.MinLeashOpacity * 100f), 100, "%d%%"))
                {
                    config.LeashOpacity = opacity / 100f;
                    config.Save();
                }
            }));
    }

    private bool tieOffTimed;
    private int tieOffMinutes = 10;

    /// Tie off sends a round cage at the Owner's spot; the leash stays engaged under it and pulls again on Untie.
    private void DrawHitchingPostSection(bool canSend)
    {
        if (plugin.Configuration.ActivePairing is not { Direction: PairingDirection.OwnerSide } pairing)
            return;
        using var box = Section.Begin("leashPost", "Hitching post");
        var name = pairing.PeerName ?? "your Sub";
        if (plugin.OwnerCageStatus.HoldingPost(pairing) is { } post)
        {
            IconGlyph.WrappedDisabled($"You tied {name} to a post at {post.PlaceLabel} ({post.Radius:0} yalms of rope).");
            using (ImRaii.Disabled(!canSend))
            {
                if (ImGui.Button("Untie"))
                    ReportCageSend(CageSendAction.TryOpen(plugin));
            }
            IconGlyph.HelpMarker("Opens the post. If they're still on your leash, it picks them up and pulls them after you again.");
            return;
        }

        if (plugin.OwnerStatusEstimates.For(pairing) is not { Leashed: true } estimate)
        {
            IconGlyph.WrappedDisabled($"Leash {name} first, then tie them off here.");
            return;
        }

        IconGlyph.WrappedDisabled($"Ties {name} to a post where you stand, with {estimate.LeashLength:0} yalms of rope. It works like a cage: they can't leave or change their permissions until you untie them. Needs their Cage permission. Panic always frees them.");
        Layout.ItemWidth(120);
        var mode = tieOffTimed ? 1 : 0;
        if (ImGui.Combo("##tieOffLock", ref mode, TieOffLockModes, TieOffLockModes.Length))
            tieOffTimed = mode == 1;
        if (tieOffTimed)
        {
            ImGui.SameLine();
            Layout.ItemWidth(80);
            if (ImGui.InputInt("min##tieOffMinutes", ref tieOffMinutes, 5, 60))
                tieOffMinutes = Math.Clamp(tieOffMinutes, 1, 7 * 24 * 60);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!canSend))
        {
            if (ImGui.Button("Tie off here"))
                ReportCageSend(CageSendAction.TryTieOff(plugin, (int)MathF.Round(estimate.LeashLength), tieOffTimed ? tieOffMinutes * 60 : null));
        }
    }

    private static readonly string[] TieOffLockModes = ["Until untied", "Timed"];

    private void DrawFollowQuickSectionBody(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Follow;

        // Custom follow words saved by an older version are listed so they can be removed.
        using (Section.Begin("followQuickCommands", "Commands"))
        {
            var quickConfig = plugin.Configuration.QuickCommands;
            DrawCommandButtons(canSend,
                new RowCommand("Leash", ControlWords.Leash, FixedActionIds.LeashDefault, "Sent with the options below."),
                new RowCommand("Unleash", ControlWords.Unleash, FixedActionIds.UnleashDefault));

            Section.SubHeading("Leash options");
            // Used wherever the Leash command is sent.
            DrawOptionRows("ownerLeashOptions",
                new OptionRow("Length", "How far your Sub can move from you, in yalms. They may have set a shorter limit.", width =>
                {
                    var leashLength = quickConfig.LeashLengthYalms;
                    ImGui.SetNextItemWidth(Math.Min(width, Scaled(280)));
                    if (ImGui.SliderInt("##ownerLeashLength", ref leashLength, LengthOption.MinYalms, LengthOption.MaxYalms, "%d yalms"))
                    {
                        quickConfig.LeashLengthYalms = Math.Clamp(leashLength, LengthOption.MinYalms, LengthOption.MaxYalms);
                        plugin.Configuration.Save();
                    }
                }),
                new OptionRow("Moodle", "Which moodle goes on your Sub with the leash. \"Sub's default\" uses their own pick.", width =>
                {
                    var leashMoodle = quickConfig.LeashMoodleOverride;
                    ImGui.SetNextItemWidth(Math.Min(width, Scaled(280)));
                    OwnerMoodleOverride.Draw("leash", plugin.Configuration, ref leashMoodle, bare: true);
                    if (leashMoodle != quickConfig.LeashMoodleOverride)
                    {
                        quickConfig.LeashMoodleOverride = leashMoodle;
                        plugin.Configuration.Save();
                    }
                }),
                new OptionRow("In duties", "While your Sub is in a duty, their leash pauses as if you were apart, and picks back up once they leave. Applies from the next time you send Leash.", _ =>
                {
                    var pauseInDuties = quickConfig.PauseLeashInDuties;
                    if (ImGui.Checkbox("Pause the leash##ownerPauseLeashInDuties", ref pauseInDuties))
                    {
                        quickConfig.PauseLeashInDuties = pauseInDuties;
                        plugin.Configuration.Save();
                    }
                }));
        }

        DrawHitchingPostSection(canSend);

        if (quick.Count == 0)
            return;

        using var legacyBox = Section.Begin("followQuickLegacy", "Old custom follow words");
        IconGlyph.WrappedDisabled("Saved by an older version - your Sub's plugin now only understands leash / unleash, so these can be removed.");
        foreach (var cmd in quick.ToArray())
            DrawSavedQuickRow(cmd, quick, canSend);
    }
}
