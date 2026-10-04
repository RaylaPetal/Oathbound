using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;
using Oathbound.Plugin.UI;
using ECommons.Automation;

namespace Oathbound.Plugin.Commands;

/// Applies a Custom Trigger's actions in order through each category's own apply path and permission check.
/// An action whose permission isn't met is skipped; the rest still apply.
public sealed class CustomTriggerCommand
{
    private readonly PluginConfig config;
    private readonly TitleCommand title;
    private readonly OutfitCommand outfit;
    private readonly GestureCommand gesture;
    private readonly MoodlesCommand moodles;
    private readonly RestraintCommand restraints;

    public CustomTriggerCommand(PluginConfig config, TitleCommand title, OutfitCommand outfit, GestureCommand gesture, MoodlesCommand moodles, RestraintCommand restraints)
    {
        this.config = config;
        this.title = title;
        this.outfit = outfit;
        this.gesture = gesture;
        this.moodles = moodles;
        this.restraints = restraints;
    }

    /// Only the restraint action uses `restraintLock`.
    public LocalTestResult Apply(List<CustomTriggerAction> actions, RestraintLock restraintLock = default, Guid? sourcePairingId = null)
    {
        var applied = new List<string>();
        var skipped = new List<string>();
        var effects = config.CustomTriggerEffects;

        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case CustomTriggerActionKind.Title:
                    if (!config.Permissions.Title) { skipped.Add("title (permission)"); break; }
                    title.Apply(new TitleAliasDefinition { Text = action.TitleText, IsPrefix = action.TitleIsPrefix, Color = action.TitleColor, Glow = action.TitleGlow });
                    effects.Title = true;
                    applied.Add("title");
                    break;

                case CustomTriggerActionKind.Outfit:
                    if (!config.Permissions.Outfit) { skipped.Add("outfit (permission)"); break; }
                    // An Owner's ad-hoc bundle only has the design name, so it falls back to the name-based ForceApply.
                    var (outfitOk, _) = action.OutfitDesignId != Guid.Empty
                        ? outfit.Apply(new OutfitAliasDefinition { DesignId = action.OutfitDesignId, DesignName = action.OutfitDesignName, Locked = true })
                        : outfit.ForceApply(action.OutfitDesignName);
                    if (outfitOk)
                    {
                        effects.Outfit = true;
                        applied.Add("outfit");
                    }
                    else
                        skipped.Add("outfit (force-locked, not found, or apply failed)");
                    break;

                case CustomTriggerActionKind.Gesture:
                    if (!(config.Permissions.Gesture && config.TosAcknowledged)) { skipped.Add("gesture (permission/acknowledgement)"); break; }
                    // An Owner's ad-hoc action has no id, so it uses ForceApply's plain name match.
                    var gestureOk = action.GestureId.Length > 0
                        ? gesture.Apply(new GestureAliasDefinition { GestureId = action.GestureId, AnimationName = action.GestureAnimationName }, sourcePairingId)
                        : gesture.ForceApply(action.GestureAnimationName, sourcePairingId);
                    if (gestureOk)
                    {
                        effects.Gesture = true;
                        applied.Add("gesture");
                    }
                    else
                        skipped.Add("gesture (not found or failed to play)");
                    break;

                case CustomTriggerActionKind.Moodle:
                    if (!config.Permissions.Moodles) { skipped.Add("moodle (permission)"); break; }
                    if (moodles.Apply(new MoodlesAliasDefinition { StatusId = action.MoodleStatusId, StatusName = action.MoodleStatusName }))
                    {
                        // Held under the "manual" ledger source so revert releases exactly this status.
                        if ((Guid.TryParse(action.MoodleStatusId, out var statusId) || moodles.TryResolveStatusId(action.MoodleStatusName, out statusId))
                            && !effects.MoodleStatusIds.Contains(statusId))
                            effects.MoodleStatusIds.Add(statusId);
                        applied.Add("moodle");
                    }
                    else
                        skipped.Add("moodle (not found or failed to apply)");
                    break;

                case CustomTriggerActionKind.Restraint:
                    if (!(config.Permissions.Restraints && config.TosAcknowledged)) { skipped.Add("restraint (permission/acknowledgement)"); break; }
                    // Which devices this action added - revert releases exactly these.
                    var activeBefore = restraints.ActiveDeviceIds.ToHashSet();
                    // Apply-only: the bundle is an Owner command, so never route through the Sub's Toggle (refused while force-locked).
                    // A self-contained copy carries its own rules and needs no lookup.
                    var restraintOk = action.RestraintRules is { } inlineRules
                        ? action.RestraintRulesOnly
                            ? restraints.ForceApplyAdHoc(action.RestraintSlot, action.RestraintItemId == 0 ? null : action.RestraintItemId, action.RestraintDeviceName, inlineRules, restraintLock: restraintLock)
                            : restraints.ForceApplyCatalog(action.RestraintCatalogId, action.RestraintItemId, inlineRules, restraintLock: restraintLock)
                        : action.RestraintCatalogId.Length > 0
                        ? restraints.ForceApplyCatalog(action.RestraintCatalogId, action.RestraintItemId,
                            config.RestraintMapping.ConfiguredMods.FirstOrDefault(x => x.CatalogId == action.RestraintCatalogId)?.Rules ?? [], restraintLock: restraintLock)
                        : config.RestraintMapping.Devices.ContainsKey(action.RestraintDeviceId)
                            ? restraints.ForceApplyById(action.RestraintDeviceId, restraintLock)
                            : restraints.ForceApply(action.RestraintDeviceName, restraintLock: restraintLock);
                    if (restraintOk)
                    {
                        foreach (var id in restraints.ActiveDeviceIds.Where(id => !activeBefore.Contains(id) && !effects.RestraintDeviceIds.Contains(id)))
                            effects.RestraintDeviceIds.Add(id);
                        applied.Add($"restraint \"{action.RestraintDeviceName}\"");
                    }
                    else
                        skipped.Add($"restraint \"{action.RestraintDeviceName}\" ({restraints.LastFailureReason ?? "apply failed"})");
                    break;

                case CustomTriggerActionKind.Chat:
                    if (!(config.Permissions.CustomChatMessages && config.CustomChatAcknowledged)) { skipped.Add("chat (permission/acknowledgement)"); break; }
                    if (action.ChatText.Trim().Length > 0)
                    {
                        PluginOutput.RecordChat(action.ChatText);
                        Chat.SendMessage(action.ChatText);
                        applied.Add("chat");
                    }
                    else
                    {
                        skipped.Add("chat (no text configured)");
                    }
                    break;
            }
        }

        config.Save();
        var skippedSuffix = skipped.Count > 0 ? $" (skipped: {string.Join(", ", skipped)})" : "";
        return applied.Count > 0
            ? LocalTestResult.Ok($"Applied: {string.Join(", ", applied)}{skippedSuffix}")
            : LocalTestResult.Fail($"Nothing applied{skippedSuffix}");
    }

    /// Undoes only what Custom Triggers applied since the last revert. Sent chat can't be undone.
    public LocalTestResult RevertEffects()
    {
        var effects = config.CustomTriggerEffects;
        if (!effects.Any)
            return LocalTestResult.Fail("Nothing to revert - no Custom Trigger effects are active.");

        var done = new List<string>();
        var failed = new List<string>();
        void Step(string name, bool applies, Action action)
        {
            if (!applies) return;
            try { action(); done.Add(name); }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"customtrigger revert: {name} step failed - continuing.");
                failed.Add(name);
            }
        }

        // Restraints first: releasing their slot lock can hand a slot back to the outfit.
        Step("restraints", effects.RestraintDeviceIds.Count > 0, () => restraints.ReleaseDevices(effects.RestraintDeviceIds));
        Step("outfit", effects.Outfit, () => outfit.RevertToBase());
        Step("title", effects.Title, title.ForceClear);
        Step("animation", effects.Gesture, gesture.ResetActiveTemporary);
        Step("moodles", effects.MoodleStatusIds.Count > 0, () =>
        {
            foreach (var statusId in effects.MoodleStatusIds)
                moodles.Ledger.Release(AttachedMoodleLedger.ManualSource(statusId));
        });

        ForgetEffects();
        var summary = $"Reverted Custom Trigger effects: {string.Join(", ", done)}.";
        return failed.Count == 0 ? LocalTestResult.Ok(summary) : LocalTestResult.Fail($"{summary} Failed: {string.Join(", ", failed)}.");
    }

    /// The effects are already gone (revert all, panic), so a later revert never undoes something newer.
    public void ForgetEffects()
    {
        config.CustomTriggerEffects = new CustomTriggerEffectsState();
        config.Save();
    }

    /// Each non-chat action is one `kind=value` segment joined by ';'. Free text is base64 so it can't collide with the
    /// delimiters. A chat action is always last and takes the rest of the line. At most one action per kind.
    public static string BuildCastCommand(string label, List<CustomTriggerAction> actions)
    {
        var segments = new List<string>();
        string? chatSegment = null;

        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case CustomTriggerActionKind.Title:
                    // Glow is an optional 4th part, so a glow-less action still parses on an older Sub's strict 3-part check.
                    var titleSegment = $"title={EncodeText(action.TitleText)}|{(action.TitleIsPrefix ? 1 : 0)}|{FormatColor(action.TitleColor)}";
                    if (action.TitleGlow is { } glow)
                        titleSegment += $"|{FormatColor(glow)}";
                    segments.Add(titleSegment);
                    break;
                case CustomTriggerActionKind.Outfit:
                    segments.Add($"outfit={action.OutfitDesignId}|{EncodeText(action.OutfitDesignName)}");
                    break;
                case CustomTriggerActionKind.Gesture:
                    segments.Add($"gesture={action.GestureId}|{EncodeText(action.GestureAnimationName)}");
                    break;
                case CustomTriggerActionKind.Moodle:
                    segments.Add($"moodle={action.MoodleStatusId}|{EncodeText(action.MoodleStatusName)}");
                    break;
                case CustomTriggerActionKind.Restraint:
                    if (action.RestraintRules is { } rules)
                    {
                        // Self-contained: the rules travel inline. An older Sub's strict 2-part check rejects the bundle (fails closed).
                        var reference = action.RestraintRulesOnly
                            ? $"wear:{action.RestraintSlot?.ToString() ?? "-"}:{(action.RestraintItemId == 0 ? "-" : action.RestraintItemId.ToString())}"
                            : $"catalog:{action.RestraintCatalogId}:{action.RestraintItemId}";
                        segments.Add($"restraint={reference}|{EncodeText(action.RestraintDeviceName)}|{EncodeText(RestraintCommand.EncodeRuleTokens(rules))}");
                        break;
                    }
                    segments.Add(action.RestraintCatalogId.Length > 0
                        ? $"restraint=catalog:{action.RestraintCatalogId}:{action.RestraintItemId}|{EncodeText(action.RestraintDeviceName)}"
                        : $"restraint={action.RestraintDeviceId}|{EncodeText(action.RestraintDeviceName)}");
                    break;
                case CustomTriggerActionKind.Chat:
                    chatSegment = $"chat={action.ChatText}";
                    break;
            }
        }

        if (chatSegment is not null)
            segments.Add(chatSegment);

        return $"customtrigger cast \"{label}\" {string.Join(';', segments)}";
    }

    /// Fails closed on any malformed segment, unknown kind, or an empty bundle.
    public static bool TryParseCastCommand(string remainder, out string label, out List<CustomTriggerAction> actions)
    {
        label = "";
        actions = new List<CustomTriggerAction>();

        var trimmed = remainder.Trim();
        if (!trimmed.StartsWith('"'))
            return false;

        var closing = trimmed.IndexOf('"', 1);
        if (closing < 0)
            return false;

        label = trimmed[1..closing];
        if (label.Length == 0)
            return false;

        var tail = trimmed[(closing + 1)..].Trim();
        if (tail.Length == 0)
            return false;

        SplitChatTail(tail, out var beforeChat, out var chatText);

        if (beforeChat.Length > 0)
        {
            foreach (var segment in beforeChat.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = segment.IndexOf('=');
                if (eq < 0)
                    return false;

                var kind = segment[..eq];
                var value = segment[(eq + 1)..];
                var parts = value.Split('|');

                switch (kind.ToLowerInvariant())
                {
                    case "title":
                        // 3 parts is the legacy shape; a 4th carries an optional glow.
                        if ((parts.Length != 3 && parts.Length != 4) || !TryDecodeText(parts[0], out var titleText) || titleText.Length == 0)
                            return false;
                        if (!int.TryParse(parts[1], out var prefixFlag))
                            return false;
                        if (!TryParseColor(parts[2], out var color))
                            return false;
                        Vector3? glow = null;
                        if (parts.Length == 4 && parts[3].Length > 0)
                        {
                            if (!TryParseColor(parts[3], out var g))
                                return false;
                            glow = g;
                        }
                        actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Title, TitleText = titleText, TitleIsPrefix = prefixFlag != 0, TitleColor = color, TitleGlow = glow });
                        break;

                    case "outfit":
                        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var designId) || !TryDecodeText(parts[1], out var designName) || designName.Length == 0)
                            return false;
                        actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Outfit, OutfitDesignId = designId, OutfitDesignName = designName });
                        break;

                    // Ids may be empty: the Owner's editor only knows names, and Apply falls back to a name match.
                    case "gesture":
                        if (parts.Length != 2 || !TryDecodeText(parts[1], out var animName) || animName.Length == 0)
                            return false;
                        actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Gesture, GestureId = parts[0], GestureAnimationName = animName });
                        break;

                    case "moodle":
                        if (parts.Length != 2 || !TryDecodeText(parts[1], out var statusName) || statusName.Length == 0)
                            return false;
                        actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Moodle, MoodleStatusId = parts[0], MoodleStatusName = statusName });
                        break;

                    case "restraint":
                        if ((parts.Length != 2 && parts.Length != 3) || !TryDecodeText(parts[1], out var deviceName) || deviceName.Length == 0)
                            return false;
                        if (parts.Length == 3)
                        {
                            if (parts[0].Length == 0)
                                return false;
                            if (!TryDecodeText(parts[2], out var ruleTokens))
                                return false;
                            var inlineRules = RestraintCommand.DecodeRuleTokens(ruleTokens);
                            if (inlineRules.Count == 0)
                                return false;
                            var reference = parts[0].Split(':');
                            if (reference.Length != 3)
                                return false;
                            if (reference[0].Equals("catalog", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!ulong.TryParse(reference[2], out var inlineItemId) || inlineItemId == 0) return false;
                                actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Restraint, RestraintCatalogId = reference[1], RestraintItemId = inlineItemId, RestraintDeviceName = deviceName, RestraintRules = inlineRules });
                            }
                            else if (reference[0].Equals("wear", StringComparison.OrdinalIgnoreCase))
                            {
                                Glamourer.Api.Enums.ApiEquipSlot? wearSlot = null;
                                if (reference[1] != "-")
                                {
                                    if (!Enum.TryParse<Glamourer.Api.Enums.ApiEquipSlot>(reference[1], out var parsedSlot)) return false;
                                    wearSlot = parsedSlot;
                                }
                                ulong wearItem = 0;
                                if (reference[2] != "-" && !ulong.TryParse(reference[2], out wearItem)) return false;
                                actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Restraint, RestraintDeviceName = deviceName, RestraintRules = inlineRules, RestraintRulesOnly = true, RestraintSlot = wearSlot, RestraintItemId = wearItem });
                            }
                            else
                            {
                                return false;
                            }
                            break;
                        }
                        if (parts[0].StartsWith("catalog:", StringComparison.OrdinalIgnoreCase))
                        {
                            var catalogParts = parts[0].Split(':');
                            if (catalogParts.Length != 3 || !ulong.TryParse(catalogParts[2], out var itemId) || itemId == 0) return false;
                            actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Restraint, RestraintCatalogId = catalogParts[1], RestraintItemId = itemId, RestraintDeviceName = deviceName });
                        }
                        else
                            actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Restraint, RestraintDeviceId = parts[0], RestraintDeviceName = deviceName });
                        break;

                    default:
                        return false;
                }
            }
        }

        if (chatText is { Length: > 0 })
            actions.Add(new CustomTriggerAction { Kind = CustomTriggerActionKind.Chat, ChatText = chatText });

        return actions.Count > 0;
    }

    private const string CastPrefix = "customtrigger cast ";

    /// Splits a bundle too long for one message into one single-action `cast` per action. Null if not a cast command.
    public static List<string>? SplitCastCommand(string command)
    {
        const string categoryWord = "customtrigger ";
        const string castWord = "cast ";
        var trimmed = command.Trim();
        if (!trimmed.StartsWith(categoryWord, StringComparison.OrdinalIgnoreCase))
            return null;

        // Every part carries the lock option, so whichever part holds the restraint still locks.
        var afterCategory = LockTimerOption.Strip(trimmed[categoryWord.Length..], out var restraintLock).TrimStart();
        if (!afterCategory.StartsWith(castWord, StringComparison.OrdinalIgnoreCase))
            return null;

        var remainder = afterCategory[castWord.Length..].Trim();
        if (!TryParseCastCommand(remainder, out var label, out _))
            return null;

        SplitChatTail(remainder[(remainder.IndexOf('"', 1) + 1)..].Trim(), out var beforeChat, out var chatText);
        var head = $"{CastPrefix}\"{label}\" ";
        var parts = beforeChat.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(segment => head + segment).ToList();
        if (chatText is { Length: > 0 })
            parts.Add($"{head}chat={chatText}");
        return parts.Select(part => LockTimerOption.Insert(part, restraintLock)).ToList();
    }

    private static void SplitChatTail(string tail, out string beforeChat, out string? chatText)
    {
        chatText = null;
        if (tail.StartsWith("chat=", StringComparison.OrdinalIgnoreCase))
        {
            beforeChat = "";
            chatText = tail["chat=".Length..];
            return;
        }

        var chatMarker = tail.IndexOf(";chat=", StringComparison.OrdinalIgnoreCase);
        if (chatMarker >= 0)
        {
            beforeChat = tail[..chatMarker];
            chatText = tail[(chatMarker + ";chat=".Length)..];
        }
        else
        {
            beforeChat = tail;
        }
    }

    /// Shared by the Sub's UI and the catalog export so both describe a trigger identically.
    public static string Summarize(CustomTriggerAction a) => CommandPresentation.Action(a);

    private static string EncodeText(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static bool TryDecodeText(string encoded, out string text)
    {
        text = "";
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string FormatColor(Vector3 color) =>
        $"{color.X.ToString(CultureInfo.InvariantCulture)},{color.Y.ToString(CultureInfo.InvariantCulture)},{color.Z.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryParseColor(string token, out Vector3 color)
    {
        color = new Vector3(1, 1, 1);
        var parts = token.Split(',');
        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return false;

        color = new Vector3(r, g, b);
        return true;
    }
}
